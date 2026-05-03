using Nemerle.LanguageServer;
using Serilog;

// Preload Nemerle assemblies so the compiler can find them
var compilerDir = Path.GetDirectoryName(typeof(Nemerle.Compiler.ManagerClass).Assembly.Location)!;
foreach (var dll in new[] { "Nemerle.dll", "Nemerle.Compiler.dll", "Nemerle.Macros.dll", "dnlib.dll" })
{
    var path = Path.Combine(compilerDir, dll);
    if (File.Exists(path))
        try { System.Reflection.Assembly.LoadFrom(path); } catch { }
}

// Configure logging to file
var logDir = Path.Combine(Path.GetTempPath(), "nemerle-lsp");
Directory.CreateDirectory(logDir);
Log.Logger = new LoggerConfiguration()
    .WriteTo.File(Path.Combine(logDir, "server.log"), rollingInterval: RollingInterval.Day)
    .CreateLogger();

Log.Information("Nemerle Language Server starting");

using var transport = new LspTransport(Console.OpenStandardInput(), Console.OpenStandardOutput());
var server = new NemerleLanguageServer(transport, Log.Logger);

try
{
    await server.RunAsync();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Server crashed");
    return 1;
}
finally
{
    Log.CloseAndFlush();
}

return 0;
