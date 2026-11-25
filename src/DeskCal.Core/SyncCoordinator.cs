using Google.Apis.Calendar.v3;
using Google.Apis.Services;

namespace DeskCal.Core;

// forgetRevokedToken: invalid_grant(만료·취소)일 때 공유 토큰 행을 지울지. 앱만 켠다(지운 뒤 트레이로 다시 연결을 알린다).
// MCP는 어떤 갱신 실패에도 로그인을 지우지 않는다. 죽은 토큰이면 다음에 앱이 같은 오류를 받아 지운다.
public sealed class SyncCoordinator(Database database, GoogleAccount google, Action<string> log, bool forgetRevokedToken = false)
{
    public Task<bool> RunAsync(bool includeHolidays, CancellationToken token) =>
        WithSenderAsync(database, () => RunServicesAsync(includeHolidays, token), token);

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

    private async Task<bool> RunServicesAsync(bool includeHolidays, CancellationToken token)
    {
        try
        {
            if (await google.LoadAsync() is not { } credential) return false;
            var init = new BaseClientService.Initializer { HttpClientInitializer = credential, ApplicationName = "DeskCal" };
            var tasksInit = new BaseClientService.Initializer { HttpClientInitializer = credential, ApplicationName = "DeskCal" };
            if (google.HttpClientFactory is { } factory) init.HttpClientFactory = tasksInit.HttpClientFactory = factory;
            using var calendar = new CalendarService(init);
            using var tasks = GoogleTasksRemote.CreateService(tasksInit);
            await SyncDataAsync(new GoogleCalendarRemote(calendar), new GoogleTasksRemote(tasks), token);
            if (includeHolidays)
            {
                var holidays = new HolidayRepository(database);
                if (HolidaySync.IsDue(holidays.LastSyncedAt(), DateTimeOffset.Now))
                    await RunService("holidays", () => HolidaySync.SyncAsync(calendar, holidays, DateTimeOffset.Now, token), token);
            }
            return true;
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

    private async Task RunService(string name, Func<Task> work, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try { await work(); }
        catch (Exception ex) when (!token.IsCancellationRequested && !GoogleAccount.IsRevoked(ex))
        {
            log($"{name} failed: {ex.GetType().Name}: {ex.Message}");
        }
    }
}
