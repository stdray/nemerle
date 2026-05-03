using System.Text.RegularExpressions;
using Nemerle.Compiler;

namespace Nemerle.LanguageServer;

public class EngineHost
{
    private readonly List<string> _referencePaths;
    private static readonly string TempDir = Path.Combine(Path.GetTempPath(), "nemerle-lsp");

    public EngineHost(IEnumerable<string> referencePaths)
    {
        _referencePaths = referencePaths.ToList();
        Directory.CreateDirectory(TempDir);
    }

    public List<Diagnostic> GetDiagnostics(string uri, string text)
    {
        var messages = new List<string>();

        try
        {
            // Write text to temp file so compiler can parse it
            var tempFile = Path.Combine(TempDir, $"__lsp_{Path.GetFileName(uri)}");
            System.IO.File.WriteAllText(tempFile, text);

            try
            {
                var options = new CompilationOptions
                {
                    IgnoreConfusion = true,
                    ProgressBar = false,
                    ColorMessages = false,
                    ThrowOnError = false,
                    PersistentLibraries = false,
                    DisableExternalParsers = true,
                    DoNotLoadMacros = false,
                    EmitDebug = false,
                    CompileToMemory = true,
                    EarlyExit = false
                };

                // Set the root namespace from the file name
                options.RootNamespace = Path.GetFileNameWithoutExtension(tempFile);

                // Add source from temp file
                options.Sources.Add(FileUtils.GetSource(tempFile));

                var manager = new ManagerClass(options);
                // Set thread-static instance (Nemerle uses this internally)
                try
                {
                    typeof(Nemerle.Compiler.ManagerClass)
                        .GetField("_instance", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)?
                        .SetValue(null, manager);
                }
                catch { }

                manager.InitOutput(TextWriter.Null);

                manager.MessageOccured += (_, msg) =>
                {
                    lock (messages)
                        messages.Add(msg);
                };

                manager.Run();
            }
            finally
            {
                try { System.IO.File.Delete(tempFile); } catch { }
            }
        }
        catch (Exception ex)
        {
            messages.Add($"{uri}(1,1): error: Internal error: {ex.Message}");
        }

        return ConvertToDiagnostics(messages);
    }

    private static List<Diagnostic> ConvertToDiagnostics(List<string> messages)
    {
        var diags = new List<Diagnostic>();
        var regex = new Regex(@"^(.*?)\((\d+),(\d+)(?:,(\d+),(\d+))?\):\s*(error|warning|hint)(?:\s*(\w+))?:\s*(.+)",
            RegexOptions.Compiled);
        var legacyRegex = new Regex(@"^(.*?):(\d+):(\d+):\s*(error|warning|hint):\s*(.+)",
            RegexOptions.Compiled);

        foreach (var msg in messages)
        {
            var match = regex.Match(msg);
            if (!match.Success) match = legacyRegex.Match(msg);
            if (!match.Success) continue;

            var startLine = int.Parse(match.Groups[2].Value) - 1;
            var startCol = int.Parse(match.Groups[3].Value) - 1;

            int endLine = startLine;
            int endCol = startCol + 1;
            if (match.Groups[4].Success && match.Groups[5].Success)
            {
                endLine = int.Parse(match.Groups[4].Value) - 1;
                endCol = int.Parse(match.Groups[5].Value);
            }

            var severityStr = match.Groups[match.Groups.Count > 6 ? 6 : 4].Value;
            var rawMessage = match.Groups[match.Groups.Count > 6 ? 8 : 5].Value;

            var severity = severityStr switch
            {
                "error" => DiagnosticSeverity.Error,
                "warning" => DiagnosticSeverity.Warning,
                "hint" => DiagnosticSeverity.Hint,
                _ => DiagnosticSeverity.Information
            };

            diags.Add(new Diagnostic
            {
                Range = new Range(
                    new Position(Math.Max(0, startLine), Math.Max(0, startCol)),
                    new Position(Math.Max(0, endLine), Math.Max(0, endCol))),
                Severity = severity,
                Message = rawMessage,
                Source = "Nemerle"
            });
        }

        return diags;
    }
}
