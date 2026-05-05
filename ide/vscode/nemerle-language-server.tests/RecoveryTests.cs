using FluentAssertions;
using System.Text.Json.Nodes;
using Nemerle.LanguageServer.Tests.Infrastructure;
using Xunit;

namespace Nemerle.LanguageServer.Tests;

/// <summary>
/// Tests that verify the server handles compiler internal errors (Recovery, etc.)
/// gracefully — no crashes, diagnostics contain the error, hover still works.
/// </summary>
public class RecoveryTests : IAsyncLifetime
{
    private ServerHarness _harness = null!;
    private string _workspaceDir = null!;

    public async Task InitializeAsync()
    {
        _harness = new ServerHarness();
        _workspaceDir = Path.Combine(Path.GetTempPath(), "nemerle-lsp-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workspaceDir);

        await File.WriteAllTextAsync(Path.Combine(_workspaceDir, "TestProject.nproj"), """
<Project>
  <PropertyGroup>
    <OutputType>Library</OutputType>
    <AssemblyName>TestProject</AssemblyName>
    <NoStdLib>true</NoStdLib>
    <TargetFramework>netstandard2.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup><Compile Include="*.n" /></ItemGroup>
</Project>
""");

        var rootUri = "file:///" + _workspaceDir.Replace('\\', '/');
        await _harness.InitializeAsync(rootUri);
    }

    public async Task DisposeAsync()
    {
        try { await _harness.DisposeAsync(); } catch { }
        try { Directory.Delete(_workspaceDir, true); } catch { }
    }

    [Fact]
    public async Task Unknown_assembly_using_produces_InternalError_not_crash()
    {
        // Mimics opening VscodeTestApp\Program.n without VscodeTestLib reference.
        // The compiler throws Recovery when it can't resolve 'using UnknownLib;'.
        var uri = $"file:///{_workspaceDir.Replace('\\', '/')}/recovery.n";

        await _harness.SendDidOpenAsync(uri, """
using UnknownLib;

module M { def x = 42; }
""");

        var diags = await _harness.WaitForDiagnosticsAsync(TimeSpan.FromSeconds(10));
        diags.Should().NotBeNull("should receive publishDiagnostics even on Recovery");

        var diagArray = diags!["diagnostics"]!.AsArray();
        diagArray.Should().NotBeEmpty("must report the Recovery error");

        // The error should contain "Recovery" or "Internal error"
        var errMsgs = diagArray.Select(d => d!["message"]!.GetValue<string>()).ToList();
        errMsgs.Should().Contain(m => m.Contains("Recovery") || m.Contains("Internal error"),
            "Recovery exception must be surfaced as a diagnostic, not swallowed");
    }

    [Fact]
    public async Task Hover_still_works_despite_Recovery_error()
    {
        // Even when compilation fails with Recovery, hover should return something
        // (the lexical fallback), not crash.
        var uri = $"file:///{_workspaceDir.Replace('\\', '/')}/hover_recovery.n";

        await _harness.SendDidOpenAsync(uri, """
using MissingLib;

def answer = 42;
""");

        // Wait for initial diagnostics
        await _harness.WaitForDiagnosticsAsync(TimeSpan.FromSeconds(5));

        // Hover on "answer" — must not crash
        var result = await _harness.SendRequestAsync("textDocument/hover", new
        {
            textDocument = new { uri },
            position = new { line = 3, character = 5 } // on "answer"
        });

        result.Should().NotBeNull("hover should succeed even when compilation has Recovery errors");

        // Should have markdown content
        var contents = result!["contents"];
        if (contents != null)
        {
            var md = contents.GetValue<JsonObject>();
            var kind = md["kind"]?.GetValue<string>();
            var value = md["value"]?.GetValue<string>();
            // Either engine or lexical fallback should return something
            (kind == "markdown" && !string.IsNullOrEmpty(value)).Should().BeTrue(
                $"hover should return markdown, got kind={kind}");
        }
        // If contents is null/primitive, it's still valid (empty hover)
    }

    [Fact]
    public async Task ProjectReference_resolved_to_DLL_prevents_Recovery()
    {
        // Create a temp workspace simulating two projects:
        //   lib/Lib.nproj  → builds Lib.dll  (we create a fake DLL)
        //   app/App.nproj  → ProjectReference to ../lib/Lib.nproj
        //   app/test.n     → using Lib;
        // Without project reference resolution, this would throw Recovery (assembly not found).
        // With the fix, the compiler finds Lib.dll and doesn't throw Recovery.

        var ws = Path.Combine(Path.GetTempPath(), "nemerle-lsp-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(ws);

        try
        {
            // Create lib project
            var libDir = Path.Combine(ws, "lib");
            Directory.CreateDirectory(libDir);
            await File.WriteAllTextAsync(Path.Combine(libDir, "Lib.nproj"), """
<Project>
  <PropertyGroup>
    <OutputType>Library</OutputType>
    <AssemblyName>TestLib</AssemblyName>
    <NoStdLib>true</NoStdLib>
    <TargetFramework>netstandard2.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup><Compile Include="*.n" /></ItemGroup>
</Project>
""");
            // Fake the built DLL
            var libDllDir = Path.Combine(libDir, "bin", "Release");
            Directory.CreateDirectory(libDllDir);
            await File.WriteAllTextAsync(Path.Combine(libDllDir, "TestLib.dll"), "");

            // Create app project with ProjectReference
            var appDir = Path.Combine(ws, "app");
            Directory.CreateDirectory(appDir);
            await File.WriteAllTextAsync(Path.Combine(appDir, "App.nproj"), """
<Project>
  <PropertyGroup>
    <OutputType>Library</OutputType>
    <AssemblyName>TestApp</AssemblyName>
    <NoStdLib>true</NoStdLib>
    <TargetFramework>netstandard2.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="*.n" />
    <ProjectReference Include="..\lib\Lib.nproj">
      <Name>TestLib</Name>
    </ProjectReference>
  </ItemGroup>
</Project>
""");

            var rootUri = "file:///" + ws.Replace('\\', '/');
            var harness = new ServerHarness();
            await harness.InitializeAsync(rootUri);

            try
            {
                var uri = $"file:///{ws.Replace('\\', '/')}/app/test.n";

                await harness.SendDidOpenAsync(uri, """
using TestLib;

def answer = 42;
""");

                var diags = await harness.WaitForDiagnosticsAsync(TimeSpan.FromSeconds(10));
                diags.Should().NotBeNull();

                var diagArray = diags!["diagnostics"]!.AsArray();
                // Should NOT contain Recovery error — the project reference was resolved
                var msgs = diagArray.Select(d => d!["message"]!.GetValue<string>()).ToList();
                msgs.Should().NotContain(m => m.Contains("Recovery"),
                    "resolved ProjectReference should prevent Recovery exception");

                // If TestLib.dll is fake (empty), the compiler might complain about it
                // being a bad assembly — that's OK, it's not Recovery.
            }
            finally
            {
                await harness.DisposeAsync();
            }
        }
        finally
        {
            try { Directory.Delete(ws, true); } catch { }
        }
    }

    [Fact]
    public void NprojLoader_resolves_project_reference_paths()
    {
        // Unit test: verify NprojLoader.ResolveProjectReferences computes correct paths.
        var dir = Path.Combine(Path.GetTempPath(), "nemerle-lsp-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);

        try
        {
            var nprojPath = Path.Combine(dir, "Test.nproj");
            File.WriteAllText(nprojPath, """
<Project>
  <PropertyGroup>
    <OutputType>Library</OutputType>
    <AssemblyName>MyLib</AssemblyName>
    <TargetFramework>netstandard2.0</TargetFramework>
    <NoStdLib>true</NoStdLib>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="*.n" />
    <ProjectReference Include="ref\RefLib.nproj">
      <Name>RefLib</Name>
    </ProjectReference>
    <MacroProjectReference Include="macros\MacroLib.nproj">
      <Name>MacroLib</Name>
    </MacroProjectReference>
  </ItemGroup>
</Project>
""");

            // Create the referenced .nproj files and their fake output DLLs
            var refProjDir = Path.Combine(dir, "ref");
            Directory.CreateDirectory(refProjDir);
            File.WriteAllText(Path.Combine(refProjDir, "RefLib.nproj"), """
<Project>
  <PropertyGroup>
    <OutputType>Library</OutputType>
    <AssemblyName>RefLib</AssemblyName>
    <TargetFramework>netstandard2.0</TargetFramework>
    <NoStdLib>true</NoStdLib>
  </PropertyGroup>
</Project>
""");
            var refDllDir = Path.Combine(refProjDir, "bin", "Release");
            Directory.CreateDirectory(refDllDir);
            File.WriteAllText(Path.Combine(refDllDir, "RefLib.dll"), "");

            var macroProjDir = Path.Combine(dir, "macros");
            Directory.CreateDirectory(macroProjDir);
            File.WriteAllText(Path.Combine(macroProjDir, "MacroLib.nproj"), """
<Project>
  <PropertyGroup>
    <OutputType>Library</OutputType>
    <AssemblyName>MacroLib</AssemblyName>
    <TargetFramework>netstandard2.0</TargetFramework>
    <NoStdLib>true</NoStdLib>
  </PropertyGroup>
</Project>
""");
            var macroDllDir = Path.Combine(macroProjDir, "bin", "Release");
            Directory.CreateDirectory(macroDllDir);
            File.WriteAllText(Path.Combine(macroDllDir, "MacroLib.dll"), "");

            var info = Nemerle.LanguageServer.ProjectSystem.NprojLoader.Load(nprojPath);

            // Verify parsed paths
            info.ProjectReferences.Should().ContainSingle()
                .Which.Should().EndWith("RefLib.nproj");
            info.MacroProjectReferences.Should().ContainSingle()
                .Which.Should().EndWith("MacroLib.nproj");

            var (assemblies, macros) = Nemerle.LanguageServer.ProjectSystem.NprojLoader.ResolveProjectReferences(info);

            assemblies.Should().ContainSingle()
                .Which.Should().EndWith($"ref{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}RefLib.dll");
            macros.Should().ContainSingle()
                .Which.Should().EndWith($"macros{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}MacroLib.dll");
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }
}
