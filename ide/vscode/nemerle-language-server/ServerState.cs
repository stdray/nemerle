using Nemerle.LanguageServer.ProjectSystem;
using Nemerle.Completion2;

namespace Nemerle.LanguageServer;

public class ServerState
{
    private readonly Dictionary<string, OpenDocument> _documents = new();
    private readonly object _lock = new();
    private EngineHost _engine;
    private readonly CompletionEngine _completionEngine;
    private readonly AnalysisEngine _analysisEngine;
    private EngineBridge? _engineBridge;
    private LspIdeProject? _ideProject;
    private string? _rootPath;

    public ServerState()
    {
        _engine = new EngineHost();
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
        if (rootUri != null && rootUri.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                _rootPath = Uri.UnescapeDataString(new Uri(rootUri).LocalPath);
                if (Directory.Exists(_rootPath))
                {
                    var nprojFiles = Directory.GetFiles(_rootPath, "*.nproj", SearchOption.TopDirectoryOnly);
                    if (nprojFiles.Length > 0)
                    {
                        foreach (var nproj in nprojFiles)
                        {
                            try
                            {
                                var info = NprojLoader.Load(nproj);
                                refs.AddRange(NprojLoader.ResolveReferences(info));
                            }
                            catch { }
                        }
                    }
                }
            }
            catch { }
        }

        _engine = new EngineHost(refs);

        // Init engine bridge with refs
        if (_engineBridge == null)
        {
            try
            {
                _ideProject = new LspIdeProject();
                foreach (var r in refs)
                    if (File.Exists(r))
                        _ideProject.AddAssemblyRef(r);
                _engineBridge = new EngineBridge();
                _engineBridge.Initialize(_ideProject);
            }
            catch { _engineBridge = null; }
        }
    }

    public void AddDocument(string uri, string text, int version)
    {
        lock (_lock)
            _documents[uri] = new OpenDocument(uri, text, version);

        try { _engineBridge?.AddOrUpdateDocument(uri, text, version); }
        catch { _engineBridge = null; }
    }

    public void UpdateDocument(string uri, string text, int version)
    {
        lock (_lock)
        {
            if (_documents.TryGetValue(uri, out var doc))
                _documents[uri] = doc with { Text = text, Version = version };
        }
        try { _engineBridge?.AddOrUpdateDocument(uri, text, version); }
        catch { _engineBridge = null; }
    }

    public void RemoveDocument(string uri)
    {
        lock (_lock)
            _documents.Remove(uri);
        try { _engineBridge?.RemoveDocument(uri); }
        catch { }
    }

    private OpenDocument? GetDocument(string uri)
    {
        lock (_lock)
            return _documents.TryGetValue(uri, out var d) ? d : null;
    }

    public Task<List<Diagnostic>> GetDiagnosticsAsync(string uri)
    {
        var doc = GetDocument(uri);
        if (doc == null) return Task.FromResult(new List<Diagnostic>());

        return Task.Run(() => _engine.GetDiagnostics(doc.Uri, doc.Text));
    }

    public Task<List<CompletionItem>> GetCompletionAsync(string uri, Position position)
    {
        var doc = GetDocument(uri);
        if (doc == null) return Task.FromResult(new List<CompletionItem>());

        // Try engine bridge first, fall back to lexical
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
            catch { }

            return _completionEngine.GetCompletions(doc.Text, position);
        });
    }

    public Task<Hover?> GetHoverAsync(string uri, Position position)
    {
        var doc = GetDocument(uri);
        if (doc == null) return Task.FromResult<Hover?>(null);

        var word = _analysisEngine.GetWordAtPosition(doc.Text, position);
        var lines = doc.Text.Split('\n');
        if (position.Line >= lines.Length)
            return Task.FromResult<Hover?>(null);

        var line = lines[position.Line];
        var defs = word != null ? _analysisEngine.FindDefinitions(doc.Text, word, uri) : new List<Location>();
        var md = $"`{line.Trim()}`\n\nLine {position.Line + 1}, Col {position.Character + 1}";
        if (word != null) md += $"\n\n**Identifier:** `{word}`";
        if (defs.Count > 0) md += $"\n\n**Defined at line {defs[0].Range.Start.Line + 1}**";

        var diags = _engine.GetDiagnostics(doc.Uri, doc.Text);
        var lineDiags = diags.Where(d => d.Range.Start.Line == (int)position.Line)
            .Select(d => $"- **{d.Severity}**: {d.Message}").ToList();
        if (lineDiags.Count > 0) md += "\n\n### Messages\n" + string.Join("\n", lineDiags);

        return Task.FromResult<Hover?>(new Hover(md));
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

        // Try engine (real GotoInfo) first
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

                        results.Add(new Location(
                            fileUri,
                            new Range(
                                new Position(Math.Max(0, g.Line - 1), Math.Max(0, g.Column - 1)),
                                new Position(Math.Max(0, g.EndLine - 1), Math.Max(0, g.EndColumn)))));
                    }
                    return Task.FromResult(results);
                }
            }
        }
        catch { }

        // Fallback: lexical search
        var word = _analysisEngine.GetWordAtPosition(doc.Text, position);
        if (word == null) return Task.FromResult(new List<Location>());

        var defs = _analysisEngine.FindDefinitions(doc.Text, word, uri);
        return Task.FromResult(defs);
    }

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
}

internal record OpenDocument(string Uri, string Text, int Version);
