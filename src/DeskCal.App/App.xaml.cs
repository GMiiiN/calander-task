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
        MainWindow = new MainWindow(database);
        MainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
