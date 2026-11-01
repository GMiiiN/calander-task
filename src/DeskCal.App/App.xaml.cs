using System.IO;
using System.Windows;
using System.Windows.Threading;
using DeskCal.Core;
using Google.Apis.Calendar.v3;
using Google.Apis.Services;
using Google.Apis.Tasks.v1;

namespace DeskCal.App;

public partial class App : Application
{
    private readonly DispatcherTimer _syncTimer = new() { Interval = TimeSpan.FromMinutes(2) };
    private Mutex? _singleInstance;
    private TrayIcon? _tray;
    private MainWindow? _calendar;
    private bool _isSyncing;

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
        _syncTimer.Tick += async (_, _) => await SyncAsync(database, google);
        _syncTimer.Start();
        _ = SyncAsync(database, google);
    }

    // 동기화(§2.4): 시작 시와 2분마다. 일정·할 일은 매번(push → pull), 공휴일은 하루 1회(§5.6).
    // 로그인 전이거나 실패하면 다음 차례에 다시 한다. 한쪽이 실패해도 나머지는 한다.
    private async Task SyncAsync(Database database, GoogleAccount google)
    {
        if (_isSyncing) return;
        _isSyncing = true;
        try
        {
            if (await google.LoadAsync() is not { } credential) return;
            var initializer = new BaseClientService.Initializer { HttpClientInitializer = credential, ApplicationName = "DeskCal" };
            using var calendar = new CalendarService(initializer);
            using var tasks = new TasksService(initializer);
            await RunAsync("events", () => new EventSync(database, new GoogleCalendarRemote(calendar)).SyncAsync(CancellationToken.None));
            await RunAsync("todos", () => new TaskSync(database, new GoogleTasksRemote(tasks)).SyncAsync(CancellationToken.None));
            var holidays = new HolidayRepository(database);
            if (HolidaySync.IsDue(holidays.LastSyncedAt(), DateTimeOffset.Now))
                await RunAsync("holidays", () => HolidaySync.SyncAsync(calendar, holidays, DateTimeOffset.Now, CancellationToken.None));
            ChangeSignal.Notify();
        }
        catch (Exception exception)
        {
            LogSync($"sign-in failed: {exception.GetType().Name}: {exception.Message}");
        }
        finally
        {
            _isSyncing = false;
        }

        static async Task RunAsync(string name, Func<Task> work)
        {
            try
            {
                await work();
            }
            catch (Exception exception)
            {
                LogSync($"{name} failed: {exception.GetType().Name}: {exception.Message}");
            }
        }
    }

    private static void LogSync(string message)
    {
        try
        {
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeskCal", "logs");
            Directory.CreateDirectory(directory);
            File.AppendAllText(Path.Combine(directory, "sync.log"), $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // 기록 실패로 동기화를 멈추지 않는다.
        }
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
