using System.IO;
using System.Windows;
using DeskCal.Core;

namespace DeskCal.App;

public partial class App : Application
{
    private Mutex? _singleInstance;
    private TrayIcon? _tray;
    private MainWindow? _calendar;

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
        // OAuth 클라이언트 파일은 설치마다 하나, 토큰은 계정 결합 정보와 함께 DB 안에(§5.1).
        var google = new GoogleAccount(database,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeskCal", "client_secret.json"));
        // 창이 다시 만들어지는 사이(3초)에는 _calendar가 비어 있으므로 메뉴는 아무것도 하지 않거나 바로 끝낸다.
        _tray = new TrayIcon(google, () => _calendar?.IsAdjusting == true, () => _calendar?.ToggleAdjust(), () => _calendar?.ShowSettings(),
            () => { if (_calendar is null) Shutdown(); else _calendar.Exit(); });
        ShowCalendar(database);
    }

    // 창은 바탕화면 창에 붙어 있어 Explorer가 다시 시작되면 함께 닫힐 수 있다. 사용자가 종료한 게 아니면 잠시 뒤 다시 띄운다.
    private void ShowCalendar(Database database)
    {
        var window = new MainWindow(database);
        _calendar = window;
        window.Closed += (_, _) =>
        {
            _calendar = null;
            if (window.ExitRequested) Shutdown();
            else Dispatcher.InvokeAsync(async () => { await Task.Delay(TimeSpan.FromSeconds(3)); ShowCalendar(database); });
        };
        MainWindow = window;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
