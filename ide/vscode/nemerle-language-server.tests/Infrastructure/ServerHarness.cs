using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Nemerle.LanguageServer.Tests.Infrastructure;

public class ServerHarness : IAsyncDisposable
{
    private readonly JsonSerializerOptions _json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private StreamWriter? _writer;
    private StreamReader? _reader;
    private Task? _serverTask;
    private int _nextId;
    private bool _initialized;

    public async Task InitializeAsync(string rootUri = "file:///test")
    {
        // Server reads from inPipe (PipeDirection.In), writes to outPipe (PipeDirection.Out)
        // Client writes to inPipe, reads from outPipe
        var serverReadPipe = new AnonymousPipeServerStream(PipeDirection.In);
        var serverWritePipe = new AnonymousPipeServerStream(PipeDirection.Out);

        var clientToServer = new AnonymousPipeClientStream(PipeDirection.Out, serverReadPipe.GetClientHandleAsString());
        var serverToClient = new AnonymousPipeClientStream(PipeDirection.In, serverWritePipe.GetClientHandleAsString());
        _writer = new StreamWriter(clientToServer, Encoding.UTF8) { AutoFlush = true };
        _reader = new StreamReader(serverToClient, Encoding.UTF8);

        _serverTask = Task.Run(() =>
        {
            try
            {
                // server reads from serverReadPipe, writes to serverWritePipe
                var transport = new Nemerle.LanguageServer.LspTransport(serverReadPipe, serverWritePipe);
                var server = new Nemerle.LanguageServer.NemerleLanguageServer(transport, Serilog.Log.Logger);
                server.RunAsync().Wait();
            }
            catch (EndOfStreamException) { }
            catch (ObjectDisposedException) { }
        });

        await Task.Delay(200);

        // Send initialize directly
        var id = Interlocked.Increment(ref _nextId);
        var msg = JsonSerializer.Serialize(new { jsonrpc = "2.0", id, method = "initialize", @params = (object?)new { processId = (int?)null, rootUri, capabilities = new { } } }, _json);
        var header = $"Content-Length: {Encoding.UTF8.GetByteCount(msg)}\r\n\r\n{msg}";
        await _writer!.WriteAsync(header);
        await _writer.FlushAsync();

        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!cts.IsCancellationRequested)
        {
            var resp = await ReadLspMessageAsync(cts.Token);
            var node = JsonNode.Parse(resp)!;
            if (node["id"]?.GetValue<int>() == id)
            {
                if (node["result"] != null) break;
                throw new InvalidOperationException($"Initialize failed: {resp}");
            }
        }
        _initialized = true;
    }

    public async Task SendDidOpenAsync(string uri, string text)
    {
        EnsureInitialized();
        await SendNotificationAsync("textDocument/didOpen", new
        {
            textDocument = new { uri, languageId = "nemerle", version = 1, text }
        });
    }

    public async Task<JsonNode> SendRequestAsync(string method, object? @params = null)
    {
        EnsureInitialized();
        var id = Interlocked.Increment(ref _nextId);
        var msg = JsonSerializer.Serialize(new { jsonrpc = "2.0", id, method, @params }, _json);
        var header = $"Content-Length: {Encoding.UTF8.GetByteCount(msg)}\r\n\r\n{msg}";
        await _writer!.WriteAsync(header);
        await _writer.FlushAsync();

        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!cts.IsCancellationRequested)
        {
            var resp = await ReadLspMessageAsync(cts.Token);
            var node = JsonNode.Parse(resp)!;
            if (node["id"]?.GetValue<int>() == id)
                return node["result"] ?? node;
        }
        throw new TimeoutException($"No response for '{method}'");
    }

    public async Task SendNotificationAsync(string method, object? @params = null)
    {
        EnsureInitialized();
        var msg = JsonSerializer.Serialize(new { jsonrpc = "2.0", method, @params }, _json);
        var header = $"Content-Length: {Encoding.UTF8.GetByteCount(msg)}\r\n\r\n{msg}";
        await _writer!.WriteAsync(header);
        await _writer.FlushAsync();
    }

    public async Task<JsonNode?> WaitForDiagnosticsAsync(TimeSpan timeout)
    {
        EnsureInitialized();
        var cts = new CancellationTokenSource(timeout);
        while (!cts.IsCancellationRequested)
        {
            var msg = await ReadLspMessageAsync(cts.Token);
            var node = JsonNode.Parse(msg)!;
            if (node["method"]?.GetValue<string>() == "textDocument/publishDiagnostics")
                return node["params"];
        }
        return null;
    }

    private async Task<string> ReadLspMessageAsync(CancellationToken ct)
    {
        var sb = new StringBuilder();
        int contentLength = -1;

        while (!ct.IsCancellationRequested)
        {
            var line = await ReadLineAsync(ct);
            if (line == null) throw new EndOfStreamException();
            if (line.Length == 0) break;

            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
            {
                var val = line["Content-Length:".Length..].Trim();
                int.TryParse(val, out contentLength);
            }
        }

        if (contentLength <= 0) throw new InvalidOperationException($"Invalid Content-Length: {contentLength}");

        var buffer = new char[contentLength];
        var total = 0;
        while (total < contentLength)
        {
            var read = await _reader!.ReadAsync(buffer.AsMemory(total, contentLength - total));
            if (read == 0) break;
            total += read;
        }
        return new string(buffer, 0, total);
    }

    private async Task<string?> ReadLineAsync(CancellationToken ct)
    {
        var sb = new StringBuilder();
        int prev = -1;
        while (!ct.IsCancellationRequested)
        {
            var ch = _reader!.Read();
            if (ch == -1) return sb.Length > 0 ? sb.ToString() : null;
            if (prev == '\r' && ch == '\n') break;
            if (ch != '\r') sb.Append((char)ch);
            prev = ch;
        }
        return sb.ToString();
    }

    private void EnsureInitialized()
    {
        if (!_initialized) throw new InvalidOperationException("Call InitializeAsync first");
    }

    public async ValueTask DisposeAsync()
    {
        if (_initialized)
        {
            try { await SendRequestAsync("shutdown"); } catch { }
            try { await SendNotificationAsync("exit"); } catch { }
        }
        try { await Task.WhenAny(_serverTask ?? Task.CompletedTask, Task.Delay(2000)); } catch { }
        _writer?.Dispose();
        _reader?.Dispose();
    }
}
