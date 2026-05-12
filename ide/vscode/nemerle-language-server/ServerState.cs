using Microsoft.Extensions.Logging;
using Nemerle.LanguageServer.ProjectSystem;
using Nemerle.Completion2;

namespace Nemerle.LanguageServer;

public class ServerState
{
    private readonly Dictionary<string, OpenDocument> _documents = new();
    private readonly object _lock = new();
    private readonly ILogger<ServerState> _logger;
    private EngineHost _engine;
    private readonly CompletionEngine _completionEngine;
    private readonly AnalysisEngine _analysisEngine;
    private EngineBridge? _engineBridge;
    private LspIdeProject? _ideProject;
    private string? _rootPath;
    private readonly List<NprojInfo> _projectInfos = new();

    public ServerState(ILogger<ServerState> logger)
    {
        _logger = logger;
        _engine = new EngineHost(logger: _logger);
        _completionEngine = new CompletionEngine();
        _analysisEngine = new AnalysisEngine();
    }

    private void EnsureEngineBridge()
    {
        if (_engineBridge == null)
        {
            _ideProject = new LspIdeProject();
            _engineBridge = new EngineBridge();
            _engineBridge.Initialize(_ideProject);
        }
    }

    public void SetWorkspaceRoot(string? rootUri)
    {
        var refs = new List<string>();
        var macroRefsList = new List<string>();
        if (rootUri != null && rootUri.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var rawPath = Uri.UnescapeDataString(rootUri);
                if (rawPath.StartsWith("file:///", StringComparison.OrdinalIgnoreCase))
                    rawPath = rawPath[8..]; // remove "file:///" prefix
                _rootPath = Path.GetFullPath(rawPath);
                _logger.LogInformation("Workspace root: {RootPath}", _rootPath);
                if (Directory.Exists(_rootPath))
                {
                    var nprojFiles = Directory.GetFiles(_rootPath, "*.nproj", SearchOption.AllDirectories);
                    _logger.LogInformation("Found {Count} .nproj files", nprojFiles.Length);
                    if (nprojFiles.Length > 0)
                    {
                        foreach (var nproj in nprojFiles)
                        {
                            try
                            {
                                var info = NprojLoader.Load(nproj);
                                _projectInfos.Add(info);
                                refs.AddRange(NprojLoader.ResolveReferences(info));
                                var (projRefs, macroRefs) = NprojLoader.ResolveProjectReferences(info);
                                refs.AddRange(projRefs);
                                macroRefsList.AddRange(macroRefs);
                                _logger.LogInformation("Loaded .nproj: {Path} (+{ProjRefs} project refs, +{MacroRefs} macro refs)",
                                    nproj, projRefs.Count, macroRefs.Count);
                            }
                            catch (Exception ex) { _logger.LogWarning(ex, "Failed to load .nproj: {Path}", nproj); }
                        }
                    }
                }
            }
            catch (Exception ex) { _logger.LogError(ex, "SetWorkspaceRoot failed"); }
        }

        _engine = new EngineHost(refs, _logger);

        if (_engineBridge == null)
        {
            try
            {
                _ideProject = new Nemerle.Completion2.LspIdeProject();
                foreach (var r in refs)
                    if (File.Exists(r))
                        _ideProject.AddAssemblyRef(r);
                foreach (var r in macroRefsList)
                    if (File.Exists(r))
                        _ideProject.AddMacroAssemblyRef(r);
                _engineBridge = new Nemerle.Completion2.EngineBridge();
                _engineBridge.Initialize(_ideProject);
                _logger.LogInformation("EngineBridge initialized successfully");

                // Load all project source files into the engine for cross-file macro resolution
                var totalFiles = 0;
                foreach (var info in _projectInfos)
                {
                    var resolved = ResolveCompilePatterns(info.ProjectPath, info.CompilePatterns);
                    _logger.LogInformation("Project {Path} Compile patterns: {Patterns} → {Count} files",
                        info.ProjectPath, string.Join(";", info.CompilePatterns), resolved.Count);
                    foreach (var file in resolved)
                    {
                        try
                        {
                            var text = File.ReadAllText(file);
                            var uri = new Uri(file).ToString();
                            _engineBridge.AddOrUpdateDocument(uri, text, 0);
                            totalFiles++;
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to add project source: {File}", file);
                        }
                    }
                }
                _logger.LogInformation("Loaded {TotalFiles} project source files into engine", totalFiles);
                if (totalFiles > 0)
                {
                    _engineBridge.DebugDumpSources();
                    try { _engineBridge.RebuildProject(); }
                    catch (Exception ex) { _logger.LogWarning(ex, "RebuildProject failed"); }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "EngineBridge initialization FAILED");
                _engineBridge = null;
            }
        }
    }

    public void AddDocument(string uri, string text, int version)
    {
        lock (_lock)
            _documents[uri] = new OpenDocument(uri, text, version);

        EnsureEngineBridge();
        try { _engineBridge?.AddOrUpdateDocument(uri, text, version); }
        catch (Exception ex) { _logger.LogWarning(ex, "AddOrUpdateDocument failed for {Uri}", uri); }
    }

    public void UpdateDocument(string uri, string text, int version)
    {
        lock (_lock)
        {
            if (_documents.TryGetValue(uri, out var doc))
                _documents[uri] = doc with { Text = text, Version = version };
        }
        try { _engineBridge?.AddOrUpdateDocument(uri, text, version); }
        catch (Exception ex) { _logger.LogWarning(ex, "UpdateDocument failed for {Uri}", uri); }
    }

    public void RemoveDocument(string uri)
    {
        lock (_lock)
            _documents.Remove(uri);
        try { _engineBridge?.RemoveDocument(uri); }
        catch (Exception ex) { _logger.LogWarning(ex, "RemoveDocument failed for {Uri}", uri); }
    }

    private OpenDocument? GetDocument(string uri)
    {
        lock (_lock)
            return _documents.TryGetValue(uri, out var d) ? d : null;
    }

    public OpenDocument? GetDocumentByUri(string uri) => GetDocument(uri);

    public string? GetHoverRaw(string uri, Position position)
    {
        var doc = GetDocument(uri);
        if (doc == null || _engineBridge?.Ready != true) return null;
        try { return _engineBridge.GetHoverText(uri, (int)position.Line, (int)position.Character); }
        catch { return null; }
    }

    public Task<List<Diagnostic>> GetDiagnosticsAsync(string uri)
    {
        var diags = new List<Diagnostic>();

        // Project-level diagnostics from EngineBridge
        try
        {
            if (_engineBridge != null && _engineBridge.Ready)
            {
                var msgs = _engineBridge.GetDiagnostics(uri);
                if (msgs != null && msgs.Length > 0)
                {
                    _logger.LogDebug("EngineBridge diagnostics for {Uri}: {Count} messages", uri, msgs.Length);
                    foreach (var m in msgs)
                        diags.Add(new Diagnostic
                        {
                            Range = new Range(new Position(0, 0), new Position(0, 1)),
                            Severity = DiagnosticSeverity.Error,
                            Message = m,
                            Source = "Nemerle"
                        });
                }
            }
        }
        catch (Exception ex) { _logger.LogWarning(ex, "EngineBridge.GetDiagnostics failed for {Uri}", uri); }

        // Single-file diagnostics from EngineHost as fallback
        if (_documents.TryGetValue(uri, out var doc))
            diags.AddRange(_engine.GetDiagnostics(doc.Uri, doc.Text));

        return Task.FromResult(diags);
    }

    public Task<List<CompletionItem>> GetCompletionAsync(string uri, Position position)
    {
        var doc = GetDocument(uri);
        if (doc == null) return Task.FromResult(new List<CompletionItem>());

        return Task.Run(() =>
        {
            try
            {
                if (_engineBridge?.Ready == true)
                {
                    var elems = _engineBridge.Complete(uri, (int)position.Line, (int)position.Character);
                    if (elems != null && elems.Length > 0)
                        return MapCompletionElems(elems);
                }
            }
            catch (Exception ex) { _logger.LogWarning(ex, "Engine completion failed"); }

            return _completionEngine.GetCompletions(doc.Text, position);
        });
    }

    public Task<Hover?> GetHoverAsync(string uri, Position position)
    {
        _logger.LogDebug("Hover requested: {Uri} ({Line},{Col}) ready={Ready}", 
            uri, position.Line, position.Character, _engineBridge?.Ready);

        var doc = GetDocument(uri);
        if (doc == null) return Task.FromResult<Hover?>(null);

        // Try compiler-based hover from cached ManagerClass
        try
        {
            var hoverMd = _engine.GetHoverInfo(uri, (int)position.Line, (int)position.Character);
            if (!string.IsNullOrEmpty(hoverMd))
                return Task.FromResult<Hover?>(new Hover(hoverMd));
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Compiler hover failed"); }

        // Fallback: lexical
        var word = _analysisEngine.GetWordAtPosition(doc.Text, position);
        var lines = doc.Text.Split('\n');
        if (position.Line >= lines.Length) return Task.FromResult<Hover?>(null);

        var line = lines[position.Line];
        var defs = word != null ? _analysisEngine.FindDefinitions(doc.Text, word, uri) : new List<Location>();

        var md = new System.Text.StringBuilder();
        md.AppendLine($"`{line.Trim()}`");
        md.AppendLine();
        if (word != null)
        {
            if (defs.Count > 0)
                md.AppendLine($"**`{word}`** defined at line {defs[0].Range.Start.Line + 1}");
            else
                md.AppendLine($"Identifier: `{word}` (no definition found in this file)");
        }
        md.AppendLine($"*Line {position.Line + 1}:{position.Character + 1}*");

        var diags = _engine.GetDiagnostics(doc.Uri, doc.Text);
        var lineDiags = diags.Where(d => d.Range.Start.Line == (int)position.Line)
            .Select(d => $"- **{d.Severity}**: {d.Message}").ToList();
        if (lineDiags.Count > 0)
        {
            md.AppendLine();
            md.AppendLine("### Messages");
            foreach (var d in lineDiags) md.AppendLine(d);
        }

        return Task.FromResult<Hover?>(new Hover(md.ToString()));
    }

    public Task<List<SymbolInfo>> GetDocumentSymbolsAsync(string uri)
    {
        var doc = GetDocument(uri);
        if (doc == null) return Task.FromResult(new List<SymbolInfo>());
        return Task.Run(() => _analysisEngine.GetDocumentSymbols(doc.Text));
    }

    public Task<List<Location>> GetDefinitionAsync(string uri, Position position)
    {
        var doc = GetDocument(uri);
        if (doc == null) return Task.FromResult(new List<Location>());

        try
        {
            if (_engineBridge?.Ready == true)
            {
                var gotos = _engineBridge.GetDefinitions(uri, (int)position.Line, (int)position.Character);
                if (gotos != null && gotos.Length > 0)
                {
                    var results = new List<Location>();
                    foreach (var g in gotos)
                    {
                        if (g == null) continue;
                        var fileUri = _engineBridge.GetFileUri(g.FileIndex);
                        if (string.IsNullOrEmpty(fileUri) && !string.IsNullOrEmpty(g.FilePath))
                            fileUri = "file:///" + g.FilePath.Replace('\\', '/');
                        if (string.IsNullOrEmpty(fileUri)) fileUri = uri;
                        results.Add(new Location(fileUri, new Range(
                            new Position(Math.Max(0, g.Line - 1), Math.Max(0, g.Column - 1)),
                            new Position(Math.Max(0, g.EndLine - 1), Math.Max(0, g.EndColumn)))));
                    }
                    return Task.FromResult(results);
                }
            }
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Engine definition failed"); }

        var word = _analysisEngine.GetWordAtPosition(doc.Text, position);
        if (word == null) return Task.FromResult(new List<Location>());
        return Task.FromResult(_analysisEngine.FindDefinitions(doc.Text, word, uri));
    }

    public Task<object?> GetSignatureHelpAsync(string uri, Position position)
    {
        try
        {
            if (_engineBridge?.Ready == true)
            {
                var tip = _engineBridge.GetMethodTip(uri, (int)position.Line, (int)position.Character);
                if (tip != null && tip.HasTip)
                {
                    var sigs = new List<object>();
                    var count = tip.GetCount();
                    for (int i = 0; i < count; i++)
                    {
                        var parms = new List<object>();
                        var paramCount = tip.GetParameterCount(i);
                        for (int p = 0; p < paramCount; p++)
                            parms.Add(new { label = $"param{p + 1}", documentation = (string?)null });
                        sigs.Add(new
                        {
                            label = $"{tip.GetName(i)}({tip.GetDescription(i) ?? tip.GetType(i)})",
                            documentation = tip.GetDescription(i) ?? tip.GetType(i),
                            parameters = parms.ToArray()
                        });
                    }
                    return Task.FromResult<object?>(new
                    {
                        signatures = sigs.ToArray(),
                        activeSignature = tip.DefaultMethod,
                        activeParameter = tip.ParameterIndex
                    });
                }
            }
        }
        catch { }
        return Task.FromResult<object?>(null);
    }

    public Task<List<Location>> GetReferencesAsync(string uri, Position position)
        => Task.FromResult(new List<Location>());

    public Task<List<int>> GetSemanticTokensAsync(string uri)
        => Task.FromResult(new List<int>());

    private static List<CompletionItem> MapCompletionElems(Nemerle.Completion2.CompletionElem[] elems)
    {
        var items = new List<CompletionItem>();
        foreach (var e in elems)
        {
            if (e == null) continue;
            items.Add(new CompletionItem
            {
                Label = e.DisplayName,
                Kind = GlyphToKind(e.GlyphType),
                Detail = e.Info,
                InsertText = e.DisplayName
            });
        }
        return items;
    }

    private static CompletionItemKind GlyphToKind(int glyph)
    {
        return glyph switch
        {
            0 => CompletionItemKind.Class,
            1 => CompletionItemKind.Method,
            2 => CompletionItemKind.Property,
            3 => CompletionItemKind.Field,
            4 => CompletionItemKind.Enum,
            5 => CompletionItemKind.Interface,
            6 => CompletionItemKind.Module,
            7 => CompletionItemKind.Variable,
            8 => CompletionItemKind.Keyword,
            _ => CompletionItemKind.Text
        };
    }

    public static List<string> ResolveCompilePatterns(string projectDir, List<string> patterns)
    {
        var files = new List<string>();
        foreach (var pattern in patterns)
        {
            var normalized = pattern.Replace('\\', '/');
            var searchPattern = Path.GetFileName(normalized);
            var relativeDir = Path.GetDirectoryName(normalized)?.Replace('/', Path.DirectorySeparatorChar) ?? ".";
            var searchDir = Path.GetFullPath(Path.Combine(projectDir, relativeDir));
            var searchOption = normalized.Contains("**") ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;

            if (!Directory.Exists(searchDir))
                continue;

            foreach (var file in Directory.GetFiles(searchDir, searchPattern, searchOption))
            {
                if (file.EndsWith(".n", StringComparison.OrdinalIgnoreCase) && !files.Contains(file))
                    files.Add(file);
            }
        }
        return files;
    }
}

public record OpenDocument(string Uri, string Text, int Version);

