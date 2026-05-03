using System.Text.Json;

namespace Nemerle.LanguageServer;

public class NemerleLanguageServer
{
    private readonly LspTransport _transport;
    private readonly Serilog.ILogger _logger;
    private readonly ServerState _state;
    private readonly Dictionary<string, Func<LspRequest, CancellationToken, Task>> _handlers = new();

    public NemerleLanguageServer(LspTransport transport, Serilog.ILogger logger)
    {
        _transport = transport;
        _logger = logger;
        _state = new ServerState();

        RegisterHandlers();
    }

    private void RegisterHandlers()
    {
        _handlers["initialize"] = HandleInitializeAsync;
        _handlers["initialized"] = HandleInitializedAsync;
        _handlers["textDocument/didOpen"] = HandleDidOpenAsync;
        _handlers["textDocument/didClose"] = HandleDidCloseAsync;
        _handlers["textDocument/didChange"] = HandleDidChangeAsync;
        _handlers["textDocument/didSave"] = HandleDidSaveAsync;
        _handlers["textDocument/completion"] = HandleCompletionAsync;
        _handlers["textDocument/hover"] = HandleHoverAsync;
        _handlers["textDocument/definition"] = HandleDefinitionAsync;
        _handlers["shutdown"] = HandleShutdownAsync;
        _handlers["exit"] = HandleExitAsync;
    }

    public async Task RunAsync(CancellationToken ct = default)
    {
        _logger.Information("Server ready, waiting for requests");

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var request = await _transport.ReadRequestAsync(ct);
                _ = HandleRequestAsync(request, ct);
            }
            catch (EndOfStreamException)
            {
                _logger.Information("Client disconnected");
                break;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error reading request");
            }
        }
    }

    private async Task HandleRequestAsync(LspRequest request, CancellationToken ct)
    {
        try
        {
            if (_handlers.TryGetValue(request.Method, out var handler))
            {
                await handler(request, ct);
            }
            else
            {
                _logger.Warning("Unknown method: {Method}", request.Method);
                await _transport.SendResponseAsync(request.Id, new { }, ct);
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Error handling {Method}", request.Method);
            try
            {
                await _transport.SendResponseAsync(request.Id, new { error = new { code = -1, message = ex.Message } }, ct);
            }
            catch { }
        }
    }

    private async Task HandleInitializeAsync(LspRequest request, CancellationToken ct)
    {
        var initParams = ((JsonElement)request.Params!).Deserialize<InitializeParams>(_jsonOpts)!;

        var result = new InitializeResult
        {
            Capabilities = new ServerCapabilities
            {
                HoverProvider = true,
                CompletionProvider = new CompletionOptions(TriggerCharacters: [".", "("]),
                DefinitionProvider = true,
                ReferencesProvider = true,
                DocumentSymbolProvider = true,
                SignatureHelpProvider = new SignatureHelpOptions(TriggerCharacters: ["(", ","]),
                TextDocumentSync = new TextDocumentSyncOptions { OpenClose = true, Change = TextDocumentSyncKind.Full }
            }
        };

        await _transport.SendResponseAsync(request.Id, result, ct);
        _logger.Information("Initialized for root: {RootPath}", initParams.RootPath);
    }

    private Task HandleInitializedAsync(LspRequest request, CancellationToken ct)
    {
        _logger.Information("Client initialized");
        return Task.CompletedTask;
    }

    private async Task HandleDidOpenAsync(LspRequest request, CancellationToken ct)
    {
        var p = ((JsonElement)request.Params!).Deserialize<DidOpenTextDocumentParams>(_jsonOpts)!;
        _state.AddDocument(p.TextDocument.Uri, p.TextDocument.Text, p.TextDocument.Version);
        await PublishDiagnosticsAsync(p.TextDocument.Uri, ct);
    }

    private Task HandleDidCloseAsync(LspRequest request, CancellationToken ct)
    {
        var p = ((JsonElement)request.Params!).Deserialize<DidCloseTextDocumentParams>(_jsonOpts)!;
        _state.RemoveDocument(p.TextDocument.Uri);
        return Task.CompletedTask;
    }

    private async Task HandleDidChangeAsync(LspRequest request, CancellationToken ct)
    {
        var p = ((JsonElement)request.Params!).Deserialize<DidChangeTextDocumentParams>(_jsonOpts)!;
        foreach (var change in p.ContentChanges)
            _state.UpdateDocument(p.TextDocument.Uri, change.Text, p.TextDocument.Version);

        await PublishDiagnosticsAsync(p.TextDocument.Uri, ct);
    }

    private async Task HandleDidSaveAsync(LspRequest request, CancellationToken ct)
    {
        var p = ((JsonElement)request.Params!).Deserialize<DidSaveTextDocumentParams>(_jsonOpts)!;
        await PublishDiagnosticsAsync(p.TextDocument.Uri, ct);
    }

    private async Task HandleCompletionAsync(LspRequest request, CancellationToken ct)
    {
        var p = ((JsonElement)request.Params!).Deserialize<CompletionParams>(_jsonOpts)!;
        var items = await _state.GetCompletionAsync(p.TextDocument.Uri, p.Position);
        await _transport.SendResponseAsync(request.Id, new CompletionList(false, items.ToArray()), ct);
    }

    private async Task HandleHoverAsync(LspRequest request, CancellationToken ct)
    {
        var p = ((JsonElement)request.Params!).Deserialize<HoverParams>(_jsonOpts)!;
        var hover = await _state.GetHoverAsync(p.TextDocument.Uri, p.Position);
        await _transport.SendResponseAsync(request.Id, hover, ct);
    }

    private async Task HandleDefinitionAsync(LspRequest request, CancellationToken ct)
    {
        var p = ((JsonElement)request.Params!).Deserialize<DefinitionParams>(_jsonOpts)!;
        var locations = await _state.GetDefinitionAsync(p.TextDocument.Uri, p.Position);
        await _transport.SendResponseAsync(request.Id, locations.ToArray(), ct);
    }

    private Task HandleShutdownAsync(LspRequest request, CancellationToken ct)
    {
        return _transport.SendResponseAsync(request.Id, null, ct);
    }

    private Task HandleExitAsync(LspRequest request, CancellationToken ct)
    {
        Environment.Exit(0);
        return Task.CompletedTask;
    }

    private async Task PublishDiagnosticsAsync(string uri, CancellationToken ct)
    {
        var diags = await _state.GetDiagnosticsAsync(uri);
        await _transport.SendNotificationAsync("textDocument/publishDiagnostics",
            new PublishDiagnosticsParams(uri, diags.ToArray()), ct);
    }

    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };
}
