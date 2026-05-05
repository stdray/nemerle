using System.Text;
using System.Text.Json;

namespace Nemerle.LanguageServer;

public class LspTransport : IDisposable
{
    private readonly StreamReader _reader;
    private readonly StreamWriter _writer;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly object _writeLock = new();

    public LspTransport(Stream input, Stream output)
    {
        _reader = new StreamReader(input, new UTF8Encoding(false));
        _writer = new StreamWriter(output, new UTF8Encoding(false)) { AutoFlush = true };
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };
    }

    public async Task<LspRequest> ReadRequestAsync(CancellationToken ct = default)
    {
        while (!ct.IsCancellationRequested)
        {
            var contentLength = await ReadHeadersAsync(ct);
            if (contentLength == null) throw new EndOfStreamException();

            var buffer = new char[contentLength.Value];
            var totalRead = 0;
            while (totalRead < contentLength.Value)
            {
                var read = await _reader.ReadAsync(buffer, totalRead, contentLength.Value - totalRead);
                if (read == 0) throw new EndOfStreamException();
                totalRead += read;
            }

            var json = new string(buffer, 0, totalRead);
            return JsonSerializer.Deserialize<LspRequest>(json, _jsonOptions)!;
        }

        throw new OperationCanceledException(ct);
    }

    private async Task<int?> ReadHeadersAsync(CancellationToken ct)
    {
        int? contentLength = null;
        while (true)
        {
            var line = await _reader.ReadLineAsync(ct);
            if (line == null) return null;
            if (line.Length == 0) break;

            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
            {
                var val = line["Content-Length:".Length..].Trim();
                if (int.TryParse(val, out var len) && len >= 0)
                    contentLength = len;
            }
        }
        return contentLength;
    }

    public async Task SendResponseAsync(int id, object? result, CancellationToken ct = default)
    {
        var response = new { jsonrpc = "2.0", id, result };
        await WriteMessageAsync(response, ct);
    }

    public async Task SendNotificationAsync(string method, object? @params = null, CancellationToken ct = default)
    {
        var notification = new { jsonrpc = "2.0", method, @params };
        await WriteMessageAsync(notification, ct);
    }

    private async Task WriteMessageAsync(object message, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(message, _jsonOptions);
        var content = $"Content-Length: {Encoding.UTF8.GetByteCount(json)}\r\n\r\n{json}";

        lock (_writeLock)
        {
            _writer.Write(content);
            _writer.Flush();
        }
        await Task.CompletedTask;
    }

    public void Dispose()
    {
        _reader.Dispose();
        _writer.Dispose();
    }
}
