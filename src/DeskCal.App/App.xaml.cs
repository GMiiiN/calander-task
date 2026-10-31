using System.IO;
using System.Windows;
using DeskCal.Core;

namespace DeskCal.App;

public partial class App : Application
{
    private Mutex? _singleInstance;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // 단일 인스턴스(§8). 이미 떠 있으면 조용히 끝낸다.
        _singleInstance = new Mutex(true, @"Local\DeskCal.App.SingleInstance", out var isFirst);
        if (!isFirst)
        {
            Shutdown();
            return;
        }
        // MCP 서버와 같은 DB를 쓴다(§2.3).
        var path = Environment.GetEnvironmentVariable("DESKCAL_DB")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeskCal", "deskcal.db");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var database = new Database(path);
        database.Initialize();
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        ShowCalendar(database);
    }

    // 창은 바탕화면 창에 붙어 있어 Explorer가 다시 시작되면 함께 닫힐 수 있다. 사용자가 종료한 게 아니면 잠시 뒤 다시 띄운다.
    private void ShowCalendar(Database database)
    {
        var window = new MainWindow(database);
        window.Closed += (_, _) =>
        {
            if (window.ExitRequested) Shutdown();
            else Dispatcher.InvokeAsync(async () => { await Task.Delay(TimeSpan.FromSeconds(3)); ShowCalendar(database); });
        };
        MainWindow = window;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
