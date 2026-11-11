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
    // MCP가 연달아 여러 번 쓸 수 있으므로 신호가 3초 동안 조용해지면 한 번만 동기화한다.
    private readonly DispatcherTimer _quickSyncTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private Mutex? _singleInstance;
    private TrayIcon? _tray;
    private MainWindow? _calendar;
    private bool _isSyncing;
    private bool _syncAgain;
    private string _lastSyncStartedAt = "";
    private bool _started;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // 처리되지 않은 예외로 바탕화면 달력이 조용히 사라지지 않게 한다. 기록하고 계속 실행한다.
        // 단, 시작(DB 초기화 등)이 끝나기 전의 예외는 삼키지 않는다. 창·트레이 없이 단일 인스턴스 잠금만 쥔 프로세스가 남기 때문이다.
        DispatcherUnhandledException += (_, args) =>
        {
            LogError(_started ? "ui" : "startup", args.Exception);
            args.Handled = _started;
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            LogError("task", args.Exception);
            args.SetObserved();
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) => LogError("fatal", args.ExceptionObject as Exception);
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
        _quickSyncTimer.Tick += async (_, _) =>
        {
            _quickSyncTimer.Stop();
            if (PendingChanges.ExistSince(database, _lastSyncStartedAt)) await SyncAsync(database, google);
        };
        _ = SyncAsync(database, google);
        _started = true;
    }

    // MCP 쓰기 신호(§2.4 "MCP 쓰기 직후 push"). 다른 PC가 2분까지 기다리지 않게 한다.
    private void OnExternalChange()
    {
        _quickSyncTimer.Stop();
        _quickSyncTimer.Start();
    }

    // 동기화(§2.4): 시작 시와 2분마다. 일정·할 일은 매번(push → pull), 공휴일은 하루 1회(§5.6).
    // 로그인 전이거나 실패하면 다음 차례에 다시 한다. 한쪽이 실패해도 나머지는 한다.
    private async Task SyncAsync(Database database, GoogleAccount google)
    {
        if (_isSyncing)
        {
            // 진행 중인 동기화는 이미 보낼 목록을 읽었을 수 있다. 끝나면 한 번 더 확인한다.
            _syncAgain = true;
            return;
        }
        _isSyncing = true;
        _syncAgain = false;
        _lastSyncStartedAt = DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
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
        catch (Exception exception) when (GoogleAccount.IsRevoked(exception))
        {
            // 토큰이 만료·취소됐다. 지우지 않으면 계속 "연결됨"으로 보이고 다시 연결할 수도 없다.
            LogSync($"token revoked, sign in again: {exception.GetType().Name}: {exception.Message}");
            await google.ForgetTokenAsync();
            _tray?.ShowDisconnected(google.Email);
        }
        catch (Exception exception)
        {
            LogSync($"sign-in failed: {exception.GetType().Name}: {exception.Message}");
        }
        finally
        {
            _isSyncing = false;
            if (_syncAgain) OnExternalChange();
        }

        static async Task RunAsync(string name, Func<Task> work)
        {
            try
            {
                await work();
            }
            catch (Exception exception) when (!GoogleAccount.IsRevoked(exception))
            {
                LogSync($"{name} failed: {exception.GetType().Name}: {exception.Message}");
            }
        }
    }

    private static void LogSync(string message) => AppendLog("sync.log", message);

    private static void LogError(string source, Exception? exception) =>
        AppendLog("app.log", $"unhandled {source}: {exception}");

    private static void AppendLog(string fileName, string message)
    {
        try
        {
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeskCal", "logs");
            Directory.CreateDirectory(directory);
            File.AppendAllText(Path.Combine(directory, fileName), $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
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
        window.ExternalChange += OnExternalChange;
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
