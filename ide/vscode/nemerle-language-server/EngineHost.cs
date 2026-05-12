using Microsoft.Extensions.Logging;
using Nemerle.Compiler;

namespace Nemerle.LanguageServer;

public class EngineHost
{
    private readonly List<string> _referencePaths;
    private readonly ILogger _logger;
    private static readonly string TempDir = Path.Combine(Path.GetTempPath(), "nemerle-lsp");
    private static readonly string[] _frameworkAssemblies;
    private static readonly string? _frameworkDir;
    private Nemerle.Compiler.ManagerClass? _lastManager;
    private string? _lastUri;
    private string? _lastText;

    static EngineHost()
    {
        // Discover .NET framework assemblies so the compiler can resolve System.* types.
        // On .NET 8+, most types live in System.Private.CoreLib.dll directly.
        // Include all System.*.dll — duplicate type forwards produce warnings, not errors.
        _frameworkDir = Path.GetDirectoryName(typeof(object).Assembly.Location);
        if (_frameworkDir != null && Directory.Exists(_frameworkDir))
        {
            _frameworkAssemblies = Directory.GetFiles(_frameworkDir, "System.*.dll")
                .Where(f => !f.EndsWith(".Native.dll", StringComparison.OrdinalIgnoreCase))
                .ToArray();
        }
        else
        {
            _frameworkAssemblies = Array.Empty<string>();
        }
    }

    public EngineHost(IEnumerable<string>? referencePaths = null, ILogger? logger = null)
    {
        _referencePaths = referencePaths?.ToList() ?? new List<string>();
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        Directory.CreateDirectory(TempDir);
    }

    public List<Diagnostic> GetDiagnostics(string uri, string text)
    {
        _logger.LogDebug("GetDiagnostics: uri={Uri}, textLen={Len}, refs={Refs}",
            uri, text.Length, string.Join(";", _referencePaths.Select(Path.GetFileName)));

        var messages = new List<string>();

        try
        {
            var tempFile = Path.Combine(TempDir, $"__lsp_{Guid.NewGuid():N}_{Path.GetFileName(uri)}");
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

                // Framework references — needed for System.Console, System.Linq, etc.
                foreach (var r in _frameworkAssemblies)
                    options.References.Add(r);

                // User references from .nproj
                foreach (var r in _referencePaths)
                {
                    if (!string.IsNullOrEmpty(r) && System.IO.File.Exists(r))
                        options.References.Add(r);
                }

                _logger.LogDebug("GetDiagnostics: compiling with {Count} refs: {Refs}",
                    options.References.Count, string.Join(";", options.References));

                // Add source from temp file
                options.Sources.Add(FileUtils.GetSource(tempFile));

                var manager = new ManagerClass(options);
                ManagerClass.Instance = manager;
                manager.InitOutput(TextWriter.Null);
                Nemerle.Compiler.CompilerLog.InitLogger(_logger);

                manager.MessageOccured += (loc, msg) =>
                {
                    _logger.LogWarning("Compiler: {Uri}: {Message}", uri, msg ?? "(null)");
                    if (msg != null)
                        lock (messages)
                            messages.Add(msg);
                };

                manager.Run();

                // Cache for IntelliSense queries
                _lastManager = manager;
                _lastUri = uri;
                _lastText = text;
            }
            finally
            {
                try { System.IO.File.Delete(tempFile); } catch { }
            }
        }
        catch (Nemerle.Compiler.Recovery)
        {
            // Recovery is expected — compiler found too many errors and bailed out.
            // Messages collected before bailout are in the list already.
            _logger.LogDebug("GetDiagnostics: Recovery bailout for {Uri}", uri);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetDiagnostics crashed: uri={Uri}", uri);
            messages.Add($"{uri}(1,1): error: Internal error: {ex.GetType().Name}: {ex.Message}");
        }

        // Rewrite temp file paths to the original URI so VSCode can map diagnostics to the document
        RewritePaths(messages, TempDir, uri);

        return ConvertToDiagnostics(messages);
    }

    private static void RewritePaths(List<string> messages, string tempDir, string uri)
    {
        // Decode URI to file path for matching against temp directory
        var decodedUri = uri;
        try { decodedUri = new Uri(uri).LocalPath.Replace('\\', '/'); } catch { }

        for (int i = 0; i < messages.Count; i++)
        {
            var msg = messages[i];
            if (msg.StartsWith(tempDir, StringComparison.OrdinalIgnoreCase))
            {
                // Replace temp path prefix with URI
                var colonIndex = msg.IndexOf('(');
                if (colonIndex > 0)
                {
                    messages[i] = uri + msg[colonIndex..];
                }
            }
        }
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

    public string? GetHoverInfo(string uri, int line, int col)
    {
        if (_lastText == null) return null;

        var word = GetWordAt(_lastText, line, col);
        if (word == null) return null;

        try
        {
            // Try compiler type lookup
            var manager = _lastManager;
            if (manager != null)
            {
                try
                {
                    var ti = manager.LookupTypeInfo(word);
                    if (ti != null)
                        return $"**`{word}`** — *type*\n\n{ti}";
                }
                catch { }

                try
                {
                    var exactOpt = manager.NameTree?.LookupExactType(word);
                    if (exactOpt != null)
                    {
                        var val = exactOpt.GetType().GetProperty("Value")?.GetValue(exactOpt);
                        if (val != null)
                            return $"**`{word}`** — *compiler type*\n\n{val}";
                    }
                }
                catch { }
            }

            // Fallback: try System.Type
            var sysType = System.Type.GetType(word, throwOnError: false);
            if (sysType == null)
                sysType = System.Type.GetType("System." + word, throwOnError: false);
            if (sysType != null)
                return $"**`{sysType.FullName}`** — *type*\n\nAssembly: `{sysType.Assembly.GetName().Name}`";

            return $"**`{word}`** — (compiler not available)";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GetHoverInfo failed for {Word}", word);
            return null;
        }
    }

    public PositionResult? GetDefinitionLocation(string uri, int line, int col)
    {
        if (_lastManager == null || _lastUri != uri || _lastText == null)
            return null;

        var word = GetWordAt(_lastText, line, col);
        if (word == null) return null;

        try
        {
            // Look up as type
            var typeInfo = _lastManager.LookupTypeInfo(word);
            if (typeInfo != null)
            {
                var loc = typeInfo.NameLocation;
                if (loc != null && loc.Line > 0)
                    return new PositionResult(uri, loc.Line - 1, loc.Column - 1);
                return null;
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    private static string? GetWordAt(string text, int line0, int col0)
    {
        var lines = text.Split('\n');
        if (line0 >= lines.Length) return null;
        var line = lines[line0];
        if (col0 >= line.Length) return null;

        // Find word boundaries
        int start = col0;
        while (start > 0 && (char.IsLetterOrDigit(line[start - 1]) || line[start - 1] == '_'))
            start--;
        int end = col0;
        while (end < line.Length && (char.IsLetterOrDigit(line[end]) || line[end] == '_'))
            end++;
        if (end <= start) return null;

        return line[start..end];
    }

    public record PositionResult(string Uri, int Line, int Column);
}
