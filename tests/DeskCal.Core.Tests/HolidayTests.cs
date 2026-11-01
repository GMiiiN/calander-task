using Google.Apis.Calendar.v3.Data;

namespace DeskCal.Core.Tests;

public sealed class HolidayTests
{
    private static Event AllDay(string start, string end, string summary, string status = "confirmed") =>
        new() { Start = new EventDateTime { Date = start }, End = new EventDateTime { Date = end }, Summary = summary, Status = status };

    [Fact]
    public void ConvertsAllDayEventsWithExclusiveEndAndSkipsOthers()
    {
        var holidays = HolidaySync.ToHolidays([
            AllDay("2026-03-01", "2026-03-02", "삼일절"),
            AllDay("2026-09-24", "2026-09-27", "추석 연휴"),
            AllDay("2026-10-03", "2026-10-04", "개천절", status: "cancelled"),
            new Event { Start = new EventDateTime { DateTimeRaw = "2026-05-05T10:00:00+09:00" }, Summary = "시간 일정" }
        ]).ToArray();

        Assert.Equal([
            (new DateOnly(2026, 3, 1), "삼일절"),
            (new DateOnly(2026, 9, 24), "추석 연휴"), (new DateOnly(2026, 9, 25), "추석 연휴"), (new DateOnly(2026, 9, 26), "추석 연휴")
        ], holidays);
    }

    [Fact]
    public void ReplaceSwapsWholeListAndJoinsSameDayNames()
    {
        using var db = new TestDatabase();
        var repository = new HolidayRepository(db.Database);
        var first = DateTimeOffset.Parse("2026-11-01T07:00:00Z");
        repository.Replace([(new DateOnly(2026, 1, 1), "새해첫날"), (new DateOnly(2026, 5, 5), "어린이날")], first);
        repository.Replace([(new DateOnly(2026, 5, 5), "어린이날"), (new DateOnly(2026, 5, 5), "다른 휴일"), (new DateOnly(2026, 5, 5), "어린이날")], first.AddDays(1));

        var list = repository.List(new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31));
        Assert.Equal(new Dictionary<DateOnly, string> { [new DateOnly(2026, 5, 5)] = "다른 휴일 · 어린이날" }, list);
        Assert.Equal(first.AddDays(1), repository.LastSyncedAt());
    }

    [Fact]
    public void IsDueOncePerDayAndWhenYearChanges()
    {
        var now = DateTimeOffset.Parse("2026-11-01T12:00:00+09:00");
        Assert.True(HolidaySync.IsDue(null, now));
        Assert.False(HolidaySync.IsDue(now.AddHours(-23), now));
        Assert.True(HolidaySync.IsDue(now.AddDays(-1), now));
        Assert.True(HolidaySync.IsDue(DateTimeOffset.Parse("2026-12-31T23:30:00+09:00"), DateTimeOffset.Parse("2027-01-01T00:10:00+09:00")));
    }
}
