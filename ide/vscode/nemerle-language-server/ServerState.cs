using Nemerle.LanguageServer.ProjectSystem;

namespace Nemerle.LanguageServer;

public class ServerState
{
    private readonly Dictionary<string, OpenDocument> _documents = new();
    private readonly object _lock = new();
    private readonly EngineHost _engine;
    private string? _rootPath;

    public ServerState()
    {
        _engine = new EngineHost();
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
        var items = new List<CompletionItem>
        {
            new() { Label = "def", Kind = CompletionItemKind.Keyword, Detail = "Define a function or value", InsertText = "def $0" },
            new() { Label = "mutable", Kind = CompletionItemKind.Keyword, Detail = "Mutable variable modifier", InsertText = "mutable $0" },
            new() { Label = "class", Kind = CompletionItemKind.Keyword, Detail = "Class declaration", InsertText = "class $0\n{\n}" },
            new() { Label = "module", Kind = CompletionItemKind.Keyword, Detail = "Module declaration", InsertText = "module $0\n{\n}" },
            new() { Label = "using", Kind = CompletionItemKind.Keyword, Detail = "Import namespace", InsertText = "using $0;" },
            new() { Label = "variant", Kind = CompletionItemKind.Keyword, Detail = "Variant type declaration", InsertText = "variant $0\n{\n}" },
            new() { Label = "match", Kind = CompletionItemKind.Keyword, Detail = "Pattern match expression", InsertText = "match ($0)\n{\n}" },
            new() { Label = "fun", Kind = CompletionItemKind.Keyword, Detail = "Lambda expression", InsertText = "fun($0)" },
            new() { Label = "namespace", Kind = CompletionItemKind.Keyword, Detail = "Namespace declaration", InsertText = "namespace $0\n{\n}" },
            new() { Label = "when", Kind = CompletionItemKind.Keyword, Detail = "Guard clause in match", InsertText = "when ($0)" },
        };
        return Task.FromResult(items);
    }

    public Task<Hover?> GetHoverAsync(string uri, Position position)
    {
        var doc = GetDocument(uri);
        if (doc == null) return Task.FromResult<Hover?>(null);

        var lines = doc.Text.Split('\n');
        if (position.Line < lines.Length)
        {
            return Task.FromResult<Hover?>(new Hover(
                $"`{lines[position.Line].Trim()}`\n\nLine {position.Line + 1}, Col {position.Character + 1}"));
        }
        return Task.FromResult<Hover?>(null);
    }

    public Task<List<Location>> GetDefinitionAsync(string uri, Position position)
        => Task.FromResult(new List<Location>());
}

internal record OpenDocument(string Uri, string Text, int Version);
