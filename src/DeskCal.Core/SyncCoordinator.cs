using Google.Apis.Calendar.v3;
using Google.Apis.Services;

namespace DeskCal.Core;

public sealed class SyncCoordinator(Database database, GoogleAccount google, Action<string> log)
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
            using var calendar = new CalendarService(init);
            using var tasks = GoogleTasksRemote.CreateService(new BaseClientService.Initializer
                { HttpClientInitializer = credential, ApplicationName = "DeskCal" });
            await SyncDataAsync(new GoogleCalendarRemote(calendar), new GoogleTasksRemote(tasks), token);
            if (includeHolidays)
            {
                var holidays = new HolidayRepository(database);
                if (HolidaySync.IsDue(holidays.LastSyncedAt(), DateTimeOffset.Now))
                    await RunService("holidays", () => HolidaySync.SyncAsync(calendar, holidays, DateTimeOffset.Now, token), token);
            }
            return true;
        }
        catch (Exception ex) when (GoogleAccount.IsRevoked(ex))
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
