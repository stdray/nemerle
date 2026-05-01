//////////////////////////////////////////////////////////////////////
// ARGUMENTS
//////////////////////////////////////////////////////////////////////
var target        = Argument("target", "Default");
var configuration = Argument("configuration", "Release");
var nccBoot       = Argument("nccBoot", "boot-dnlib");
var netCoreVersion = Argument("netCoreVersion", "2.1.0");

//////////////////////////////////////////////////////////////////////
// TASKS
//////////////////////////////////////////////////////////////////////

//////////////////////////////////////////////////////////////////////
// HELPERS
//////////////////////////////////////////////////////////////////////

// Discover .NET Core 2.1 runtime directory dynamically
string FindNetCore21Runtime()
{
    // Try DOTNET_ROOT first, then standard locations
    var dotnetRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT");
    if (string.IsNullOrEmpty(dotnetRoot))
        dotnetRoot = Environment.GetEnvironmentVariable("ProgramFiles") + "/dotnet";
    if (!DirectoryExists(dotnetRoot))
        dotnetRoot = @"C:/Program Files/dotnet";

    var sharedDir = dotnetRoot + "/shared/Microsoft.NETCore.App";
    if (!DirectoryExists(sharedDir))
        throw new Exception($"Cannot find .NET runtime shared directory: {sharedDir}");

    // Find highest matching runtime version
    var majorMinor = netCoreVersion.Substring(0, netCoreVersion.LastIndexOf('.'));
    var searchPattern = $"{majorMinor}.*";
    var dirs = System.IO.Directory.GetDirectories(sharedDir, searchPattern);
    if (dirs.Length == 0)
        throw new Exception($"No .NET Core {majorMinor} runtime found! Install via dotnet-install script.");

    System.Array.Sort(dirs);
    var latest = dirs[dirs.Length - 1].Replace('\\', '/');
    Information($"  Found .NET Core 2.1 runtime: {latest}");
    return latest;
}


Task("Clean")
    .Does(() =>
{
    Information("Cleaning all build artifacts...");
    var dirsToClean = new[] { "bin", "obj" };
    foreach (var dir in dirsToClean)
    {
        if (DirectoryExists(dir))
        {
            DeleteDirectory(dir, new DeleteDirectorySettings { Recursive = true });
            Information($"  Deleted {dir}/");
        }
    }
    Information("Clean completed.");
});

Task("FixBoot")
    .IsDependentOn("Clean")
    .Does(() =>
{
    Information("Fixing boot-dnlib compiler...");

    // 1. Create ncc.runtimeconfig.json
    var runtimeConfig = $@"{{
  ""runtimeOptions"": {{
    ""framework"": {{
      ""name"": ""Microsoft.NETCore.App"",
      ""version"": ""{netCoreVersion}"",
      ""rollForward"": ""LatestPatch""
    }}
  }}
}}";
    System.IO.File.WriteAllText($"{nccBoot}/ncc.runtimeconfig.json", runtimeConfig);
    Information("  Created ncc.runtimeconfig.json");

    // 2. Ensure System.Security.Permissions.dll exists
    var permsDll = $"{nccBoot}/System.Security.Permissions.dll";
    if (!FileExists(permsDll))
    {
        Information("  Building System.Security.Permissions shim...");
        var shimDir = "tmp_shim";
        EnsureDirectoryExists(shimDir);

        System.IO.File.WriteAllText($"{shimDir}/shim.csproj",
@"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>netstandard2.0</TargetFramework>
    <AssemblyName>System.Security.Permissions</AssemblyName>
  </PropertyGroup>
</Project>");
        System.IO.File.WriteAllText($"{shimDir}/SecurityAttribute.cs",
@"namespace System.Security.Permissions {
    public class SecurityAttribute : System.Attribute {
        public SecurityAttribute(System.Security.Permissions.SecurityAction action) { }
        public bool Unrestricted { get; set; }
    }
    public enum SecurityAction { Demand, Assert, Deny, PermitOnly, LinkDemand, InheritanceDemand, RequestMinimum, RequestOptional, RequestRefuse }
}");
        var msBuildSettings = new DotNetMSBuildSettings();
        msBuildSettings.SetConfiguration(configuration);
        DotNetBuild(shimDir, new DotNetBuildSettings {
            MSBuildSettings = msBuildSettings
        });
        CopyFile($"{shimDir}/bin/{configuration}/netstandard2.0/System.Security.Permissions.dll", permsDll);
        Information("  System.Security.Permissions.dll created");
    }
    else
    {
        Information("  System.Security.Permissions.dll already exists");
    }
});

Task("BuildTasks")
    .IsDependentOn("FixBoot")
    .Does(() =>
{
    Information("Building MSBuild Tasks...");
    var msBuildSettings = new DotNetMSBuildSettings();
    msBuildSettings.SetConfiguration(configuration);
    DotNetBuild("Nemerle.MSBuild.Tasks.csproj", new DotNetBuildSettings {
        MSBuildSettings = msBuildSettings
    });
    try { CopyFile($"bin/{configuration}/Nemerle.MSBuild.Tasks.dll", $"{nccBoot}/Nemerle.MSBuild.Tasks.dll"); }
    catch { Warning("  MSBuild Tasks DLL locked — using committed version in boot-dnlib"); }
    Information("  MSBuild Tasks built.");
});

Task("PrepareSdk")
    .IsDependentOn("FixBoot")
    .Does(() =>
{
    Information("Preparing SDK files in boot-dnlib...");
    var sdkDir = "tools/msbuild-task";
    CopyFile($"{sdkDir}/Nemerle.Sdk.props",  $"{nccBoot}/Nemerle.Sdk.props");
    CopyFile($"{sdkDir}/Nemerle.Sdk.targets", $"{nccBoot}/Nemerle.Sdk.targets");
    // Targets file is patched manually in boot-dnlib already
    Information("  SDK files ready.");
});

Task("Stage1")
    .IsDependentOn("BuildTasks")
    .IsDependentOn("PrepareSdk")
    .Does(() =>
{
    Information("=== STAGE 1: Building with boot compiler ===");

    void BuildNproj(string nproj)
    {
        Information($"  Building {nproj}...");
        // Restore
        var restoreSettings = new DotNetRestoreSettings {
            ArgumentCustomization = args => args.Append($"/p:Nemerle={nccBoot}")
        };
        DotNetRestore(nproj, restoreSettings);
        // Build
        var msBuildSettings = new DotNetMSBuildSettings();
        msBuildSettings.SetConfiguration(configuration);
        msBuildSettings.WithProperty("Nemerle", nccBoot);
        DotNetBuild(nproj, new DotNetBuildSettings {
            MSBuildSettings = msBuildSettings
        });
    }

    BuildNproj("Nemerle.nproj");
    BuildNproj("Nemerle.Compiler.nproj");
    BuildNproj("Nemerle.Macros.nproj");

    // ncc-core: direct compiler invocation (bypasses MSBuild/TF issues)
    Information("  Building ncc-core.exe...");
    var nccRt = FindNetCore21Runtime();
    var nccArgs = $"\"{nccBoot}/ncc.exe\" ncc/main.n ncc/shared/AssemblyInfo.n " +
        $"-r \"{nccBoot}/System.Security.Permissions.dll\" " +
        $"-r \"{nccBoot}/Nemerle.Compiler.dll\" " +
        $"-r \"{nccBoot}/Nemerle.Macros.dll\" " +
        $"-r \"{nccRt}/System.Console.dll\" " +
        $"-r \"{nccRt}/System.Runtime.Extensions.dll\" " +
        $"-r \"{nccRt}/System.Threading.Thread.dll\" " +
        $"-r \"{nccRt}/System.IO.FileSystem.dll\" " +
        $"-t exe -o bin/{configuration}/ncc-core.exe";
    var exitCode = StartProcess("dotnet", nccArgs);
    if (exitCode != 0)
        throw new Exception($"ncc-core build failed with exit code {exitCode}");

    CopyFile($"{nccBoot}/dnlib.dll", $"bin/{configuration}/dnlib.dll");
    CopyFile($"{nccBoot}/System.Security.Permissions.dll", $"bin/{configuration}/System.Security.Permissions.dll");

    // Create runtimeconfig.json for ncc-core.exe
    var nccRtConfig =
@"{{
  ""runtimeOptions"": {{
    ""framework"": {{
      ""name"": ""Microsoft.NETCore.App"",
      ""version"": ""{netCoreVersion}"",
      ""rollForward"": ""LatestPatch""
    }}
  }}
}}";
    System.IO.File.WriteAllText($"bin/{configuration}/ncc-core.runtimeconfig.json", nccRtConfig);
    Information("  Created ncc-core.runtimeconfig.json");
    Information("=== Stage 1 complete! ===");
});

Task("Stage1b")
    .IsDependentOn("Stage1")
    .Does(() =>
{
    Information("=== STAGE 1b: Building test runner ===");

    var nccRt = FindNetCore21Runtime();
    var ncc = $"dotnet {nccBoot}/ncc.exe";
    var sec = $"-r {nccBoot}/System.Security.Permissions.dll";
    var outDir = "snippets/Nemerle.Test/Nemerle.Compiler.Test/bin/Release";
    var st1 = $"bin/{configuration}";

    // Copy Stage1 libs + runtimeconfig to test runner dir (for HostedNcc)
    EnsureDirectoryExists(outDir);
    CopyFile($"{st1}/Nemerle.dll", $"{outDir}/Nemerle.dll");
    CopyFile($"{st1}/Nemerle.Compiler.dll", $"{outDir}/Nemerle.Compiler.dll");
    CopyFile($"{st1}/Nemerle.Macros.dll", $"{outDir}/Nemerle.Macros.dll");
    CopyFile($"{nccBoot}/System.Security.Permissions.dll", $"{outDir}/System.Security.Permissions.dll");

    // Build test framework
    Information("  Building Nemerle.Test.Framework.dll...");
    var fwSrc = new[] {
        "ColorizedOutputWriter", "DefaultColorizedOutputWriter", "ExecutionListener",
        "IRunner", "MulticastExecutionListener", "Result", "Runner", "Statistics",
        "TeamCityExecutionListener", "Test", "ThreadRunner", "UnixColorizedOutputWriter",
        "VisualStudioExecutionListener", "Utils/FileSearcher", "Properties/AssemblyInfo"
    };
    var fwFiles = string.Join(" ", fwSrc.Select(f => $"snippets/Nemerle.Test/Nemerle.Test.Framework/{f}.n"));
    StartProcess("dotnet", $"{nccBoot}/ncc.exe {fwFiles} {sec} -r {st1}/Nemerle.dll -t library -o {outDir}/Nemerle.Test.Framework.dll");

    // Build test runner
    Information("  Building Nemerle.Compiler.Test.dll...");
    var trSrc = new[] {
        "DefaultProcessStartInfoFactory", "ExternalNcc", "ExternalVerifier", "HostedNcc",
        "NccTestExecutionListener", "NccTestFileInfo", "ProcessExtensions", "ThreadPoolUtils",
        "VerifierResult", "Ncc", "Main", "NccMessageType", "NccResult", "NccTest",
        "NccTestDescription", "NccTestOutputWriter", "ProcessStartInfoFactory",
        "Properties/AssemblyInfo", "RuntimeProcessStartInfoFactory", "Verifier"
    };
    var trFiles = string.Join(" ", trSrc.Select(f => $"snippets/Nemerle.Test/Nemerle.Compiler.Test/{f}.n"));
    var trArgs = $"{nccBoot}/ncc.exe {trFiles} {sec}" +
        $" -r {outDir}/Nemerle.Test.Framework.dll" +
        $" -r {st1}/Nemerle.dll -r {st1}/Nemerle.Compiler.dll -r {st1}/Nemerle.Macros.dll" +
        $" -t exe -o {outDir}/Nemerle.Compiler.Test.dll";
    StartProcess("dotnet", trArgs);

    Information("=== Stage 1b complete! ===");
});

Task("Test")
    .IsDependentOn("Stage1b")
    .Does(() =>
{
    Information("=== Running tests ===");

    var nccRt = FindNetCore21Runtime();

    // Framework references (like C#/F# MSBuild ResolveFrameworkReferences)
    var refArgs = string.Join(" ", new[] {
        $"-ref \"{nccBoot}/System.Security.Permissions.dll\"",
        $"-ref \"{nccRt}/System.Console.dll\"",
        $"-ref \"{nccRt}/System.Runtime.Extensions.dll\"",
        $"-ref \"{nccRt}/System.IO.FileSystem.dll\"",
        $"-ref \"{nccRt}/System.Threading.Thread.dll\"",
        $"-ref \"{nccRt}/System.Linq.dll\"",
        $"-ref \"{nccRt}/System.Text.RegularExpressions.dll\"",
        $"-ref \"{nccRt}/System.Collections.dll\"",
    });

    // Generate runtimeconfig.json for EXE tests
    var rtConfig = $@"{{
  ""runtimeOptions"": {{
    ""framework"": {{
      ""name"": ""Microsoft.NETCore.App"",
      ""version"": ""{netCoreVersion}""
    }}
  }}
}}";
    int rtCount = 0;
    foreach (var dir in new[] { "testsuite/positive", "testsuite/negative" })
    {
        foreach (var f in System.IO.Directory.GetFiles(dir, "*.n"))
        {
            if (System.IO.File.ReadAllText(f).Contains("BEGIN-OUTPUT"))
            {
                System.IO.File.WriteAllText(
                    System.IO.Path.ChangeExtension(f, ".runtimeconfig.json"), rtConfig);
                rtCount++;
            }
        }
    }
    Information($"  Generated {rtCount} runtimeconfig.json files");

    // Use the real test runner (HostedNcc + ThreadRunner)
    var testExe = "snippets/Nemerle.Test/Nemerle.Compiler.Test/bin/Release/Nemerle.Compiler.Test.dll";

    // Positive
    var posFiles = System.IO.Directory.GetFiles("testsuite/positive", "*.n");
    var posFileArgs = string.Join(" ", posFiles.Select(f => $"\"{f}\""));
    var posArgs = $"\"{testExe}\" {posFileArgs} -r dotnet -p \"-nowarn:10003\" {refArgs}";
    Information($"  Positive: {posFiles.Length} tests...");
    StartProcess("dotnet", posArgs);

    // Negative
    var negFiles = System.IO.Directory.GetFiles("testsuite/negative", "*.n");
    var negFileArgs = string.Join(" ", negFiles.Select(f => $"\"{f}\""));
    var negArgs = $"\"{testExe}\" {negFileArgs} -r dotnet -p \"-nowarn:10003\" {refArgs}";
    Information($"  Negative: {negFiles.Length} tests...");
    StartProcess("dotnet", negArgs);

    Information("=== Tests complete ===");
});



Task("Default")
    .IsDependentOn("Stage1");

RunTarget(target);
