using FluentAssertions;
using System.Text.Json.Nodes;
using Nemerle.LanguageServer.Tests.Infrastructure;
using Xunit;

namespace Nemerle.LanguageServer.Tests;

public class IntegrationTests : IAsyncLifetime
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
    public async Task Change_text_updates_diagnostics()
    {
        var uri = $"file:///{_workspaceDir.Replace('\\', '/')}/live.n";

        await _harness.SendNotificationAsync("textDocument/didOpen", new
        {
            textDocument = new { uri, languageId = "nemerle", version = 1, text = "def x = 42;" }
        });

        var firstDiags = await _harness.WaitForDiagnosticsAsync(TimeSpan.FromSeconds(5));
        firstDiags.Should().NotBeNull();

        await _harness.SendNotificationAsync("textDocument/didChange", new
        {
            textDocument = new { uri, version = 2 },
            contentChanges = new[] { new { text = "def x = undefinedVar;" } }
        });

        var secondDiags = await _harness.WaitForDiagnosticsAsync(TimeSpan.FromSeconds(5));
        secondDiags.Should().NotBeNull();
        var diagArray = secondDiags!["diagnostics"]!.AsArray();
        // After change, we should get diagnostics (at least an internal error)
        diagArray.Should().NotBeEmpty("changing to invalid code should produce diagnostics");
    }

    [Fact]
    public async Task Completion_after_dot_in_module()
    {
        var uri = $"file:///{_workspaceDir.Replace('\\', '/')}/completion.n";

        await _harness.SendDidOpenAsync(uri, "module M { def foo() { } def bar() { foo" );

        var result = await _harness.SendRequestAsync("textDocument/completion", new
        {
            textDocument = new { uri },
            position = new { line = 0, character = 37 }
        });

        result.Should().NotBeNull();
        var items = result!["items"]?.AsArray();
        items.Should().NotBeNull();
        items!.Should().Contain(i => i!["label"]!.GetValue<string>() == "foo");
    }

    [Fact]
    public async Task Multiple_documents_in_workspace()
    {
        var uri1 = $"file:///{_workspaceDir.Replace('\\', '/')}/file1.n";
        var uri2 = $"file:///{_workspaceDir.Replace('\\', '/')}/file2.n";

        await _harness.SendDidOpenAsync(uri1, "def x = 1;");
        var d1 = await _harness.WaitForDiagnosticsAsync(TimeSpan.FromSeconds(5));
        d1.Should().NotBeNull();

        await _harness.SendDidOpenAsync(uri2, "def y = x;  // error: x not in scope");
        var d2 = await _harness.WaitForDiagnosticsAsync(TimeSpan.FromSeconds(5));
        d2.Should().NotBeNull();

        // Both documents should receive diagnostics
        d2!["diagnostics"]!.AsArray().Should().NotBeEmpty();
    }

    [Fact]
    public async Task Server_handles_rapid_document_changes()
    {
        var uri = $"file:///{_workspaceDir.Replace('\\', '/')}/rapid.n";

        // Send 5 rapid changes
        for (int i = 0; i < 5; i++)
        {
            var text = i switch
            {
                0 => "def x = 1;\n",
                1 => "def x = 1;\ndef y = 2;\n",
                2 => "def x = 1;\ndef y = 2;\ndef z = x + y;\n",
                3 => "def x = 1;\n// comment\n",
                _ => "def final = 42;\n"
            };

            if (i == 0)
            {
                await _harness.SendNotificationAsync("textDocument/didOpen", new
                {
                    textDocument = new { uri, languageId = "nemerle", version = i + 1, text }
                });
            }
            else
            {
                await _harness.SendNotificationAsync("textDocument/didChange", new
                {
                    textDocument = new { uri, version = i + 1 },
                    contentChanges = new[] { new { text } }
                });
            }

            await Task.Delay(200); // Let server catch up
        }

        var diags = await _harness.WaitForDiagnosticsAsync(TimeSpan.FromSeconds(5));
        diags.Should().NotBeNull("server should not crash under rapid changes");
    }

    [Fact]
    public async Task Completion_includes_stdlib_keywords()
    {
        var uri = $"file:///{_workspaceDir.Replace('\\', '/')}/std.n";

        await _harness.SendDidOpenAsync(uri, "cl" );

        var result = await _harness.SendRequestAsync("textDocument/completion", new
        {
            textDocument = new { uri },
            position = new { line = 0, character = 2 }
        });

        var items = result!["items"]?.AsArray();
        items.Should().NotBeNull();
        // "cl" should complete to "class"
        items!.Should().Contain(i => i!["label"]!.GetValue<string>() == "class");
    }
}
