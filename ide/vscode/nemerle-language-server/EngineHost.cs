using Nemerle.Compiler;

namespace Nemerle.LanguageServer;

public class EngineHost
{
    private static readonly string TempDir = Path.Combine(Path.GetTempPath(), "nemerle-lsp");

    public EngineHost()
    {
        Directory.CreateDirectory(TempDir);
    }

    public List<Diagnostic> GetDiagnostics(string uri, string text)
    {
        var messages = new List<string>();

        try
        {
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

                // Like HostedNcc: add compiler dir to library path
                var compilerDir = Path.GetDirectoryName(typeof(ManagerClass).Assembly.Location)!;
                options.LibraryPaths.Add(compilerDir);

                // Add source from temp file
                options.Sources.Add(FileUtils.GetSource(tempFile));

                var manager = new ManagerClass(options);
                ManagerClass.Instance = manager;
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
        var regex = new System.Text.RegularExpressions.Regex(
            @"^(.*?)\((\d+),(\d+)(?:,(\d+),(\d+))?\):\s*(error|warning|hint)(?:\s*(\w+))?:\s*(.+)",
            System.Text.RegularExpressions.RegexOptions.Compiled);
        var legacyRegex = new System.Text.RegularExpressions.Regex(
            @"^(.*?):(\d+):(\d+):\s*(error|warning|hint):\s*(.+)",
            System.Text.RegularExpressions.RegexOptions.Compiled);

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
