using DeskCal.Core;
using DeskCal.Mcp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

if (args.Length > 0 && args[0] is "--register-claude-desktop" or "--unregister-claude-desktop")
{
    try
    {
        if (args.Length != 2) throw new ArgumentException($"Usage: {args[0]} <config_path>");
        if (args[0] == "--unregister-claude-desktop") ClaudeDesktopConfig.Unregister(args[1]);
        else ClaudeDesktopConfig.Register(args[1], Environment.ProcessPath
            ?? throw new InvalidOperationException("Cannot determine the MCP executable path."));
    }
    catch (Exception error)
    {
        Console.Error.WriteLine($"Claude Desktop {args[0]} failed: {error.Message}");
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
var database = new Database(path, Console.Error.WriteLine);
Console.Error.WriteLine($"MCP {database.DiagnosticIdentity}");
database.Initialize();
var secret = Environment.GetEnvironmentVariable("DESKCAL_CLIENT_SECRET") ??
    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeskCal", "client_secret.json");
var coordinator = new SyncCoordinator(database, new GoogleAccount(database, secret), Console.Error.WriteLine);
var tools = new CalendarTools(database, ct => coordinator.RunAsync(false, ct));
builder.Services.AddMcpServer().WithStdioServerTransport().WithTools(ToolCatalog.Create(tools));
using var host = builder.Build();
var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
lifetime.ApplicationStarted.Register(() => _ = ResumeAsync());
await host.RunAsync();

async Task ResumeAsync()
{
    try
    {
        // 마지막 쓰기 때 앱이 켜져 있었어도 전송 전에 종료될 수 있다. 살아 있는 MCP가 다음 차례에 인계한다.
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(2));
        do
        {
            try
            {
                if (!AppPresence.IsRunning(database) && PendingChanges.HasToken(database) && PendingChanges.HasUnsent(database))
                    await coordinator.RunAsync(false, lifetime.ApplicationStopping);
            }
            catch (Exception ex) when (!lifetime.ApplicationStopping.IsCancellationRequested)
            {
                Console.Error.WriteLine($"동기화 재개 실패 ({ex.GetType().Name})");
            }
        } while (await timer.WaitForNextTickAsync(lifetime.ApplicationStopping));
    }
    catch (OperationCanceledException) when (lifetime.ApplicationStopping.IsCancellationRequested) { }
}
