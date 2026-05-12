using System.Text;
using Microsoft.Extensions.Logging;

namespace Nemerle.LanguageServer;

public class LoggerTextWriter : TextWriter
{
    private readonly ILogger _logger;
    private readonly LogLevel _level;

    public LoggerTextWriter(ILogger logger, LogLevel level = LogLevel.Debug)
    {
        _logger = logger;
        _level = level;
    }

    public override Encoding Encoding => Encoding.UTF8;

    public override void WriteLine(string? value)
    {
        if (!string.IsNullOrEmpty(value))
            _logger.Log(_level, 0, default, value, null, (s, _) => s!);
    }

    public override void Write(string? value)
    {
        if (!string.IsNullOrEmpty(value))
            _logger.Log(_level, 0, default, value, null, (s, _) => s!);
    }
}
