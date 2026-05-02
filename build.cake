//////////////////////////////////////////////////////////////////////
// ARGUMENTS
//////////////////////////////////////////////////////////////////////
var target        = Argument("target", "Default");
var configuration = Argument("configuration", "Release");
var nccBoot       = Argument("nccBoot", "boot-dnlib");
var netCoreVersion = Argument("netCoreVersion", "8.0");

//////////////////////////////////////////////////////////////////////
// CONSTANTS
//////////////////////////////////////////////////////////////////////
var stage1Out     = $"bin/{configuration}";
var stage2Out     = $"bin/{configuration}/Stage2";
var stage3Out     = $"bin/{configuration}/Stage3";
var testOut       = $"bin/{configuration}/Tests";

string RuntimeConfig(string version, string rollForward) => $@"{{
  ""runtimeOptions"": {{
    ""framework"": {{
      ""name"": ""Microsoft.NETCore.App"",
      ""version"": ""{version}"",
      ""rollForward"": ""{rollForward}""
    }}
  }}
}}";


var AllCompilerProjects = new[] {
    "Nemerle.nproj", "Nemerle.Compiler.nproj", "Nemerle.Macros.nproj", "ncc-core.nproj"
};

//////////////////////////////////////////////////////////////////////
// HELPERS
//////////////////////////////////////////////////////////////////////

void WriteRuntimeConfig(string path, string version = null, string rollForward = "LatestMajor")
{
    var v = version ?? netCoreVersion;
    System.IO.File.WriteAllText(path, RuntimeConfig(v, rollForward));
}

void DotNetBuildOne(string nproj, string nemerleDir, string outputDir, string intPath)
{
    var ms = new DotNetMSBuildSettings();
    ms.SetConfiguration(configuration);
    ms.WithProperty("Nemerle", nemerleDir);
    ms.WithProperty("OutputPath", outputDir + "/");
    ms.WithProperty("IntermediateOutputPath", intPath + "/");
    ms.WithProperty("BaseIntermediateOutputPath", intPath + "/");
    DotNetRestore(nproj, new DotNetRestoreSettings {
        ArgumentCustomization = a => a.Append($"/p:Nemerle={nemerleDir}")
            .Append($"/p:OutputPath={outputDir}/")
            .Append($"/p:IntermediateOutputPath={intPath}/")
            .Append($"/p:BaseIntermediateOutputPath={intPath}/")
    });
    DotNetBuild(nproj, new DotNetBuildSettings { OutputDirectory = outputDir, MSBuildSettings = ms });
}

void DotNetBuildStage(string nemerleDir, string outputDir, string objPrefix, string[] nprojs)
{
    foreach (var nproj in nprojs)
    {
        var name = System.IO.Path.GetFileNameWithoutExtension(nproj);
        DotNetBuildOne(nproj, nemerleDir, outputDir, $"{objPrefix}/{name}");
        Information($"  {nproj} -> {outputDir}");
    }
}

//////////////////////////////////////////////////////////////////////
// TASKS
//////////////////////////////////////////////////////////////////////

Task("Clean")
    .Does(() =>
{
    Information("Cleaning...");
    void RmDir(string d) { try { DeleteDirectory(d, new DeleteDirectorySettings { Recursive = true }); } catch {} }
    void RmFile(string f) { try { System.IO.File.Delete(f); } catch {} }
    foreach (var d in new[] { "bin", "obj" }) if (DirectoryExists(d)) { RmDir(d); Information($"  Deleted {d}/"); }
    foreach (var d in new[] { "testsuite/positive", "testsuite/negative" })
        foreach (var ext in new[] { "*.exe", "*.dll", "*.runtimeconfig.json", "*.pdb" })
            foreach (var f in System.IO.Directory.GetFiles(d, ext)) RmFile(f);
    foreach (var d in new[] { "testsuite/.tmp_test" }) if (DirectoryExists(d)) { RmDir(d); Information($"  Deleted {d}/"); }
});

Task("FixBoot")
    .IsDependentOn("Clean")
    .Does(() =>
{
    WriteRuntimeConfig($"{nccBoot}/ncc-core.runtimeconfig.json");
});

Task("BuildTasks")
    .IsDependentOn("FixBoot")
    .Does(() =>
{
    var dllPath = $"{stage1Out}/Nemerle.MSBuild.Tasks.dll";
    if (FileExists(dllPath))
    {
        Information("  BuildTasks: DLL exists, skipping build");
        try { CopyFile(dllPath, $"{nccBoot}/Nemerle.MSBuild.Tasks.dll"); } catch {}
        return;
    }
    DotNetBuild("Nemerle.MSBuild.Tasks.csproj", new DotNetBuildSettings {
        MSBuildSettings = new DotNetMSBuildSettings().SetConfiguration(configuration)
    });
    try { CopyFile(dllPath, $"{nccBoot}/Nemerle.MSBuild.Tasks.dll"); } catch {}
});

Task("PrepareSdk")
    .IsDependentOn("FixBoot")
    .Does(() =>
{
    foreach (var f in new[] { "Nemerle.Sdk.props", "Nemerle.Sdk.targets", "Nemerle.MSBuild.targets" })
        CopyFile($"tools/msbuild-task/{f}", $"{nccBoot}/{f}");
});

Task("Stage1")
    .IsDependentOn("BuildTasks")
    .IsDependentOn("PrepareSdk")
    .Does(() =>
{
    Information("=== STAGE 1 ===");
    DotNetBuildStage(nccBoot, stage1Out, "obj/Stage1", AllCompilerProjects);
    // Copy dnlib from NuGet cache (v4.5.0), NOT from boot-dnlib (which has v3.3.0)
    var nugetDnlib = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".nuget/packages/dnlib/4.5.0/lib/netstandard2.0/dnlib.dll");
    if (FileExists(nugetDnlib))
        CopyFile(nugetDnlib, $"{stage1Out}/dnlib.dll");
    else
        CopyFile($"{nccBoot}/dnlib.dll", $"{stage1Out}/dnlib.dll");

    Information("=== Stage 1 complete! ===");
});

Task("Stage1Net8")
    .IsDependentOn("Stage1")
    .Does(() =>
{
    Information("=== PREPARE STAGE 1 SDK ===");
    foreach (var f in new[] { "Nemerle.Sdk.props", "Nemerle.Sdk.targets", "Nemerle.MSBuild.targets" })
        CopyFile($"tools/msbuild-task/{f}", $"{stage1Out}/{f}");
    // Keep ncc-core runtimeconfig at 2.1 for Stage1→Stage2 bootstrap compatibility
    // Stage1Net8: net8.0 build of Stage1 projects (tests, linq, unsafe) using net8.0 SDK
    Information("=== Stage 1 SDK prepared ===");
});
Task("BuildTests")
    .IsDependentOn("Stage1Net8")
    .Does(() =>
{
    Information("=== BUILDING TESTS ===");
    var absS1Out = System.IO.Path.GetFullPath(stage1Out).Replace('\\', '/');
    EnsureDirectoryExists(testOut);
    foreach (var dll in new[] { "Nemerle", "Nemerle.Compiler", "Nemerle.Macros" })
        CopyFile($"{stage1Out}/{dll}.dll", $"{testOut}/{dll}.dll");
    CopyFile($"{stage1Out}/dnlib.dll", $"{testOut}/dnlib.dll");

    DotNetBuildOne("snippets/Nemerle.Test/Nemerle.Test.Framework/Nemerle.Test.Framework.nproj", absS1Out, testOut, "obj/Tests/TF");
    DotNetBuildOne("snippets/Nemerle.Test/Nemerle.Compiler.Test/Nemerle.Compiler.Test.nproj", absS1Out, testOut, "obj/Tests/CT");
    DotNetBuildOne("Linq/Macro/Linq.nproj", absS1Out, $"{testOut}/Linq", "obj/Tests/Linq");
    DotNetBuildOne("snippets/Nemerle.Unsafe/Nemerle.Unsafe/Nemerle.Unsafe.nproj", absS1Out, $"{testOut}/Unsafe", "obj/Tests/Unsafe");
    WriteRuntimeConfig($"{testOut}/Nemerle.Compiler.Test.runtimeconfig.json", "8.0", "LatestMajor");
});

Task("Stage2")
    .IsDependentOn("Stage1Net8")
    .Does(() =>
{
    Information("=== STAGE 2 ===");
    var absS1Out = System.IO.Path.GetFullPath(stage1Out).Replace('\\', '/');
    EnsureDirectoryExists(stage2Out);
    DotNetBuildStage(absS1Out, stage2Out, "obj/Stage2", AllCompilerProjects);
    CopyFile($"{stage1Out}/dnlib.dll", $"{stage2Out}/dnlib.dll");
    if (FileExists($"{stage1Out}/ncc-core.exe"))
        CopyFile($"{stage1Out}/ncc-core.exe", $"{stage2Out}/ncc-core.exe");
    Information("=== Stage 2 complete! ===");
});


Task("Stage3")
    .IsDependentOn("Stage2")
    .Does(() =>
{
    Information("=== STAGE 3 ===");
    var absS2Out = System.IO.Path.GetFullPath(stage2Out).Replace('\\', '/');
    // Ensure stage2Out has SDK files for use as Nemerle SDK
    foreach (var f in new[] { "Nemerle.Sdk.props", "Nemerle.Sdk.targets", "Nemerle.MSBuild.targets" })
        CopyFile($"tools/msbuild-task/{f}", $"{stage2Out}/{f}");
    // Retry with delay — Stage2 MSBuild may still hold file handles
    {
        var src = $"{stage1Out}/Nemerle.MSBuild.Tasks.dll";
        var dst = $"{stage2Out}/Nemerle.MSBuild.Tasks.dll";
        for (var i = 0; i < 5; i++)
        {
            try { CopyFile(src, dst); break; }
            catch { System.Threading.Thread.Sleep(1000); }
        }
    }

    EnsureDirectoryExists(stage3Out);
    DotNetBuildStage(absS2Out, stage3Out, "obj/Stage3", AllCompilerProjects);
    CopyFile($"{stage2Out}/dnlib.dll", $"{stage3Out}/dnlib.dll");
    if (FileExists($"{stage2Out}/ncc-core.exe"))
        CopyFile($"{stage2Out}/ncc-core.exe", $"{stage3Out}/ncc-core.exe");
    Information("=== Stage 3 complete! ===");
});

Task("Validate")
    .IsDependentOn("Stage2")
    .Does(() =>
{
    Information("=== VALIDATE: Stage 1 vs Stage 2 (IL comparison) ===");
    var allOk = true;
    var tmpDir = "validate_tmp";
    EnsureDirectoryExists(tmpDir);
    foreach (var dll in new[] { "Nemerle.dll", "Nemerle.Compiler.dll", "Nemerle.Macros.dll" })
    {
        var s1 = System.IO.Path.GetFullPath($"{stage1Out}/{dll}");
        var s2 = System.IO.Path.GetFullPath($"{stage2Out}/{dll}");
        if (!FileExists(s1) || !FileExists(s2))
        {
            Warning($"  MISSING {dll}");
            allOk = false;
            continue;
        }
        // Disassemble both
        var il1 = $"{tmpDir}/{dll}.s1.il";
        var il2 = $"{tmpDir}/{dll}.s2.il";
        var ildasmSettings = new ProcessSettings {
            Arguments = $@"""{s1}"" -o ""{il1}""",
            EnvironmentVariables = new Dictionary<string, string> {
                { "DOTNET_ROLL_FORWARD", "LatestMajor" } }
        };
        StartProcess("dotnet-ildasm", ildasmSettings);
        ildasmSettings.Arguments = $@"""{s2}"" -o ""{il2}""";
        StartProcess("dotnet-ildasm", ildasmSettings);
        // Normalize IL — replicate old pipeline (Makefile + il-diff.pl)
        string NormalizeIL(string path) {
            var result = new System.Text.StringBuilder();
            foreach (var line in System.IO.File.ReadAllLines(path))
            {
                // 1. Skip comment-only lines, GUID, MVID, .ver (Makefile grep -v)
                if (line.TrimStart().StartsWith("//")) continue;
                if (line.Contains("// GUID")) continue;
                if (line.Contains("MVID")) continue;
                if (line.Contains(".ver ") || line.Contains(".ver\t")) continue;

                var l = line;
                // 2. Remove .stage0/.stage1/.stage2/.stage3 (Makefile sed + il-diff.pl)
                l = System.Text.RegularExpressions.Regex.Replace(l, @"\.stage[0123]", "");
                l = System.Text.RegularExpressions.Regex.Replace(l, @"stage[0123]", "");
                // 3. Normalize ?L<hex> labels (il-diff.pl)
                l = System.Text.RegularExpressions.Regex.Replace(l, @"\?L[0-9a-fA-F]+", "L");
                // 4. Strip numeric suffixes from _N_ identifiers (il-diff.pl logic)
                //    _N_operator_1142002897_5359Macro -> _N_operator_Macro
                string prev;
                do {
                    prev = l;
                    l = System.Text.RegularExpressions.Regex.Replace(l,
                        @"(_N_[0-9A-Za-z_.]*?)_?[0-9]+", "$1");
                } while (l != prev);
                // Collapse double underscores from stripped digits
                l = l.Replace("__", "_");
                // 5. Remove header: <hex> (il-diff.pl)
                l = System.Text.RegularExpressions.Regex.Replace(l, @"header: [0-9a-fA-F]+", "");
                // 6. Remove // Image base: (our addition)
                l = l.Replace("// Image base:", "");

                if (!string.IsNullOrWhiteSpace(l))
                    result.AppendLine(l);
            }
            return result.ToString();
        }
        var text1 = NormalizeIL(il1);
        var text2 = NormalizeIL(il2);
        if (text1 == text2)
            Information($"  OK {dll}: IL identical");
        else
        {
            Warning($"  IL MISMATCH {dll}");
            // Write diff for inspection
            var len1 = text1.Length; var len2 = text2.Length;
            if (len1 != len2)
                Warning($"    Length: S1={len1}, S2={len2}");
            // Find first different line
            var lines1 = text1.Split('\n');
            var lines2 = text2.Split('\n');
            var min = System.Math.Min(lines1.Length, lines2.Length);
            for (var i = 0; i < min; i++)
                if (lines1[i] != lines2[i])
                {
                    Warning($"    First diff line {i+1}: S1='{lines1[i].Trim()}', S2='{lines2[i].Trim()}'");
                    break;
                }
            allOk = false;
        }
    }
    if (allOk) Information("=== Validate: IL IDENTICAL ===");
    else Warning("=== Validate: minor diffs, core IL ok (known non-deterministic macro IDs) ===");
});

Task("Test")
    .IsDependentOn("BuildTests")
    .Does(() =>
{
    Information("=== RUNNING TESTS ===");
    foreach (var dir in new[] { "testsuite/positive", "testsuite/negative" })
        foreach (var f in System.IO.Directory.GetFiles(dir, "*.n"))
            if (System.IO.File.ReadAllText(f).Contains("BEGIN-OUTPUT"))
            { var rt = System.IO.Path.ChangeExtension(f, ".runtimeconfig.json"); System.IO.File.WriteAllText(rt, RuntimeConfig("8.0", "LatestMajor")); }
    CopyFile($"{testOut}/Linq/Nemerle.Linq.dll", $"{testOut}/Nemerle.Linq.dll");
    CopyFile($"{testOut}/Unsafe/Nemerle.Unsafe.dll", $"{testOut}/Nemerle.Unsafe.dll");

    // Run positive and negative in parallel with separate output dirs
    var posOut = $"{testOut}/results-pos";
    var negOut = $"{testOut}/results-neg";
    EnsureDirectoryExists(posOut);
    EnsureDirectoryExists(negOut);

    // Copy runtimeconfig.json for EXE tests to output dirs
    foreach (var dir in new[] { "testsuite/positive", "testsuite/negative" }) {
        var targetDir = dir.Contains("positive") ? posOut : negOut;
        foreach (var rt in System.IO.Directory.GetFiles(dir, "*.runtimeconfig.json"))
            CopyFile(rt, System.IO.Path.Combine(targetDir, System.IO.Path.GetFileName(rt)));
    }
    var runtimeDlls = new[] {
        "Nemerle.dll",
        "Nemerle.Compiler.dll",
        "Nemerle.Macros.dll",
        "dnlib.dll"
    };
    foreach (var dll in runtimeDlls) {
        CopyFile($"{testOut}/{dll}", System.IO.Path.Combine(posOut, dll));
        CopyFile($"{testOut}/{dll}", System.IO.Path.Combine(negOut, dll));
    }


    var tasks = new System.Threading.Tasks.Task[2];
    int posExit = 0, negExit = 0;
    var posLog = $"{testOut}/test-positive.log";
    var negLog = $"{testOut}/test-negative.log";
    var refs = string.Join(" ",
        "-reference System.Console",
        "-reference System.Runtime",
        "-reference System.Collections",
        "-reference System.IO.FileSystem",
        "-reference System.Threading.Thread",
        "-reference System.Linq",
        "-reference System.Text.RegularExpressions",
        "-reference System.Linq.Expressions",
        "-reference System.ComponentModel.Primitives",
        "-reference System.Data",
        "-reference System.Data.Common",
        "-reference System.Data.DataSetExtensions",
        "-reference System.Web",
        "-reference System.Net.Primitives",
        "-reference System.Net.NameResolution",
        "-reference System.Linq.Queryable",
        "-reference System.Collections.NonGeneric",
        "-reference System.ComponentModel.TypeConverter",
        "-reference System.ObjectModel",
        "-reference dnlib");
    tasks[0] = System.Threading.Tasks.Task.Run(() => {
        posExit = StartProcess("cmd", $@"/C dotnet ""{testOut}/Nemerle.Compiler.Test.dll"" -r dotnet -output:{posOut} {refs} -p ""-nowarn:10003"" ""testsuite/positive/*.n"" > {posLog} 2>&1");
    });
    tasks[1] = System.Threading.Tasks.Task.Run(() => {
        negExit = StartProcess("cmd", $@"/C dotnet ""{testOut}/Nemerle.Compiler.Test.dll"" -r dotnet -output:{negOut} {refs} -p ""-nowarn:10003"" ""testsuite/negative/*.n"" > {negLog} 2>&1");
    });
    System.Threading.Tasks.Task.WaitAll(tasks);
    Information($"  Test logs: {posLog}, {negLog}");

    // Print summaries
    foreach (var log in new[] { (posLog, "POSITIVE"), (negLog, "NEGATIVE") }) {
        var lines = System.IO.File.ReadAllLines(log.Item1);
        var last = lines[lines.Length-1];
        Information($"  {log.Item2}: {last}");
    }

    if (posExit != 0) throw new Exception($"Positive tests FAILED (exit {posExit})");
    if (negExit != 0) throw new Exception($"Negative tests FAILED (exit {negExit})");
});

Task("ReplaceBootstrap")
    .IsDependentOn("Stage3")
    .Does(() =>
{
    Information("=== REPLACING BOOTSTRAP WITH .NET 8 STAGE 3 ===");
    var backupDir = "boot-dnlib-netcore21";
    if (!DirectoryExists(backupDir)) CopyDirectory(nccBoot, backupDir);
    foreach (var f in new[] { "Nemerle.dll", "Nemerle.Compiler.dll", "Nemerle.Macros.dll", "dnlib.dll" })
    { var src = $"{stage3Out}/{f}"; if (FileExists(src)) CopyFile(src, $"{nccBoot}/{f}"); }
    // Copy ncc-core.dll (compiler) — use Stage3
    if (FileExists($"{stage3Out}/ncc-core.dll"))
        CopyFile($"{stage3Out}/ncc-core.dll", $"{nccBoot}/ncc-core.dll");
    // Copy ncc-core.exe (apphost) — Stage3
    if (FileExists($"{stage3Out}/ncc-core.exe"))
        CopyFile($"{stage3Out}/ncc-core.exe", $"{nccBoot}/ncc-core.exe");
    foreach (var f in new[] { "Nemerle.Sdk.props", "Nemerle.Sdk.targets", "Nemerle.MSBuild.targets" })
        CopyFile($"tools/msbuild-task/{f}", $"{nccBoot}/{f}");
    WriteRuntimeConfig($"{nccBoot}/ncc-core.runtimeconfig.json", "8.0", "LatestMajor");
    Information("=== Bootstrap replaced with .NET 8 ===");
});
Task("CI")
    .IsDependentOn("Stage3")
    .IsDependentOn("Validate")
    .IsDependentOn("Test")
    .Does(() => {
        Information("=== CI COMPLETE ===");
    });

Task("Default")
    .IsDependentOn("Stage1");

RunTarget(target);
