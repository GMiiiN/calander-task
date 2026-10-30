using DeskCal.Core;
using DeskCal.Mcp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

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
