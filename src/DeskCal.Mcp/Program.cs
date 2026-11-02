using DeskCal.Core;
using DeskCal.Mcp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

if (args.Length > 0 && args[0] == "--register-claude-desktop")
{
    try
    {
        if (args.Length != 2) throw new ArgumentException("Usage: --register-claude-desktop <config_path>");
        ClaudeDesktopConfig.Register(args[1], Environment.ProcessPath
            ?? throw new InvalidOperationException("Cannot determine the MCP executable path."));
    }
    catch (Exception error)
    {
        Console.Error.WriteLine($"Claude Desktop registration failed: {error.Message}");
        Environment.ExitCode = 1;
    }
    return;
}

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
var path = Environment.GetEnvironmentVariable("DESKCAL_DB")
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeskCal", "deskcal.db");
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
var database = new Database(path);
database.Initialize();
var tools = new CalendarTools(database);
builder.Services.AddMcpServer().WithStdioServerTransport().WithTools(ToolCatalog.Create(tools));
await builder.Build().RunAsync();
