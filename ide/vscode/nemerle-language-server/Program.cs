using System.Reflection;
using Microsoft.Extensions.Logging;
using Nemerle.LanguageServer;
using Seq.Extensions.Logging;

var logFilePath = Environment.GetEnvironmentVariable("NEMERLE_LOG_FILE")
    ?? Path.Combine(Path.GetTempPath(), "nemerle-lsp.log");

var seqUrl = Environment.GetEnvironmentVariable("NEMERLE_SEQ_URL")
    ?? "https://yobalog.3po.su/compat/seq/nemerle-lsp";
var seqKey = Environment.GetEnvironmentVariable("NEMERLE_SEQ_KEY")
    ?? "_hJAqQ2SGUOMfucTWyX36w";

using var loggerFactory = LoggerFactory.Create(builder =>
{
    builder.AddDebug();
    builder.AddProvider(new FileLoggerProvider(logFilePath));
    builder.SetMinimumLevel(LogLevel.Debug);

    if (!string.IsNullOrWhiteSpace(seqUrl))
        builder.AddSeq(serverUrl: seqUrl, apiKey: seqKey);
});

var logger = loggerFactory.CreateLogger("Program");

var asm = Assembly.GetExecutingAssembly();
var asmName = asm.GetName();
var buildTime = System.IO.File.GetLastWriteTime(asm.Location);
var verLine = $"Nemerle Language Server v{asmName.Version} built {buildTime:s} (log: {logFilePath})";
logger.LogInformation(verLine);

// Redirect Debug.WriteLine and Console.Error to logger
var debugLogger = loggerFactory.CreateLogger("Debug");
var errWriter = new LoggerTextWriter(debugLogger, LogLevel.Warning);
Console.SetError(errWriter);

using var transport = new LspTransport(Console.OpenStandardInput(), Console.OpenStandardOutput());
var server = new NemerleLanguageServer(transport, loggerFactory);

try
{
    await server.RunAsync();
}
catch (Exception ex)
{
    logger.LogCritical(ex, "Server crashed");
    return 1;
}

return 0;
