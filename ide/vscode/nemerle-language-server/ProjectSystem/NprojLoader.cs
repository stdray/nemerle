using System.Xml.Linq;
using System.Text.RegularExpressions;

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
    public List<PackageRef> PackageReferences { get; init; } = new();
    public List<string> CompilePatterns { get; init; } = new();
    public string SdkBinPath { get; init; } = "";
}

public record PackageRef(string Name, string Version);

public static class NprojLoader
{
    public static NprojInfo Load(string nprojPath)
    {
        // Expand the .nproj by resolving imports inline
        var expanded = ExpandImports(nprojPath);
        var doc = XDocument.Parse(expanded);
        var result = new NprojInfo { ProjectPath = Path.GetFullPath(nprojPath) };

        // Read PropertyGroup
        foreach (var pg in doc.Descendants("PropertyGroup"))
        {
            ReadProp(pg, "OutputType", v => result = result with { OutputType = v });
            ReadProp(pg, "AssemblyName", v => result = result with { AssemblyName = v });
            ReadProp(pg, "RootNamespace", v => result = result with { RootNamespace = v });
            ReadProp(pg, "TargetFramework", v => result = result with { TargetFramework = v });
            ReadProp(pg, "NoStdLib", v => result = result with { NoStdLib = ParseBool(v, true) });
            ReadProp(pg, "DefineConstants", v => result = result with { DefineConstants = v });
            // The Nemerle SDK bin path: where ncc binaries live, including framework DLLs
            ReadProp(pg, "Nemerle", v => result = result with { SdkBinPath = ResolveNemerlePath(v, nprojPath) });
        }

        var projectDir = Path.GetDirectoryName(nprojPath)!;

        // Read ItemGroup/Reference
        foreach (var refEl in doc.Root!.Elements("ItemGroup").Elements("Reference"))
        {
            var include = refEl.Attribute("Include")?.Value;
            if (string.IsNullOrEmpty(include)) continue;

            // If there's a HintPath, use it as absolute
            var hintPath = refEl.Element("HintPath")?.Value;
            if (!string.IsNullOrEmpty(hintPath))
            {
                result.AssemblyReferences.Add(ResolvePath(hintPath, projectDir));
            }
            else
            {
                // Plain assembly name — try to resolve from .nproj directory or SdkBinPath
                result.AssemblyReferences.Add(include);
            }
        }

        // Read ItemGroup/ProjectReference
        foreach (var refEl in doc.Root.Elements("ItemGroup").Elements("ProjectReference"))
        {
            var include = refEl.Attribute("Include")?.Value;
            if (!string.IsNullOrEmpty(include))
                result.ProjectReferences.Add(ResolvePath(include, projectDir));
        }

        // Read ItemGroup/PackageReference
        foreach (var refEl in doc.Root.Elements("ItemGroup").Elements("PackageReference"))
        {
            var include = refEl.Attribute("Include")?.Value;
            var version = refEl.Attribute("Version")?.Value;
            if (!string.IsNullOrEmpty(include) && !string.IsNullOrEmpty(version))
                result.PackageReferences.Add(new PackageRef(include, version));
        }

        // Read ItemGroup/Compile
        foreach (var compEl in doc.Root.Elements("ItemGroup").Elements("Compile"))
        {
            var include = compEl.Attribute("Include")?.Value;
            if (!string.IsNullOrEmpty(include))
                result.CompilePatterns.Add(include);
        }

        // Add standard framework refs if NoStdLib=false (unusual for Nemerle)
        // Always include Nemerle.dll from SDK path
        if (result.NoStdLib && !string.IsNullOrEmpty(result.SdkBinPath))
        {
            var nemerleBin = ResolvePath(result.SdkBinPath, projectDir);
            result.AssemblyReferences.Add(Path.Combine(nemerleBin, "Nemerle.dll"));
            result.AssemblyReferences.Add(Path.Combine(nemerleBin, "Nemerle.Compiler.dll"));
            result.AssemblyReferences.Add(Path.Combine(nemerleBin, "Nemerle.Macros.dll"));
            result.AssemblyReferences.Add(Path.Combine(nemerleBin, "dnlib.dll"));
        }

        return result;
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

    private static void ReadProp(XElement pg, string name, Action<string> set)
    {
        var el = pg.Element(name);
        if (el != null && !string.IsNullOrWhiteSpace(el.Value))
            set(el.Value);
    }

    private static bool ParseBool(string v, bool def)
    {
        return v?.Trim().ToLowerInvariant() switch
        {
            "true" => true,
            "false" => false,
            _ => def
        };
    }

    private static string ResolveNemerlePath(string value, string nprojPath)
    {
        // $(MSBuildProjectDirectory) → project dir
        var projectDir = Path.GetDirectoryName(nprojPath)!;
        value = value.Replace("$(MSBuildProjectDirectory)", projectDir);
        return ResolvePath(value, projectDir);
    }

    private static string ResolvePath(string path, string baseDir)
    {
        if (string.IsNullOrEmpty(path)) return path;
        if (Path.IsPathRooted(path)) return Path.GetFullPath(path);
        return Path.GetFullPath(Path.Combine(baseDir, path));
    }

    private static string ExpandImports(string nprojPath)
    {
        // Read the .nproj file, resolve <Import> elements by inlining their content
        var projectDir = Path.GetDirectoryName(nprojPath)!;
        var xml = File.ReadAllText(nprojPath);

        // Resolve $(Nemerle) and $(MSBuildProjectDirectory) in import paths
        // Simple approach: extract Nemerle variable from PropertyGroup, then replace
        var nemerlePath = ExtractProperty(nprojPath, "Nemerle");
        if (nemerlePath == null)
            nemerlePath = Path.Combine(projectDir, "boot-dnlib");
        nemerlePath = nemerlePath.Replace("$(MSBuildProjectDirectory)", projectDir);

        // Process all <Import Project="..."> elements
        var importRegex = new System.Text.RegularExpressions.Regex(
            @"<Import\s+Project=\""([^\""]+)\""\s*/?>",
            System.Text.RegularExpressions.RegexOptions.Compiled);

        var expanded = importRegex.Replace(xml, match =>
        {
            var importPath = match.Groups[1].Value;
            importPath = importPath.Replace("$(Nemerle)", nemerlePath);
            importPath = importPath.Replace("$(MSBuildProjectDirectory)", projectDir);

            if (File.Exists(importPath))
            {
                var importedContent = File.ReadAllText(importPath);
                // Remove XML declaration if present
                importedContent = System.Text.RegularExpressions.Regex.Replace(
                    importedContent, @"<\?xml[^?]*\?>", "");
                return importedContent;
            }

            return match.Value; // Keep original if not found
        });

        // Remove <Import> tags that remain (couldn't resolve)
        var cleanImportRegex = new System.Text.RegularExpressions.Regex(
            @"<Import\s+[^>]*/>", System.Text.RegularExpressions.RegexOptions.Compiled);
        expanded = cleanImportRegex.Replace(expanded, "");

        return $"<Project>{expanded}</Project>";
    }

    private static string? ExtractProperty(string nprojPath, string propertyName)
    {
        if (!File.Exists(nprojPath)) return null;
        var xml = File.ReadAllText(nprojPath);

        var regex = new System.Text.RegularExpressions.Regex(
            $@"<{propertyName}[^>]*>(.*?)</{propertyName}>",
            System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.Singleline);

        var m = regex.Match(xml);
        if (m.Success)
        {
            var value = m.Groups[1].Value;
            // Handle Condition attributes — we take the first unconditional default
            // Strip Condition if present
            var condRegex = new System.Text.RegularExpressions.Regex(
                $@"<{propertyName}\s+Condition=""[^""]*""[^>]*>(.*?)</{propertyName}>",
                System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.Singleline);

            var m2 = condRegex.Match(xml);
            if (m2.Success)
            {
                // Conditional — prefer non-conditional default
                foreach (Match m3 in regex.Matches(xml))
                {
                    var el = m3.Value;
                    if (!el.Contains("Condition="))
                        return m3.Groups[1].Value;
                }
                return m2.Groups[1].Value;
            }

            return value;
        }

        return null;
    }
}
