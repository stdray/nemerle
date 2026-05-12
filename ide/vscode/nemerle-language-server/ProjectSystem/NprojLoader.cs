using Microsoft.Build.Evaluation;

namespace Nemerle.LanguageServer.ProjectSystem;

public record NprojInfo
{
    public string ProjectPath { get; init; } = "";
    public string? OutputType { get; init; }
    public string? AssemblyName { get; init; }
    public string? RootNamespace { get; init; }
    public string? TargetFramework { get; init; }
    public bool NoStdLib { get; init; } = true;
    public string? DefineConstants { get; init; }
    public List<string> AssemblyReferences { get; init; } = new();
    public List<string> ProjectReferences { get; init; } = new();
    public List<string> MacroProjectReferences { get; init; } = new();
    public List<PackageRef> PackageReferences { get; init; } = new();
    public List<string> CompilePatterns { get; init; } = new();
    public string SdkBinPath { get; init; } = "";
}

public record PackageRef(string Name, string Version);

public static class NprojLoader
{
    public static NprojInfo Load(string nprojPath)
    {
        var projectDir = Path.GetDirectoryName(Path.GetFullPath(nprojPath))!;

        try
        {
            var project = new Project(nprojPath);
            try
            {
                var result = new NprojInfo
                {
                    ProjectPath = projectDir,
                    OutputType = project.GetPropertyValue("OutputType"),
                    AssemblyName = project.GetPropertyValue("AssemblyName"),
                    RootNamespace = project.GetPropertyValue("RootNamespace"),
                    TargetFramework = project.GetPropertyValue("TargetFramework"),
                    DefineConstants = project.GetPropertyValue("DefineConstants"),
                    NoStdLib = ParseBool(project.GetPropertyValue("NoStdLib"), true),
                    SdkBinPath = project.GetPropertyValue("Nemerle") ?? "",
                };

                // Read Reference items
                foreach (var item in project.GetItems("Reference"))
                {
                    var hintPath = item.GetMetadataValue("HintPath");
                    if (!string.IsNullOrEmpty(hintPath))
                        result.AssemblyReferences.Add(Path.GetFullPath(Path.Combine(projectDir, hintPath)));
                    else
                        result.AssemblyReferences.Add(item.EvaluatedInclude);
                }

                // Read ProjectReference items
                foreach (var item in project.GetItems("ProjectReference"))
                {
                    var path = Path.GetFullPath(Path.Combine(projectDir, item.EvaluatedInclude));
                    result.ProjectReferences.Add(path);
                }

                // Read MacroProjectReference items
                foreach (var item in project.GetItems("MacroProjectReference"))
                {
                    var path = Path.GetFullPath(Path.Combine(projectDir, item.EvaluatedInclude));
                    result.MacroProjectReferences.Add(path);
                }

                // Read PackageReference items
                foreach (var item in project.GetItems("PackageReference"))
                {
                    var version = item.GetMetadataValue("Version");
                    if (!string.IsNullOrEmpty(version))
                        result.PackageReferences.Add(new PackageRef(item.EvaluatedInclude, version));
                }

                // Read Compile items
                foreach (var item in project.GetItems("Compile"))
                {
                    result.CompilePatterns.Add(item.EvaluatedInclude);
                }

                // Add standard Nemerle references from SDK path
                if (result.NoStdLib && !string.IsNullOrEmpty(result.SdkBinPath))
                {
                    var nemerleBin = result.SdkBinPath;
                    if (!Path.IsPathRooted(nemerleBin))
                        nemerleBin = Path.GetFullPath(Path.Combine(projectDir, nemerleBin));
                    result.AssemblyReferences.Add(Path.Combine(nemerleBin, "Nemerle.dll"));
                    result.AssemblyReferences.Add(Path.Combine(nemerleBin, "Nemerle.Compiler.dll"));
                    result.AssemblyReferences.Add(Path.Combine(nemerleBin, "Nemerle.Macros.dll"));
                    result.AssemblyReferences.Add(Path.Combine(nemerleBin, "dnlib.dll"));
                }

                return result;
            }
            finally { project.ProjectCollection.Dispose(); }
        }
        catch (Microsoft.Build.Exceptions.InvalidProjectFileException)
        {
            // MSBuild evaluation failed (SDK not found), fall back to raw XML parsing
        }

        // Fallback: read items from raw XML without MSBuild evaluation
        var fallback = new NprojInfo { ProjectPath = projectDir };
        try
        {
            var doc = System.Xml.Linq.XDocument.Load(nprojPath);
            var ns = doc.Root?.GetDefaultNamespace() ?? System.Xml.Linq.XNamespace.None;

            // Compile items
            var compileItems = doc.Root?.Elements(ns + "ItemGroup")
                .SelectMany(g => g.Elements(ns + "Compile"))
                .Select(e => e.Attribute("Include")?.Value)
                .Where(v => v != null)
                .Select(v => v!);
            if (compileItems != null)
                foreach (var item in compileItems)
                    fallback.CompilePatterns.Add(item);

            // Reference items
            var refItems = doc.Root?.Elements(ns + "ItemGroup")
                .SelectMany(g => g.Elements(ns + "Reference"))
                .Select(e => e.Attribute("Include")?.Value)
                .Where(v => v != null)
                .Select(v => v!);
            if (refItems != null)
                foreach (var item in refItems)
                    fallback.AssemblyReferences.Add(item);
        }
        catch { }

        // Add Nemerle SDK references from the extension bin
        var extBin = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".vscode", "extensions", "nemerle.nemerle-vscode-0.1.0", "bin");
        if (Directory.Exists(extBin))
        {
            fallback.AssemblyReferences.Add(Path.Combine(extBin, "Nemerle.dll"));
            fallback.AssemblyReferences.Add(Path.Combine(extBin, "Nemerle.Compiler.dll"));
            fallback.AssemblyReferences.Add(Path.Combine(extBin, "Nemerle.Macros.dll"));
            fallback.AssemblyReferences.Add(Path.Combine(extBin, "dnlib.dll"));
        }

        return fallback;
    }

    public static List<string> ResolveReferences(NprojInfo info)
    {
        var resolved = new List<string>();
        foreach (var r in info.AssemblyReferences)
        {
            if (File.Exists(r))
                resolved.Add(r);
            else
                resolved.Add(r); // Keep unresolved, compiler will report errors
        }
        return resolved;
    }

    /// <summary>
    /// Resolves ProjectReference and MacroProjectReference .nproj paths to
    /// their output DLL paths. Returns (assemblies, macros) tuples.
    /// Convention: {projectDir}\bin\{configuration}\{assemblyName}.dll
    /// </summary>
    public static (List<string> Assemblies, List<string> Macros) ResolveProjectReferences(
        NprojInfo info, string configuration = "Release")
    {
        var assemblies = new List<string>();
        var macros = new List<string>();

        foreach (var projPath in info.ProjectReferences)
        {
            var dll = ResolveProjectOutput(projPath, configuration);
            if (dll != null && File.Exists(dll))
                assemblies.Add(dll);
        }

        foreach (var projPath in info.MacroProjectReferences)
        {
            var dll = ResolveProjectOutput(projPath, configuration);
            if (dll != null && File.Exists(dll))
                macros.Add(dll);
        }

        return (assemblies, macros);
    }

    private static string? ResolveProjectOutput(string nprojPath, string configuration)
    {
        if (!File.Exists(nprojPath)) return null;

        try
        {
            var project = new Project(nprojPath);
            try
            {
                var assemblyName = project.GetPropertyValue("AssemblyName")
                    ?? Path.GetFileNameWithoutExtension(nprojPath);
                var outputType = (project.GetPropertyValue("OutputType") ?? "Library")
                    .Trim().ToLowerInvariant();

                var ext = outputType is "exe" or "winexe" ? ".exe" : ".dll";
                var projectDir = Path.GetDirectoryName(Path.GetFullPath(nprojPath))!;
                return Path.GetFullPath(Path.Combine(projectDir, "bin", configuration, $"{assemblyName}{ext}"));
            }
            finally
            {
                project.ProjectCollection.UnloadAllProjects();
            }
        }
        catch { return null; }
    }

    private static bool ParseBool(string? v, bool def)
    {
        return v?.Trim().ToLowerInvariant() switch
        {
            "true" => true,
            "false" => false,
            _ => def
        };
    }
}
