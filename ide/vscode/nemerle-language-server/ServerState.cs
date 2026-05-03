using Nemerle.LanguageServer.ProjectSystem;

namespace Nemerle.LanguageServer;

public class ServerState
{
    private readonly Dictionary<string, OpenDocument> _documents = new();
    private readonly object _lock = new();
    private readonly EngineHost _engine;
    private readonly CompletionEngine _completionEngine;
    private string? _rootPath;

    public ServerState()
    {
        _engine = new EngineHost();
        _completionEngine = new CompletionEngine();
    }

    public void SetWorkspaceRoot(string? rootUri)
    {
        if (rootUri != null && rootUri.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                _rootPath = Uri.UnescapeDataString(new Uri(rootUri).LocalPath);
            }
            catch { }
        }
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

        var lines = doc.Text.Split('\n');
        if (position.Line >= lines.Length)
            return Task.FromResult<Hover?>(null);

        var line = lines[position.Line];

        // Get diagnostics for this position to show as hover
        var diags = _engine.GetDiagnostics(doc.Uri, doc.Text);
        var lineDiags = diags.Where(d =>
            d.Range.Start.Line == (int)position.Line
            && d.Severity == DiagnosticSeverity.Error)
            .Select(d => $"- **{d.Severity}**: {d.Message}")
            .ToList();

        var md = $"`{line.Trim()}`\n\nLine {position.Line + 1}, Col {position.Character + 1}";
        if (lineDiags.Count > 0)
            md += "\n\n### Errors\n" + string.Join("\n", lineDiags);

        return Task.FromResult<Hover?>(new Hover(md));
    }

    public Task<List<Location>> GetDefinitionAsync(string uri, Position position)
        => Task.FromResult(new List<Location>());
}

internal record OpenDocument(string Uri, string Text, int Version);
