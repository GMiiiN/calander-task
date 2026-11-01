using System.Globalization;
using Google.Apis.Calendar.v3;
using Google.Apis.Calendar.v3.Data;

namespace DeskCal.Core;

/// <summary>
/// 대한민국 공휴일을 Google 공개 휴일 캘린더에서 받아 온다(§5.6). official 캘린더는 쉬는 날(대체공휴일·선거일 포함)만 준다.
/// </summary>
public static class HolidaySync
{
    public const string CalendarId = "ko.south_korea.official#holiday@group.v.calendar.google.com";
    private static readonly TimeSpan Korea = TimeSpan.FromHours(9);

    // 하루 1회. 해가 바뀌면 받는 범위가 달라지므로 바로 다시 받는다.
    public static bool IsDue(DateTimeOffset? lastSyncedAt, DateTimeOffset now) =>
        lastSyncedAt is not { } last || now - last >= TimeSpan.FromDays(1) || last.ToOffset(Korea).Year != now.ToOffset(Korea).Year;

    // 올해 1/1부터 내후년 1/1 전까지(timeMax는 배타적). 모든 페이지를 받은 뒤에만 캐시를 바꾼다.
    public static async Task SyncAsync(CalendarService service, HolidayRepository repository, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var year = now.ToOffset(Korea).Year;
        var items = new List<Event>();
        string? page = null;
        do
        {
            var request = service.Events.List(CalendarId);
            request.TimeMinDateTimeOffset = new DateTimeOffset(year, 1, 1, 0, 0, 0, Korea);
            request.TimeMaxDateTimeOffset = new DateTimeOffset(year + 2, 1, 1, 0, 0, 0, Korea);
            request.SingleEvents = true;
            request.MaxResults = 250;
            request.PageToken = page;
            var result = await request.ExecuteAsync(cancellationToken);
            items.AddRange(result.Items ?? []);
            page = result.NextPageToken;
        } while (page is not null);
        repository.Replace(ToHolidays(items), now);
    }

    // 종일 일정만 쓴다. 끝 날짜는 배타적이라 여러 날이면 날짜마다 한 줄씩 만든다.
    public static IEnumerable<(DateOnly Date, string Name)> ToHolidays(IEnumerable<Event> items)
    {
        foreach (var item in items)
        {
            if (item.Status == "cancelled" || item.Start?.Date is not { } startText) continue;
            var start = DateOnly.ParseExact(startText, "yyyy-MM-dd", CultureInfo.InvariantCulture);
            var end = item.End?.Date is { } endText ? DateOnly.ParseExact(endText, "yyyy-MM-dd", CultureInfo.InvariantCulture) : start.AddDays(1);
            for (var date = start; date < end || date == start; date = date.AddDays(1))
                yield return (date, item.Summary ?? "");
        }
    }
}
