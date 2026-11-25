using Google.Apis.Calendar.v3;
using Google.Apis.Drive.v3;
using Google.Apis.Services;

namespace DeskCal.Core;

// 한 번의 전송 시도 결과(S4). Busy: 같은 DB의 다른 프로세스(앱·다른 MCP)가 보내는 중이라 그쪽이 보낸다.
// NotSignedIn: 저장된 로그인이 없다. NoClientFile: 로그인은 있는데 DB 옆 OAuth 클라이언트 파일이 없다.
public enum SyncOutcome { Completed, Busy, NotSignedIn, NoClientFile }

// forgetRevokedToken: invalid_grant(만료·취소)일 때 공유 토큰 행을 지울지. 앱만 켠다(지운 뒤 트레이로 다시 연결을 알린다).
// MCP는 어떤 갱신 실패에도 로그인을 지우지 않는다. 죽은 토큰이면 다음에 앱이 같은 오류를 받아 지운다.
public sealed class SyncCoordinator(Database database, GoogleAccount google, Action<string> log, bool forgetRevokedToken = false)
{
    private string? _authError;

    public async Task<bool> RunAsync(bool includeHolidays, CancellationToken token) =>
        await RunWithOutcomeAsync(includeHolidays, token) == SyncOutcome.Completed;

    public async Task<SyncOutcome> RunWithOutcomeAsync(bool includeHolidays, CancellationToken token)
    {
        var outcome = SyncOutcome.Busy;
        await WithSenderAsync(database, async () =>
        {
            outcome = await RunServicesAsync(includeHolidays, token);
            return true;
        }, token);
        return outcome;
    }

    internal static Task<bool> WithSenderAsync(Database db, Func<Task<bool>> work, CancellationToken token) => Task.Run(async () =>
    {
        FileStream lease;
        try
        {
            lease = new FileStream(db.FilePath + ".sync-lock", FileMode.OpenOrCreate, FileAccess.ReadWrite,
                FileShare.None, 1, FileOptions.DeleteOnClose);
        }
        catch (IOException error) when ((error.HResult & 0xffff) == 32) { return false; }
        using (lease) return await work();
    }, token);

    private async Task<SyncOutcome> RunServicesAsync(bool includeHolidays, CancellationToken token)
    {
        try
        {
            if (await google.LoadAsync() is not { } credential) return google.IsSignedIn ? SyncOutcome.NoClientFile : SyncOutcome.NotSignedIn;
            _authError = null;
            BaseClientService.Initializer Initializer()
            {
                var init = new BaseClientService.Initializer { HttpClientInitializer = credential, ApplicationName = "DeskCal" };
                if (google.HttpClientFactory is { } factory) init.HttpClientFactory = factory;
                return init;
            }
            using var calendar = new CalendarService(Initializer());
            using var tasks = GoogleTasksRemote.CreateService(Initializer());
            using var drive = GoogleAppDataStore.CreateService(Initializer());
            await SyncDataAsync(new GoogleCalendarRemote(calendar), new GoogleTasksRemote(tasks), token);
            await SyncSettingsAsync(new GoogleAppDataStore(drive), token);
            if (includeHolidays)
            {
                var holidays = new HolidayRepository(database);
                if (HolidaySync.IsDue(holidays.LastSyncedAt(), DateTimeOffset.Now))
                    await RunService("holidays", () => HolidaySync.SyncAsync(calendar, holidays, DateTimeOffset.Now, token), token);
            }
            // invalid_grant 말고도 OAuth 클라이언트가 지워지거나 바뀌면(invalid_client·unauthorized_client 등 4xx) 계속 실패한다.
            // 토큰은 지우지 않고 그 이유를 남겨 트레이·상태줄이 "연결됨" 대신 다시 연결을 알리게 한다. 성공하면 지운다.
            google.RecordAuthError(_authError);
            return SyncOutcome.Completed;
        }
        catch (Exception ex) when (forgetRevokedToken && GoogleAccount.IsRevoked(ex))
        {
            await google.ForgetTokenAsync();
            throw;
        }

    }

    internal async Task SyncDataAsync(ICalendarRemote events, ITasksRemote tasks, CancellationToken token)
    {
        await RunService("events", () => new EventSync(database, events).SyncAsync(token), token);
        await RunService("todos", () => new TaskSync(database, tasks, log: log).SyncAsync(token), token);
    }

    // 공유 설정(카테고리 별칭 등, S6). 저장된 토큰이 Drive 앱 전용 공간 권한을 받지 않았으면(동의 화면에서 뺀 경우) 부르지 않고 이유만 남긴다.
    internal Task SyncSettingsAsync(IAppDataStore store, CancellationToken token) => RunService("settings", async () =>
    {
        if (await google.HasScopeAsync(DriveService.Scope.DriveAppdata) == false)
        {
            SharedSettingsSync.RecordError(database, SharedSettingsSync.ScopeMissing);
            return;
        }
        await new SharedSettingsSync(database, store, log: log).SyncAsync(token); // 카테고리 별칭(S6), 날짜별 일정 순서(S8)
    }, token);

    private async Task RunService(string name, Func<Task> work, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try { await work(); }
        catch (Exception ex) when (!token.IsCancellationRequested && !GoogleAccount.IsRevoked(ex))
        {
            _authError ??= GoogleAccount.AuthErrorOf(ex);
            log($"{name} failed: {ex.GetType().Name}: {ex.Message}");
        }
    }
}
