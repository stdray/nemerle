using Microsoft.Extensions.Logging;
using Nemerle.LanguageServer;
using Seq.Extensions.Logging;

// Preload Nemerle assemblies so the compiler can find them
var compilerDir = Path.GetDirectoryName(typeof(Nemerle.Compiler.ManagerClass).Assembly.Location)!;
foreach (var dll in new[] { "Nemerle.dll", "Nemerle.Compiler.dll", "Nemerle.Macros.dll", "dnlib.dll" })
{
    var path = Path.Combine(compilerDir, dll);
    if (File.Exists(path))
        try { System.Reflection.Assembly.LoadFrom(path); } catch { }
}

// Configure logging: Debug (stdout via Debug.WriteLine) + Seq
var seqUrl = Environment.GetEnvironmentVariable("NEMERLE_SEQ_URL")
    ?? "http://yobalog.3po.su/compat/seq";
var seqKey = Environment.GetEnvironmentVariable("NEMERLE_SEQ_KEY")
    ?? "wE7zqtHYoEqsC0AjiXD75A";

using var loggerFactory = LoggerFactory.Create(builder =>
{
    builder.AddDebug();
    builder.SetMinimumLevel(LogLevel.Debug);

    if (!string.IsNullOrWhiteSpace(seqUrl))
        builder.AddSeq(serverUrl: seqUrl, apiKey: seqKey);
});

var logger = loggerFactory.CreateLogger("Program");
logger.LogInformation("Nemerle Language Server starting");

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
