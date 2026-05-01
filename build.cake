//////////////////////////////////////////////////////////////////////
// ARGUMENTS
//////////////////////////////////////////////////////////////////////
var target        = Argument("target", "Default");
var configuration = Argument("configuration", "Release");
var nccBoot       = Argument("nccBoot", "boot-dnlib");

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

    // Find highest 2.1.x version
    var dirs = System.IO.Directory.GetDirectories(sharedDir, "2.1.*");
    if (dirs.Length == 0)
        throw new Exception("No .NET Core 2.1 runtime found! Install it: dotnet-install.ps1 -Runtime dotnet -Version 2.1.30");

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
    var runtimeConfig =
@"{
  ""runtimeOptions"": {
    ""framework"": {
      ""name"": ""Microsoft.NETCore.App"",
      ""version"": ""2.1.0""
    },
    ""rollForward"": ""LatestPatch""
  }
}";
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
    CopyFile($"bin/{configuration}/Nemerle.MSBuild.Tasks.dll", $"{nccBoot}/Nemerle.MSBuild.Tasks.dll");
    Information("  MSBuild Tasks built and copied.");
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
    Information("=== Stage 1 complete! ===");
});

Task("Test")
    .IsDependentOn("Stage1")
    .Does(() =>
{
    Information("=== Running tests ===");

    var nccExe = $"bin/{configuration}/ncc-core.exe";
    var nccRt = FindNetCore21Runtime();
    var baseRefs = new[] {
        $"-r {nccRt}/System.Console.dll",
        $"-r {nccRt}/System.Runtime.Extensions.dll",
        $"-r {nccRt}/System.IO.FileSystem.dll",
        $"-nowarn:10003"
    };

    var positiveDir = "testsuite/positive";
    var negativeDir = "testsuite/negative";
    var tmpDir = "testsuite/.tmp_test";
    EnsureDirectoryExists(tmpDir);

    int RunNcc(string file, string extraArgs, bool expectSuccess)
    {
        var outFile = $"{tmpDir}/{System.IO.Path.GetFileNameWithoutExtension(file)}";
        outFile += (extraArgs.Contains("-t:exe") ? ".exe" : ".dll");
        var args = $"\"{nccExe}\" \"{file}\" {string.Join(" ", baseRefs)} {extraArgs} -o \"{outFile}\"";
        var exitCode = StartProcess("dotnet", args);
        var ok = expectSuccess ? exitCode == 0 : exitCode != 0;
        if (!ok)
            Error($"  FAIL: {file} (exit={exitCode}, expected={(expectSuccess ? "success" : "failure")})");
        return ok ? 1 : 0;
    }

    // --- Positive tests ---
    Information($"  Positive tests ({positiveDir}):");
    var posFiles = System.IO.Directory.GetFiles(positiveDir, "*.n");
    var posPassed = 0;
    var posFailed = 0;
    foreach (var file in posFiles)
    {
        var extra = "-t:library";
        // Some tests produce executables with expected output
        var content = System.IO.File.ReadAllText(file);
        if (content.Contains("Main()") && content.Contains("BEGIN-OUTPUT"))
            extra = "-t:exe";
        if (RunNcc(file, extra, true) == 1)
            posPassed++;
        else
            posFailed++;
    }
    Information($"  Positive: {posPassed} passed, {posFailed} failed, {posFiles.Length} total");

    // --- Negative tests ---
    Information($"  Negative tests ({negativeDir}):");
    var negFiles = System.IO.Directory.GetFiles(negativeDir, "*.n");
    var negPassed = 0;
    var negFailed = 0;
    foreach (var file in negFiles)
    {
        if (RunNcc(file, "-t:library", false) == 1)
            negPassed++;
        else
            negFailed++;
    }
    Information($"  Negative: {negPassed} passed, {negFailed} failed, {negFiles.Length} total");

    // Summary
    var totalPassed = posPassed + negPassed;
    var totalFailed = posFailed + negFailed;
    var total = posFiles.Length + negFiles.Length;
    Information($"=== Test summary: {totalPassed}/{total} passed, {totalFailed} failed ===");

    if (totalFailed > 0)
        throw new Exception($"{totalFailed} test(s) failed");

    // Cleanup
    DeleteDirectory(tmpDir, new DeleteDirectorySettings { Recursive = true });
});


Task("Default")
    .IsDependentOn("Stage1");

RunTarget(target);
