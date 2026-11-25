using Google.Apis.Calendar.v3;
using Google.Apis.Services;
using Google.Apis.Tasks.v1;

namespace DeskCal.Core;

public sealed class SyncCoordinator(Database database, GoogleAccount google, Action<string> log)
{
    public Task<bool> RunAsync(bool includeHolidays, CancellationToken token) => Task.Run(async () =>
    {
        try
        {
            if (await google.LoadAsync() is not { } credential) return false;
            var init = new BaseClientService.Initializer { HttpClientInitializer = credential, ApplicationName = "DeskCal" };
            using var calendar = new CalendarService(init);
            using var tasks = new TasksService(init);
            await RunService("events", () => new EventSync(database, new GoogleCalendarRemote(calendar)).SyncAsync(token));
            await RunService("todos", () => new TaskSync(database, new GoogleTasksRemote(tasks)).SyncAsync(token));
            if (includeHolidays)
            {
                var holidays = new HolidayRepository(database);
                if (HolidaySync.IsDue(holidays.LastSyncedAt(), DateTimeOffset.Now))
                    await RunService("holidays", () => HolidaySync.SyncAsync(calendar, holidays, DateTimeOffset.Now, token));
            }
            return true;
        }
        catch (Exception ex) when (GoogleAccount.IsRevoked(ex))
        {
            await google.ForgetTokenAsync();
            throw;
        }

        async Task RunService(string name, Func<Task> work)
        {
            token.ThrowIfCancellationRequested();
            try { await work(); }
            catch (Exception ex) when (!token.IsCancellationRequested && !GoogleAccount.IsRevoked(ex))
            {
                log($"{name} failed: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }, token);
}
