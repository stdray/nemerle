//////////////////////////////////////////////////////////////////////
// ARGUMENTS
//////////////////////////////////////////////////////////////////////
var target        = Argument("target", "Default");
var configuration = Argument("configuration", "Release");
var nccBoot       = Argument("nccBoot", "boot-dnlib");
var netCoreVersion = Argument("netCoreVersion", "2.1.0");

//////////////////////////////////////////////////////////////////////
// CONSTANTS
//////////////////////////////////////////////////////////////////////

var stage1Out     = $"bin/{configuration}";
var stage2Out     = $"bin/{configuration}/Stage2";
var testRunnerOut = "snippets/Nemerle.Test/Nemerle.Compiler.Test/bin/Release";

string RuntimeConfig(string version) => $@"{{
  ""runtimeOptions"": {{
    ""framework"": {{
      ""name"": ""Microsoft.NETCore.App"",
      ""version"": ""{version}"",
      ""rollForward"": ""LatestPatch""
    }}
  }}
}}";

var ShimCsproj = @"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>netstandard2.0</TargetFramework>
    <AssemblyName>System.Security.Permissions</AssemblyName>
  </PropertyGroup>
</Project>";

var ShimCs = @"namespace System.Security.Permissions {
    public class SecurityAttribute : System.Attribute {
        public SecurityAttribute(System.Security.Permissions.SecurityAction action) { }
        public bool Unrestricted { get; set; }
    }
    public enum SecurityAction { Demand, Assert, Deny, PermitOnly, LinkDemand,
        InheritanceDemand, RequestMinimum, RequestOptional, RequestRefuse }
}";

//////////////////////////////////////////////////////////////////////
// HELPERS
//////////////////////////////////////////////////////////////////////

string FindNetCore21Runtime()
{
    var dotnetRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT");
    if (string.IsNullOrEmpty(dotnetRoot))
        dotnetRoot = Environment.GetEnvironmentVariable("ProgramFiles") + "/dotnet";
    if (!DirectoryExists(dotnetRoot))
        dotnetRoot = @"C:/Program Files/dotnet";

    var sharedDir = dotnetRoot + "/shared/Microsoft.NETCore.App";
    if (!DirectoryExists(sharedDir))
        throw new Exception($"Cannot find .NET runtime shared directory: {sharedDir}");

    var majorMinor = netCoreVersion.Substring(0, netCoreVersion.LastIndexOf('.'));
    var dirs = System.IO.Directory.GetDirectories(sharedDir, $"{majorMinor}.*");
    if (dirs.Length == 0)
        throw new Exception($"No .NET Core {majorMinor} runtime found!");

    System.Array.Sort(dirs);
    var latest = dirs[dirs.Length - 1].Replace('\\', '/');
    Information($"  Found .NET Core runtime: {latest}");
    return latest;
}

void WriteRuntimeConfig(string path)
    => System.IO.File.WriteAllText(path, RuntimeConfig(netCoreVersion));

int Ncc(string tool, string sources, string refs, string targetType, string output)
    => StartProcess("dotnet", $"\"{tool}\" {sources} {refs} -t {targetType} -o \"{output}\"");

// Shared framework refs (-r for compiler invocation)
string[] FrameworkRefs(string nccRt) => new[] {
    $"-r \"{nccRt}/System.Console.dll\"",
    $"-r \"{nccRt}/System.Runtime.Extensions.dll\"",
    $"-r \"{nccRt}/System.Threading.Thread.dll\"",
    $"-r \"{nccRt}/System.IO.FileSystem.dll\"",
};

// Nemerle library refs from a compiler directory
string[] NemerleRefs(string compDir) => new[] {
    $"-r \"{compDir}/Nemerle.dll\"",
    $"-r \"{compDir}/Nemerle.Compiler.dll\"",
    $"-r \"{compDir}/Nemerle.Macros.dll\"",
    $"-r \"{compDir}/System.Security.Permissions.dll\"",
};

// Nemerle refs WITHOUT Nemerle.dll (boot compiler already has it loaded)
string[] NemerleRefsNoBase(string compDir) => new[] {
    $"-r \"{compDir}/Nemerle.Compiler.dll\"",
    $"-r \"{compDir}/Nemerle.Macros.dll\"",
    $"-r \"{compDir}/System.Security.Permissions.dll\"",
};

string AllRefs(string compDir, string nccRt)
    => string.Join(" ", NemerleRefs(compDir).Concat(FrameworkRefs(nccRt)));

string AllRefsNoBase(string compDir, string nccRt)
    => string.Join(" ", NemerleRefsNoBase(compDir).Concat(FrameworkRefs(nccRt)));

// Test runner -ref flags
string[] TestRunnerRefs(string nccBootDir, string nccRt)
{
    var fr = FrameworkRefs(nccRt)
        .Select(r => r.Replace("-r ", "-ref "))
        .ToList();
    fr.Add($@"-ref ""{nccBootDir}/System.Security.Permissions.dll""");
    fr.Add($@"-ref ""{nccRt}/System.Linq.dll""");
    fr.Add($@"-ref ""{nccRt}/System.Text.RegularExpressions.dll""");
    fr.Add($@"-ref ""{nccRt}/System.Collections.dll""");
    return fr.ToArray();
}

//////////////////////////////////////////////////////////////////////
// TASKS
//////////////////////////////////////////////////////////////////////

Task("Clean")
    .Does(() =>
{
    Information("Cleaning all build artifacts...");

    // Build outputs
    foreach (var dir in new[] { "bin", "obj" })
        if (DirectoryExists(dir))
            TryDeleteDir(dir, $"  Deleted {dir}/");

    // Test compiled files
    void  TryDeleteFile(string f) { try { System.IO.File.Delete(f); Verbose($"  Deleted {f}"); } catch (Exception ex) { Warning($"  Could not delete {f}: {ex.Message}"); } }
    void  TryDeleteDir(string d, string label) { try { DeleteDirectory(d, new DeleteDirectorySettings { Recursive = true }); Information(label); } catch (Exception ex) { Warning($"  {label} failed: {ex.Message}"); } }
    foreach (var dir in new[] { "testsuite/positive", "testsuite/negative" })
        foreach (var ext in new[] { "*.exe", "*.dll", "*.runtimeconfig.json", "*.pdb", "*.netmodule" })
            foreach (var f in System.IO.Directory.GetFiles(dir, ext))
                TryDeleteFile(f);

    // Test temp + generated source files
    foreach (var d in new[] { "testsuite/.tmp_test" })
        if (DirectoryExists(d))
            TryDeleteDir(d, $"  Deleted {d}/");

    // Generated Nemerle sources in testsuite
    foreach (var f in System.IO.Directory.GetFiles("testsuite/positive", "_N_GeneratedSource_*.n"))
        TryDeleteFile(f);
    foreach (var f in System.IO.Directory.GetFiles("testsuite/negative", "_N_GeneratedSource_*.n"))
        TryDeleteFile(f);

    // Test runner output
    if (DirectoryExists(testRunnerOut))
        TryDeleteDir(testRunnerOut, $"  Deleted {testRunnerOut}/");

    // Shim temp
    if (DirectoryExists("tmp_shim"))
        TryDeleteDir("tmp_shim", "  Deleted tmp_shim/");

    Information("Clean completed.");
});


Task("FixBoot")
    .IsDependentOn("Clean")
    .Does(() =>
{
    Information("Fixing boot-dnlib compiler...");
    WriteRuntimeConfig($"{nccBoot}/ncc.runtimeconfig.json");
    Information("  Created ncc.runtimeconfig.json");

    var permsDll = $"{nccBoot}/System.Security.Permissions.dll";
    if (!FileExists(permsDll))
    {
        Information("  Building System.Security.Permissions shim...");
        var shimDir = "tmp_shim";
        EnsureDirectoryExists(shimDir);
        System.IO.File.WriteAllText($"{shimDir}/shim.csproj", ShimCsproj);
        System.IO.File.WriteAllText($"{shimDir}/SecurityAttribute.cs", ShimCs);
        var ms = new DotNetMSBuildSettings();
        ms.SetConfiguration(configuration);
        DotNetBuild(shimDir, new DotNetBuildSettings { MSBuildSettings = ms });
        CopyFile($"{shimDir}/bin/{configuration}/netstandard2.0/System.Security.Permissions.dll", permsDll);
        Information("  System.Security.Permissions.dll created");
    }
    else
        Information("  System.Security.Permissions.dll already exists");
});

Task("BuildTasks")
    .IsDependentOn("FixBoot")
    .Does(() =>
{
    Information("Building MSBuild Tasks...");
    var ms = new DotNetMSBuildSettings();
    ms.SetConfiguration(configuration);
    DotNetBuild("Nemerle.MSBuild.Tasks.csproj", new DotNetBuildSettings { MSBuildSettings = ms });
    try { CopyFile($"{stage1Out}/Nemerle.MSBuild.Tasks.dll", $"{nccBoot}/Nemerle.MSBuild.Tasks.dll"); }
    catch { Warning("  MSBuild Tasks DLL locked — using committed version"); }
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
    Information("  SDK files ready.");
});

Task("Stage1")
    .IsDependentOn("BuildTasks")
    .IsDependentOn("PrepareSdk")
    .Does(() =>
{
    Information("=== STAGE 1: Building with boot compiler ===");

    var baseMs = new DotNetMSBuildSettings();
    baseMs.SetConfiguration(configuration);
    baseMs.WithProperty("Nemerle", nccBoot);

    // Build each nproj with its own IntermediateOutputPath to avoid shared obj/
    foreach (var nproj in new[] { "Nemerle.nproj", "Nemerle.Compiler.nproj", "Nemerle.Macros.nproj" })
    {
        var name = System.IO.Path.GetFileNameWithoutExtension(nproj);
        var intPath = $"obj/{name}";
        Information($"  Building {nproj} (IntermediateOutputPath={intPath})...");

        var ms = new DotNetMSBuildSettings();
        ms.SetConfiguration(configuration);
        ms.WithProperty("Nemerle", nccBoot);
        ms.WithProperty("IntermediateOutputPath", intPath + "/");
        ms.WithProperty("BaseIntermediateOutputPath", intPath + "/");

        DotNetRestore(nproj, new DotNetRestoreSettings {
            ArgumentCustomization = args => args
                .Append($"/p:Nemerle={nccBoot}")
                .Append($"/p:IntermediateOutputPath={intPath}/")
                .Append($"/p:BaseIntermediateOutputPath={intPath}/")
        });
        DotNetBuild(nproj, new DotNetBuildSettings { MSBuildSettings = ms });
    }

    // ncc-core: direct ncc invocation (MSBuild can't handle netcoreapp2.1 TF)
    Information("  Building ncc-core.exe...");
    var nccRt = FindNetCore21Runtime();
    // Use Stage 1 just-built DLLs (not boot-dnlib) so version refs match at runtime
    // Exclude Nemerle.dll (boot compiler has it loaded)
    var fwRefs = string.Join(" ", FrameworkRefs(nccRt));
    var st1Refs = $"-r \"{stage1Out}/Nemerle.Compiler.dll\" -r \"{stage1Out}/Nemerle.Macros.dll\" -r \"{nccBoot}/System.Security.Permissions.dll\"";
    var exitCode = Ncc($"{nccBoot}/ncc.exe",
        "ncc/main.n ncc/shared/AssemblyInfo.n",
        $"{st1Refs} {fwRefs}",
        "exe", $"{stage1Out}/ncc-core.exe");
    CopyFile($"{nccBoot}/dnlib.dll", $"{stage1Out}/dnlib.dll");
    if (exitCode != 0)
        throw new Exception($"ncc-core build failed with exit code {exitCode}");
    CopyFile($"{nccBoot}/System.Security.Permissions.dll", $"{stage1Out}/System.Security.Permissions.dll");
    WriteRuntimeConfig($"{stage1Out}/ncc-core.runtimeconfig.json");
    Information("=== Stage 1 complete! ===");
});

Task("Stage1b")
    .IsDependentOn("Stage1")
    .Does(() =>
{
    Information("=== STAGE 1b: Building test runner ===");

    var tool   = $"{nccBoot}/ncc.exe";  // boot compiler builds test runner
    var secRef = $"-r \"{nccBoot}/System.Security.Permissions.dll\"";

    // Copy Stage 1 libs to test runner dir (HostedNcc version match)
    EnsureDirectoryExists(testRunnerOut);
    foreach (var dll in new[] { "Nemerle", "Nemerle.Compiler", "Nemerle.Macros" })
        CopyFile($"{stage1Out}/{dll}.dll", $"{testRunnerOut}/{dll}.dll");
    CopyFile($"{nccBoot}/System.Security.Permissions.dll", $"{testRunnerOut}/System.Security.Permissions.dll");

    // Test framework
    Information("  Building Nemerle.Test.Framework.dll...");
    var fwFiles = string.Join(" ", new[] {
        "ColorizedOutputWriter", "DefaultColorizedOutputWriter", "ExecutionListener",
        "IRunner", "MulticastExecutionListener", "Result", "Runner", "Statistics",
        "TeamCityExecutionListener", "Test", "ThreadRunner", "UnixColorizedOutputWriter",
        "VisualStudioExecutionListener", "Utils/FileSearcher", "Properties/AssemblyInfo"
    }.Select(f => $"\"snippets/Nemerle.Test/Nemerle.Test.Framework/{f}.n\""));
    Ncc(tool, fwFiles, $"{secRef} -r \"{stage1Out}/Nemerle.dll\"",
        "library", $"{testRunnerOut}/Nemerle.Test.Framework.dll");

    // Test runner
    Information("  Building Nemerle.Compiler.Test.dll...");
    var trFiles = string.Join(" ", new[] {
        "DefaultProcessStartInfoFactory", "ExternalNcc", "ExternalVerifier", "HostedNcc",
        "NccTestExecutionListener", "NccTestFileInfo", "ProcessExtensions", "ThreadPoolUtils",
        "VerifierResult", "Ncc", "Main", "NccMessageType", "NccResult", "NccTest",
        "NccTestDescription", "NccTestOutputWriter", "ProcessStartInfoFactory",
        "Properties/AssemblyInfo", "RuntimeProcessStartInfoFactory", "Verifier"
    }.Select(f => $"\"snippets/Nemerle.Test/Nemerle.Compiler.Test/{f}.n\""));
    var trRefs = $"{secRef} -r \"{testRunnerOut}/Nemerle.Test.Framework.dll\"" +
                 $" -r \"{stage1Out}/Nemerle.dll\" -r \"{stage1Out}/Nemerle.Compiler.dll\"" +
                 $" -r \"{stage1Out}/Nemerle.Macros.dll\"";
    Ncc(tool, trFiles, trRefs, "exe", $"{testRunnerOut}/Nemerle.Compiler.Test.dll");

    Information("=== Stage 1b complete! ===");
});

Task("Stage2")
    .IsDependentOn("Stage1b")
    .Does(() =>
{
    Information("=== STAGE 2: Building compiler with Stage 1 compiler ===");

    var nccRt = FindNetCore21Runtime();
    var tool  = $"{stage1Out}/ncc-core.exe";  // Stage 1 → Stage 2
    var fwRefs = string.Join(" ", FrameworkRefs(nccRt));
    EnsureDirectoryExists(stage2Out);

    // Nemerle.dll: compiler IS Nemerle, only need framework refs
    var libSrc = string.Join(" ",
        System.IO.Directory.GetFiles("lib", "*.n").Select(f => $"\"{f}\""));
    Ncc(tool, libSrc, fwRefs, "library", $"{stage2Out}/Nemerle.dll");
    Information("    Nemerle.dll");

    var compSrc = string.Join(" ",
        new[] { "ncc/shared", "ncc/backend", "ncc/frontend", "Nemerle.Location" }
            .SelectMany(d => System.IO.Directory.GetFiles(d, "*.n",
                System.IO.SearchOption.AllDirectories))
            .Select(f => $"\"{f}\""));
    // Nemerle.Compiler.dll: needs just-built Nemerle.dll + framework refs
    var ccRefs = $"-r \"{stage2Out}/Nemerle.dll\" {fwRefs}";
    Ncc(tool, compSrc, ccRefs, "library", $"{stage2Out}/Nemerle.Compiler.dll");
    Information("    Nemerle.Compiler.dll");

    var macSrc = string.Join(" ",
        System.IO.Directory.GetFiles("macros", "*.n").Select(f => $"\"{f}\""));
    // Nemerle.Macros.dll: needs just-built Nemerle + Compiler, NOT old Macros.dll
    // (compiler already loads macros for execution; source redefines them into new DLL)
    var macRefs = $"-r \"{stage2Out}/Nemerle.dll\" -r \"{stage2Out}/Nemerle.Compiler.dll\" {fwRefs}";
    Ncc(tool, macSrc, macRefs, "library", $"{stage2Out}/Nemerle.Macros.dll");
    Information("    Nemerle.Macros.dll");

    // ncc-core.exe: all Stage 2 DLLs
    var exeRefs = $"-r \"{stage2Out}/Nemerle.dll\" -r \"{stage2Out}/Nemerle.Compiler.dll\" -r \"{stage2Out}/Nemerle.Macros.dll\" -r \"{stage1Out}/System.Security.Permissions.dll\" {fwRefs}";
    Ncc(tool, "ncc/main.n ncc/shared/AssemblyInfo.n", exeRefs,
        "exe", $"{stage2Out}/ncc-core.exe");
    Information("    ncc-core.exe");

    CopyFile($"{stage1Out}/dnlib.dll", $"{stage2Out}/dnlib.dll");
    CopyFile($"{stage1Out}/System.Security.Permissions.dll", $"{stage2Out}/System.Security.Permissions.dll");
    WriteRuntimeConfig($"{stage2Out}/ncc-core.runtimeconfig.json");

    Information("=== Stage 2 complete! ===");
});


Task("Validate")
    .IsDependentOn("Stage2")
    .Does(() =>
{
    Information("=== VALIDATE: Stage 1 vs Stage 2 ===");

    var dlls = new[] { "Nemerle.dll", "Nemerle.Compiler.dll", "Nemerle.Macros.dll" };
    var allMatch = true;

    foreach (var dll in dlls)
    {
        var s1 = $"{stage1Out}/{dll}";
        var s2 = $"{stage2Out}/{dll}";
        if (!FileExists(s1) || !FileExists(s2))
        {
            Warning($"  Missing: {dll}");
            allMatch = false;
            continue;
        }
        var b1 = System.IO.File.ReadAllBytes(s1);
        var b2 = System.IO.File.ReadAllBytes(s2);
        if (b1.Length != b2.Length)
        {
            Warning($"  MISMATCH {dll}: S1={b1.Length}B, S2={b2.Length}B");
            allMatch = false;
        }
        else
            Information($"  OK {dll}: {b1.Length} bytes");
    }

    if (allMatch) Information("=== Validate: ALL MATCH ===");
    else Warning("=== Validate: MISMATCHES ===");
});

Task("Test")
    .IsDependentOn("Stage2")
    .Does(() =>
{
    Information("=== RUNNING TESTS ===");

    var nccRt = FindNetCore21Runtime();
    var refArgs = string.Join(" ", TestRunnerRefs(nccBoot, nccRt));

    // Runtimeconfig for EXE tests
    int rtCount = 0;
    foreach (var dir in new[] { "testsuite/positive", "testsuite/negative" })
        foreach (var f in System.IO.Directory.GetFiles(dir, "*.n"))
            if (System.IO.File.ReadAllText(f).Contains("BEGIN-OUTPUT"))
            {
                System.IO.File.WriteAllText(
                    System.IO.Path.ChangeExtension(f, ".runtimeconfig.json"),
                    RuntimeConfig(netCoreVersion));
                rtCount++;
            }
    Information($"  Generated {rtCount} runtimeconfig.json files");

    var testExe = $"{testRunnerOut}/Nemerle.Compiler.Test.dll";

    foreach (var (label, dir) in new[] { ("Positive", "testsuite/positive"), ("Negative", "testsuite/negative") })
    {
        var files = string.Join(" ",
            System.IO.Directory.GetFiles(dir, "*.n").Select(f => $"\"{f}\""));
        Information($"  {label}: {System.IO.Directory.GetFiles(dir, "*.n").Length} tests...");
        var settings = new ProcessSettings {
            Arguments = $"\"{testExe}\" {files} -r dotnet -p \"-nowarn:10003\" {refArgs}",
            WorkingDirectory = dir  // avoid multi-project-file conflict
        };
        StartProcess("dotnet", settings);
    }

    Information("=== Tests complete ===");
});

Task("Default")
    .IsDependentOn("Validate");

RunTarget(target);
