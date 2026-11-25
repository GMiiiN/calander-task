using System.Globalization;
using Google.Apis.Calendar.v3.Data;

namespace DeskCal.Core.Tests;

// S5: 다른 곳(폰)에서 반복 일정의 시각·반복을 바꿨다. Google은 예외를 모두 버린다.
// 이 PC의 회차 기록(완료·체크리스트·회차 삭제)은 같은 날 새 회차가 하나일 때만 옮기고, 아니면 옛 회차로 보관해 확인하게 한다.
public sealed class SeriesShiftTests
{
    private static readonly DateOnly Thursday = new(2026, 11, 26);

    [Fact]
    public async Task RemoteSameDayTimeMoveCarriesDoneAndChecklistAndIsIdempotent()
    {
        using var db = new TestDatabase();
        var remote = new EventSyncTests.FakeCalendar();
        var series = db.Events.Create([new EventInput("매일 공부", "2026-11-24") { StartTime = "09:00", EndTime = "10:00", Repeat = new("daily", Count: 10) }]).Single();
        await Sync(db, remote);
        var occurrence = Single(db, Thursday);
        db.Events.SetOccurrenceDone(occurrence.OccurrenceId!, true);
        db.Events.AddChecklistItems(occurrence.OccurrenceId!, true, ["3장 읽기"]);
        await Sync(db, remote);

        remote.ShiftSeries(series.GoogleEventId!, e =>
        {
            e.Start = new EventDateTime { DateTimeRaw = "2026-11-24T15:00:00+09:00", TimeZone = "Asia/Seoul" };
            e.End = new EventDateTime { DateTimeRaw = "2026-11-24T16:00:00+09:00", TimeZone = "Asia/Seoul" };
        });
        await Sync(db, remote);
        await Sync(db, remote);

        var moved = Single(db, Thursday);
        Assert.Equal((15, true, "3장 읽기"), (moved.Start.Hour, moved.Event.IsDone, Assert.Single(moved.Event.Checklist).Text));
        Assert.Equal(15, Single(db, Thursday.AddDays(1)).Start.Hour);
        Assert.False(Single(db, Thursday.AddDays(1)).Event.IsDone);
        // 새 인스턴스에 다시 올렸고, 두 번째 pull은 아무것도 바꾸지 않는다.
        var instance = remote.Items[series.GoogleEventId + "_20261126T060000Z"];
        Assert.Equal("1", instance.ExtendedProperties.Private__["deskcalDone"]);
        Assert.Single(db.Events.GetRecords(), e => e.ParentEventId is not null && e.Status != "cancelled");
        Assert.All(db.Events.GetRecords(), e => Assert.False(e.IsDirty));
        Assert.Equal(0, db.Scalar("SELECT COUNT(*) FROM sync_conflict"));
        var patches = remote.PatchAttempts.Count;
        await Sync(db, remote);
        Assert.Equal(patches, remote.PatchAttempts.Count);
    }

    // 종일 ↔ 시간 전환, 회차 삭제, 폰에서 따로 옮긴 회차(새 시각을 따른다).
    [Fact]
    public async Task AllDayToTimedKeepsCancelledAndMovedOccurrencesOnTheirDay()
    {
        using var db = new TestDatabase();
        var remote = new EventSyncTests.FakeCalendar();
        var series = db.Events.Create([new EventInput("운동", "2026-11-24") { Repeat = new("daily", Count: 10) }]).Single();
        await Sync(db, remote);
        db.Events.DeleteOccurrence(Single(db, Thursday).OccurrenceId!);
        db.Events.SetOccurrenceDone(Single(db, Thursday.AddDays(1)).OccurrenceId!, true);
        await Sync(db, remote);
        // 폰에서 토요일 회차를 따로 옮겼다(다음 pull로 받는다).
        remote.Put(new Event
        {
            Id = series.GoogleEventId + "_20261128", RecurringEventId = series.GoogleEventId, OriginalStartTime = new EventDateTime { Date = "2026-11-28" },
            Summary = "운동", Status = "confirmed", Start = new EventDateTime { Date = "2026-11-28" }, End = new EventDateTime { Date = "2026-11-29" },
            ExtendedProperties = new Event.ExtendedPropertiesData { Private__ = new Dictionary<string, string> { ["deskcalUid"] = series.EventUid, ["deskcalDone"] = "1" } }
        });
        await Sync(db, remote);

        remote.ShiftSeries(series.GoogleEventId!, e =>
        {
            e.Start = new EventDateTime { DateTimeRaw = "2026-11-24T07:00:00+09:00", TimeZone = "Asia/Seoul" };
            e.End = new EventDateTime { DateTimeRaw = "2026-11-24T08:00:00+09:00", TimeZone = "Asia/Seoul" };
        });
        await Sync(db, remote);
        await Sync(db, remote);

        Assert.Empty(Singles(db, Thursday)); // 지운 회차는 계속 지워져 있다
        var friday = Single(db, Thursday.AddDays(1));
        Assert.Equal((7, true), (friday.Start.Hour, friday.Event.IsDone));
        var saturday = Single(db, Thursday.AddDays(2));
        Assert.Equal((7, true), (saturday.Start.Hour, saturday.Event.IsDone));
        Assert.Equal("cancelled", remote.Items[series.GoogleEventId + "_20261125T220000Z"].Status);
        Assert.Equal(0, db.Scalar("SELECT COUNT(*) FROM sync_conflict"));
    }

    // 날짜가 바뀌는 이동(밤 11시 → 다음 날 새벽 1시)은 같은 날 새 회차가 없다. 다른 날 회차에 붙이지 않고 보관해 확인하게 한다.
    [Fact]
    public async Task DateChangingMoveKeepsTheOldRecordForReviewOnce()
    {
        using var db = new TestDatabase();
        var remote = new EventSyncTests.FakeCalendar();
        var series = db.Events.Create([new EventInput("야간 작업", "2026-11-24") { StartTime = "23:00", EndTime = "23:30", Repeat = new("weekly", Weekdays: [DayOfWeek.Tuesday, DayOfWeek.Thursday]) }]).Single();
        await Sync(db, remote);
        db.Events.AddChecklistItems(Single(db, Thursday).OccurrenceId!, true, ["백업 확인"]);
        await Sync(db, remote);

        remote.ShiftSeries(series.GoogleEventId!, e =>
        {
            e.Start = new EventDateTime { DateTimeRaw = "2026-11-25T01:00:00+09:00", TimeZone = "Asia/Seoul" };
            e.End = new EventDateTime { DateTimeRaw = "2026-11-25T01:30:00+09:00", TimeZone = "Asia/Seoul" };
            e.Recurrence = ["RRULE:FREQ=WEEKLY;BYDAY=WE,FR"];
        });
        await Sync(db, remote);
        await Sync(db, remote);

        Assert.DoesNotContain(db.Events.List(Thursday.AddDays(-1), Thursday.AddDays(2)), o => o.Event.Checklist.Count > 0);
        var review = Assert.Single(ConflictLog.Unseen(db.Database));
        Assert.True(review.Orphan);
        Assert.Contains("백업 확인", db.Events.GetRecords().Single(e => e.ParentEventId is not null).Checklist.Single().Text);
    }

    // 다른 시간대(로스앤젤레스) 시리즈는 그 시간대의 같은 날로 맞춘다. 서머타임이 끝나는 주를 걸쳐도 하나씩 대응한다.
    [Fact]
    public async Task ForeignTimeZoneSeriesMapsBySeriesDayAcrossDst()
    {
        using var db = new TestDatabase();
        var remote = new EventSyncTests.FakeCalendar();
        remote.Put(new Event
        {
            Id = "laseries", Summary = "LA 회의", Status = "confirmed", Recurrence = ["RRULE:FREQ=DAILY;COUNT=6"],
            Start = new EventDateTime { DateTimeRaw = "2026-10-30T09:00:00-07:00", TimeZone = "America/Los_Angeles" },
            End = new EventDateTime { DateTimeRaw = "2026-10-30T10:00:00-07:00", TimeZone = "America/Los_Angeles" },
        });
        await Sync(db, remote);
        // LA 11/2 09:00(서머타임 끝난 뒤, -08:00) 회차 = 서울 11/3 02:00
        var target = db.Events.List(new DateOnly(2026, 11, 3), new DateOnly(2026, 11, 3)).Single();
        db.Events.SetOccurrenceDone(target.OccurrenceId!, true);
        await Sync(db, remote);

        remote.ShiftSeries("laseries", e =>
        {
            e.Start = new EventDateTime { DateTimeRaw = "2026-10-30T14:00:00-07:00", TimeZone = "America/Los_Angeles" };
            e.End = new EventDateTime { DateTimeRaw = "2026-10-30T15:00:00-07:00", TimeZone = "America/Los_Angeles" };
        });
        await Sync(db, remote);
        await Sync(db, remote);

        // LA 11/2 14:00(-08:00) = 서울 11/3 07:00
        var moved = db.Events.List(new DateOnly(2026, 11, 3), new DateOnly(2026, 11, 3)).Single(o => o.Event.IsDone);
        Assert.Equal(7, moved.Start.Hour);
        Assert.Equal(1, db.Events.List(new DateOnly(2026, 10, 30), new DateOnly(2026, 11, 6)).Count(o => o.Event.IsDone));
        Assert.Equal(0, db.Scalar("SELECT COUNT(*) FROM sync_conflict"));
    }

    // D3 완화는 안전할 때만: 요일이 바뀌어 회차 기록을 하나씩 대응할 수 없는 로컬 변경은 예전처럼 거부한다.
    [Fact]
    public void LocalChangeIsRefusedWhenAnOccurrenceCannotBeMatched()
    {
        using var db = new TestDatabase();
        var series = db.Events.Create([new EventInput("수업", "2026-11-24") { StartTime = "10:00", EndTime = "11:00", Repeat = new("weekly", Weekdays: [DayOfWeek.Tuesday, DayOfWeek.Thursday]) }]).Single();
        db.Events.SetOccurrenceDone(Single(db, Thursday).OccurrenceId!, true);

        var refused = Assert.Throws<InvalidOperationException>(() =>
            db.Events.Update(series.EventUid, new() { Repeat = new RecurrenceInput("weekly", Weekdays: [DayOfWeek.Tuesday, DayOfWeek.Friday]) }));
        Assert.Equal(EventRepository.OccurrenceKeyChangeError, refused.Message);
        Assert.True(Single(db, Thursday).Event.IsDone);

        // 반복 규칙을 바꿔도 그 회차의 날이 그대로 남으면(화·목 → 화·목·금) 바꾸게 한다.
        db.Events.Update(series.EventUid, new() { Repeat = new RecurrenceInput("weekly", Weekdays: [DayOfWeek.Tuesday, DayOfWeek.Thursday, DayOfWeek.Friday]) });
        Assert.True(Single(db, Thursday).Event.IsDone);
        Assert.False(Single(db, Thursday.AddDays(1)).Event.IsDone);
    }

    private static Task Sync(TestDatabase db, EventSyncTests.FakeCalendar remote) =>
        new EventSync(db.Database, remote, TestDatabase.Seoul, db.Clock).SyncAsync(CancellationToken.None);

    private static IReadOnlyList<EventOccurrence> Singles(TestDatabase db, DateOnly day) => db.Events.List(day, day);
    private static EventOccurrence Single(TestDatabase db, DateOnly day) => Assert.Single(Singles(db, day));
}
