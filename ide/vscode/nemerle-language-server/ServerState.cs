using Nemerle.LanguageServer.ProjectSystem;

namespace Nemerle.LanguageServer;

public class ServerState
{
    private readonly Dictionary<string, OpenDocument> _documents = new();
    private readonly object _lock = new();
    private EngineHost _engine;
    private readonly CompletionEngine _completionEngine;
    private readonly AnalysisEngine _analysisEngine;
    private string? _rootPath;

    public ServerState()
    {
        _engine = new EngineHost();
        _completionEngine = new CompletionEngine();
        _analysisEngine = new AnalysisEngine();
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
    }

    public void AddDocument(string uri, string text, int version)
    {
        lock (_lock)
            _documents[uri] = new OpenDocument(uri, text, version);
    }

    public void UpdateDocument(string uri, string text, int version)
    {
        lock (_lock)
        {
            if (_documents.TryGetValue(uri, out var doc))
                _documents[uri] = doc with { Text = text, Version = version };
        }
    }

    public void RemoveDocument(string uri)
    {
        lock (_lock)
            _documents.Remove(uri);
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

        return Task.Run(() => _completionEngine.GetCompletions(doc.Text, position));
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

        // Find definition of the word under cursor
        var defs = word != null ? _analysisEngine.FindDefinitions(doc.Text, word) : new List<Location>();
        var md = $"`{line.Trim()}`\n\nLine {position.Line + 1}, Col {position.Character + 1}";
        if (word != null)
            md += $"\n\n**Identifier:** `{word}`";
        if (defs.Count > 0)
        {
            md += $"\n\n**Defined at line {defs[0].Range.Start.Line + 1}**";
        }

        // Show diagnostics on this line
        var diags = _engine.GetDiagnostics(doc.Uri, doc.Text);
        var lineDiags = diags.Where(d =>
            d.Range.Start.Line == (int)position.Line)
            .Select(d => $"- **{d.Severity}**: {d.Message}")
            .ToList();
        if (lineDiags.Count > 0)
            md += "\n\n### Messages\n" + string.Join("\n", lineDiags);

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

        var word = _analysisEngine.GetWordAtPosition(doc.Text, position);
        if (word == null) return Task.FromResult(new List<Location>());

        var defs = _analysisEngine.FindDefinitions(doc.Text, word);
        return Task.FromResult(defs);
    }
}

internal record OpenDocument(string Uri, string Text, int Version);
