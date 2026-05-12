using Microsoft.Extensions.Logging;

namespace Nemerle.LanguageServer;

internal sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _path;
    private StreamWriter? _writer;

    public FileLoggerProvider(string path) => _path = path;

    public ILogger CreateLogger(string categoryName) => new FileLogger(categoryName, this);

    public StreamWriter GetWriter()
    {
        if (_writer == null)
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            _writer = new StreamWriter(_path, append: true) { AutoFlush = true };
        }
        return _writer;
    }

    public void Dispose() => _writer?.Dispose();
}

internal sealed class FileLogger : ILogger
{
    private readonly string _category;
    private readonly FileLoggerProvider _provider;

    public FileLogger(string category, FileLoggerProvider provider)
    {
        _category = category;
        _provider = provider;
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        var writer = _provider.GetWriter();
        var message = formatter(state, exception);
        var timestamp = DateTime.UtcNow.ToString("o");
        writer.WriteLine($"[{timestamp}] [{logLevel}] [{_category}] {message}");
        if (exception != null)
            writer.WriteLine($"  EXCEPTION: {exception}");
    }
}
