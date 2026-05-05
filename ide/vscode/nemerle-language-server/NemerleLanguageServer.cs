using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Nemerle.LanguageServer;

public class NemerleLanguageServer
{
    private readonly LspTransport _transport;
    private readonly ILogger _logger;
    private readonly ServerState _state;
    private readonly Dictionary<string, Func<LspRequest, CancellationToken, Task>> _handlers = new();

    public NemerleLanguageServer(LspTransport transport, Serilog.ILogger serilogLogger)
    {
        _transport = transport;
        var factory = new SerilogLoggerFactory(serilogLogger);
        _logger = factory.CreateLogger("NemerleLanguageServer");
        _state = new ServerState(factory.CreateLogger<ServerState>());

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
        _handlers["textDocument/signatureHelp"] = HandleSignatureHelpAsync;
        _handlers["textDocument/references"] = HandleReferencesAsync;
        _handlers["textDocument/semanticTokens/full"] = HandleSemanticTokensAsync;
        _handlers["textDocument/documentSymbol"] = HandleDocumentSymbolAsync;
        _handlers["nemerle/compile"] = HandleCompileAsync;
        _handlers["nemerle/compileRun"] = HandleCompileRunAsync;
        _handlers["nemerle/macroExpand"] = HandleMacroExpandAsync;
        _handlers["shutdown"] = HandleShutdownAsync;
        _handlers["exit"] = HandleExitAsync;
    }

    public async Task RunAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("Server ready, waiting for requests");

        var pendingTasks = new List<Task>();

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var request = await _transport.ReadRequestAsync(ct);

                var task = HandleRequestAsync(request, ct);
                pendingTasks.Add(task);

                // Clean up completed tasks
                pendingTasks.RemoveAll(t => t.IsCompleted);
            }
            catch (EndOfStreamException)
            {
                _logger.LogInformation("Client disconnected");
                break;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error reading request");
            }
        }

        // Wait for pending handlers to finish (with a timeout)
        if (pendingTasks.Count > 0)
        {
            try
            {
                await Task.WhenAll(pendingTasks).WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch (TimeoutException)
            {
                _logger.LogWarning("Some handlers did not complete within timeout");
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
                _logger.LogWarning("Unknown method: {Method}", request.Method);
                await _transport.SendResponseAsync(request.Id, new { }, ct);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling {Method}", request.Method);
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
        _logger.LogInformation("Initialize: rootUri={RootUri}, rootPath={RootPath}, params={Params}",
            initParams.RootUri, initParams.RootPath, request.Params!.ToString());

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
        _logger.LogInformation("Initialized for root: {RootPath}", initParams.RootPath);
        _state.SetWorkspaceRoot(initParams.RootUri ?? initParams.RootPath);
    }

    private Task HandleInitializedAsync(LspRequest request, CancellationToken ct)
    {
        _logger.LogInformation("Client initialized");
        return Task.CompletedTask;
    }

    private async Task HandleDidOpenAsync(LspRequest request, CancellationToken ct)
    {
        var p = ((JsonElement)request.Params!).Deserialize<DidOpenTextDocumentParams>(_jsonOpts)!;
        _logger.LogInformation("didOpen {Uri} version={Version}", p.TextDocument.Uri, p.TextDocument.Version);
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
        // Unconditional log
        System.IO.File.AppendAllText(
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "nemerle-lsp", "hover-entry.log"),
            $"{DateTime.Now:HH:mm:ss.fff} HandleHoverAsync called id={request.Id}\n");
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

    private async Task HandleDocumentSymbolAsync(LspRequest request, CancellationToken ct)
    {
        var p = ((JsonElement)request.Params!).Deserialize<DocumentSymbolParams>(_jsonOpts)!;
        var symbols = await _state.GetDocumentSymbolsAsync(p.TextDocument.Uri);

        // Convert SymbolInfo to LSP-friendly format
        var result = symbols.Select(s => new
        {
            name = s.Name,
            kind = (int)s.Kind,
            range = new { start = new { line = s.Range.Start.Line, character = s.Range.Start.Character }, 
                           end = new { line = s.Range.End.Line, character = s.Range.End.Character } },
            selectionRange = new { start = new { line = s.SelectionRange.Start.Line, character = s.SelectionRange.Start.Character },
                                    end = new { line = s.SelectionRange.End.Line, character = s.SelectionRange.End.Character } }
        }).ToArray();

        await _transport.SendResponseAsync(request.Id, result, ct);
    }

    private async Task HandleSignatureHelpAsync(LspRequest request, CancellationToken ct)
    {
        var p = ((JsonElement)request.Params!).Deserialize<SignatureHelpParams>(_jsonOpts)!;
        var result = await _state.GetSignatureHelpAsync(p.TextDocument.Uri, p.Position);
        await _transport.SendResponseAsync(request.Id, result, ct);
    }

    private async Task HandleReferencesAsync(LspRequest request, CancellationToken ct)
    {
        var p = ((JsonElement)request.Params!).Deserialize<ReferenceParams>(_jsonOpts)!;
        var refs = await _state.GetReferencesAsync(p.TextDocument.Uri, p.Position);
        await _transport.SendResponseAsync(request.Id, refs.ToArray(), ct);
    }

    private async Task HandleSemanticTokensAsync(LspRequest request, CancellationToken ct)
    {
        var p = ((JsonElement)request.Params!).Deserialize<SemanticTokensParams>(_jsonOpts)!;
        var tokens = await _state.GetSemanticTokensAsync(p.TextDocument.Uri);
        await _transport.SendResponseAsync(request.Id, new { data = tokens }, ct);
    }

    private async Task HandleCompileAsync(LspRequest request, CancellationToken ct)
    {
        var p = ((JsonElement)request.Params!).Deserialize<CompileParams>(_jsonOpts)!;
        var doc = _state.GetDocumentByUri(p.TextDocument.Uri);
        if (doc == null)
        {
            await _transport.SendResponseAsync(request.Id, new { success = false, diagnostics = new object[0], output = "File not open" }, ct);
            return;
        }

        var diags = await _state.GetDiagnosticsAsync(p.TextDocument.Uri);
        var errors = diags.Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        var success = errors.Count == 0;

        await _transport.SendResponseAsync(request.Id, new
        {
            success,
            diagnostics = diags.Select(d => new
            {
                severity = d.Severity?.ToString(),
                message = d.Message,
                line = d.Range.Start.Line + 1,
                col = d.Range.Start.Character + 1
            }).ToArray(),
            output = success ? "Compilation successful" : $"Compilation failed with {errors.Count} error(s)",
            errorCount = errors.Count
        }, ct);
    }

    private async Task HandleMacroExpandAsync(LspRequest request, CancellationToken ct)
    {
        var p = ((JsonElement)request.Params!).Deserialize<CompileParams>(_jsonOpts)!;
        var lines = p.TextDocument.Uri.Split('?');
        string? expanded = null;
        if (lines.Length > 1)
        {
            var qs = System.Web.HttpUtility.ParseQueryString(lines[1]);
            if (int.TryParse(qs["line"], out var line) && int.TryParse(qs["col"], out var col))
            {
                var doc = _state.GetDocumentByUri(p.TextDocument.Uri.Split('?')[0]);
                if (doc != null)
                {
                    var hint = _state.GetHoverRaw(doc.Uri, new Position(line, col));
                    // Extract expanded text from hint XML
                    var m = System.Text.RegularExpressions.Regex.Match(hint ?? "",
                        @"<hint\s+value\s*=\s*'After expanding[^']*'\s*>\s*<code>\s*<pre>(.*?)</pre>",
                        System.Text.RegularExpressions.RegexOptions.Singleline);
                    expanded = m.Success ? m.Groups[1].Value : (hint ?? "");
                }
            }
        }
        await _transport.SendResponseAsync(request.Id, new { text = expanded ?? "" }, ct);
    }

    private async Task HandleCompileRunAsync(LspRequest request, CancellationToken ct)
    {
        var p = ((JsonElement)request.Params!).Deserialize<CompileParams>(_jsonOpts)!;
        var doc = _state.GetDocumentByUri(p.TextDocument.Uri);
        if (doc == null)
        {
            await _transport.SendResponseAsync(request.Id, new { success = false, output = "File not open" }, ct);
            return;
        }

        var result = await Task.Run(() =>
        {
            try
            {
                // Compile to temp exe and run
                var tempDir = Path.Combine(Path.GetTempPath(), "nemerle-run", Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(tempDir);
                var sourceFile = Path.Combine(tempDir, "program.n");
                var outputExe = Path.Combine(tempDir, "program.exe");
                File.WriteAllText(sourceFile, doc.Text);

                // Use ncc to compile
                var compilerDir = Path.GetDirectoryName(typeof(Nemerle.Compiler.ManagerClass).Assembly.Location)!;
                var nccPath = Path.Combine(compilerDir, "ncc-core.exe");
                var useExe = File.Exists(nccPath);

                var args = new List<string>();
                if (useExe)
                {
                    args.Add($"\"{sourceFile}\"");
                    args.Add($"-out:\"{outputExe}\"");
                    args.Add("-target:exe");
                    args.Add("-nostdlib");
                    args.Add("-nowarn:10003");
                    args.Add("-greedy-references:-");
                }
                else
                {
                    nccPath = "dotnet";
                    args.Add($"\"{Path.Combine(compilerDir, "ncc-core.dll")}\"");
                    args.Add($"\"{sourceFile}\"");
                    args.Add($"-out:\"{outputExe}\"");
                    args.Add("-target:exe");
                    args.Add("-nostdlib");
                    args.Add("-nowarn:10003");
                    args.Add("-greedy-references:-");
                }

                // Add framework refs
                foreach (var r in new[] { "System.Runtime", "System.Console", "System.Collections", "System.IO.FileSystem", "System.Linq" })
                {
                    var rpath = Path.Combine(compilerDir, r + ".dll");
                    if (File.Exists(rpath)) args.Add($"-r:\"{rpath}\"");
                }
                foreach (var r in new[] { "Nemerle.dll", "dnlib.dll" })
                {
                    var rpath = Path.Combine(compilerDir, r);
                    if (File.Exists(rpath)) args.Add($"-r:\"{rpath}\"");
                }

                var psi = new System.Diagnostics.ProcessStartInfo(nccPath, string.Join(" ", args))
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = tempDir
                };

                using var proc = System.Diagnostics.Process.Start(psi)!;
                proc.WaitForExit(30000);
                var compileOutput = proc.StandardOutput.ReadToEnd() + proc.StandardError.ReadToEnd();

                if (proc.ExitCode != 0 || !File.Exists(outputExe))
                {
                    try { Directory.Delete(tempDir, true); } catch { }
                    return (false, compileOutput);
                }

                // Run the compiled exe
                var runPsi = new System.Diagnostics.ProcessStartInfo("dotnet", $"\"{outputExe}\"")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = tempDir
                };

                using var runProc = System.Diagnostics.Process.Start(runPsi)!;
                runProc.WaitForExit(10000);
                var runOutput = "=== Compilation output ===\n" + compileOutput + "\n=== Program output ===\n" + runProc.StandardOutput.ReadToEnd();
                var runError = runProc.StandardError.ReadToEnd();
                if (!string.IsNullOrEmpty(runError)) runOutput += "\n=== stderr ===\n" + runError;

                try { Directory.Delete(tempDir, true); } catch { }
                return (true, runOutput);
            }
            catch (Exception ex)
            {
                return (false, $"Internal error: {ex.Message}");
            }
        });

        await _transport.SendResponseAsync(request.Id, new { success = result.Item1, output = result.Item2 }, ct);
    }

    private Task HandleShutdownAsync(LspRequest request, CancellationToken ct)
    {
        _logger.LogInformation("Shutdown requested");
        return _transport.SendResponseAsync(request.Id, null, ct);
    }

    private Task HandleExitAsync(LspRequest request, CancellationToken ct)
    {
        _logger.LogInformation("Exit requested");
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

/// <summary>
/// Bridges Serilog to Microsoft.Extensions.Logging.ILogger.
/// </summary>
public class SerilogLoggerFactory : ILoggerFactory
{
    private readonly Serilog.ILogger _serilog;

    public SerilogLoggerFactory(Serilog.ILogger serilog) { _serilog = serilog; }

    public void AddProvider(ILoggerProvider provider) { }
    public Microsoft.Extensions.Logging.ILogger CreateLogger(string categoryName)
        => new SerilogLogger(_serilog.ForContext("SourceContext", categoryName));

    public void Dispose() { }
}

public class SerilogLogger : Microsoft.Extensions.Logging.ILogger
{
    private readonly Serilog.ILogger _logger;
    public SerilogLogger(Serilog.ILogger logger) { _logger = logger; }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        var level = logLevel switch
        {
            LogLevel.Trace => Serilog.Events.LogEventLevel.Verbose,
            LogLevel.Debug => Serilog.Events.LogEventLevel.Debug,
            LogLevel.Information => Serilog.Events.LogEventLevel.Information,
            LogLevel.Warning => Serilog.Events.LogEventLevel.Warning,
            LogLevel.Error => Serilog.Events.LogEventLevel.Error,
            LogLevel.Critical => Serilog.Events.LogEventLevel.Fatal,
            _ => Serilog.Events.LogEventLevel.Information
        };
        _logger.Write(level, exception, formatter(state, exception));
    }
}

