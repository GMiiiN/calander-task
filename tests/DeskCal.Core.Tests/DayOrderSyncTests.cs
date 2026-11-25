using System.Globalization;
using System.Text.Json.Nodes;
using Google.Apis.Calendar.v3.Data;
using static DeskCal.Core.Tests.SharedSettingsTests;

namespace DeskCal.Core.Tests;

// S8: 하루 안 일정 순서를 Claude가 바꾸고(reorder_events), 두 PC(두 DB)가 Drive 앱 전용 공간으로 같은 순서를 쓴다.
public sealed class DayOrderSyncTests
{
    private static readonly DateOnly Day = new(2026, 11, 26);

    [Fact]
    public void ReorderValidatesTheWholeDayAndSavesAtomically()
    {
        using var db = new TestDatabase();
        Create(db, new EventInput("기간", "2026-11-25") { EndDate = "2026-11-27" });
        Create(db, new EventInput("A", "2026-11-26"), new EventInput("B", "2026-11-26") { StartTime = "09:00" }, new EventInput("C", "2026-11-26"),
            new EventInput("완료 D", "2026-11-26"), new EventInput("다른 날", "2026-11-27"));
        db.Events.Update(Uid(db, "완료 D"), new() { IsDone = true });
        var revisions = db.Events.GetRecords().ToDictionary(e => e.EventUid, e => e.LocalRevision);
        var (a, b, c, d) = (Key(db, "A"), Key(db, "B"), Key(db, "C"), Key(db, "완료 D"));
        Assert.Equal(new[] { "A", "B", "C", "완료 D" }, Titles(db, Day));

        Assert.Contains("repeat", Reject(db, a, a, b, c, d));
        var stale = Reject(db, b, c, d);
        Assert.Contains("incomplete or stale", stale);
        Assert.Contains($"{a} (A)", stale);
        Assert.Contains("multi-day", Reject(db, Uid(db, "기간"), a, b, c, d));
        Assert.Contains("not a single-day event", Reject(db, Uid(db, "다른 날"), a, b, c));
        Assert.Contains("Completed events always stay below", Reject(db, d, a, b, c));
        Assert.Equal(0, db.Scalar("SELECT COUNT(*) FROM shared_entry"));

        var ordered = DayEventOrder.Reorder(db.Events, Day, [c, a, b, d]);
        Assert.Equal(new[] { "C", "A", "B", "완료 D" }, ordered.Select(o => o.Event.Title));
        Assert.Equal(new[] { "C", "A", "B", "완료 D" }, Titles(db, Day));
        // 순서는 그날 공유 항목 하나다. 일정 행은 바뀌지 않는다(Google로 다시 보낼 것이 없다).
        var value = JsonNode.Parse(Text(db, "SELECT value_json FROM shared_entry WHERE entry_key='day_order:2026-11-26'"))!.AsArray();
        Assert.Equal(new[] { c, a, b, d }, value.Select(n => (string)n!));
        Assert.Equal(1, db.Scalar("SELECT is_dirty FROM shared_entry WHERE entry_key='day_order:2026-11-26'"));
        Assert.All(db.Events.GetRecords(), e => Assert.Equal(revisions[e.EventUid], e.LocalRevision));
        Assert.Equal(new[] { "다른 날" }, Titles(db, Day.AddDays(1)));
    }

    // Google 일정의 PC별 로컬 UID와 가져온 시각이 달라도 기본 순서(Google created)와 바꾼 순서가 두 PC에서 같다.
    [Fact]
    public async Task GoogleCreatedEventsKeepTheSameOrderOnBothPcs()
    {
        using var a = new TestDatabase();
        using var b = new TestDatabase();
        var calendar = new EventSyncTests.FakeCalendar();
        var drive = new FakeAppData();
        Phone(calendar, "phone-3", "폰 셋째", "2026-11-26", created: 3);
        Phone(calendar, "phone-1", "폰 첫째", "2026-11-26", created: 1);
        Phone(calendar, "phone-2", "폰 둘째", "2026-11-26", created: 2);
        await Pull(a, calendar);
        b.Clock.Now = b.Clock.Now.AddHours(5); // 다른 PC는 나중에 받는다
        await Pull(b, calendar);

        Assert.NotEqual(Uid(a, "폰 첫째"), Uid(b, "폰 첫째"));
        Assert.Equal(new[] { "폰 첫째", "폰 둘째", "폰 셋째" }, Titles(a, Day));
        Assert.Equal(Titles(a, Day), Titles(b, Day));

        DayEventOrder.Reorder(a.Events, Day, [Key(a, "폰 셋째"), Key(a, "폰 첫째"), Key(a, "폰 둘째")]);
        await Share(a, drive);
        await Share(b, drive);
        Assert.Equal(new[] { "폰 셋째", "폰 첫째", "폰 둘째" }, Titles(b, Day));

        // 순서만 오갔다. Google 일정은 다시 만들거나 고치지 않는다.
        await Pull(a, calendar);
        await Pull(b, calendar);
        Assert.Empty(calendar.InsertAttempts);
        Assert.Empty(calendar.PatchAttempts);
        Assert.Equal(new[] { "폰 셋째", "폰 첫째", "폰 둘째" }, Titles(a, Day));
    }

    // 같은 날짜 경합: 두 PC가 오프라인에서 같은 날 순서를 바꿨다. 서버에 먼저 확정된 순서가 이기고 나머지는 충돌 기록에 남는다.
    [Fact]
    public async Task SameDateRaceKeepsTheOrderConfirmedFirst()
    {
        using var a = new TestDatabase();
        using var b = new TestDatabase();
        var calendar = new EventSyncTests.FakeCalendar();
        var drive = new FakeAppData();
        Create(a, new EventInput("하나", "2026-11-26"), new EventInput("둘", "2026-11-26"), new EventInput("셋", "2026-11-26"));
        await Pull(a, calendar);
        await Pull(b, calendar);
        Assert.Equal(Titles(a, Day), Titles(b, Day));

        DayEventOrder.Reorder(a.Events, Day, [Key(a, "셋"), Key(a, "둘"), Key(a, "하나")]);
        DayEventOrder.Reorder(b.Events, Day, [Key(b, "둘"), Key(b, "하나"), Key(b, "셋")]);
        await Share(a, drive);
        await Share(b, drive);
        await Share(a, drive);

        Assert.Equal(new[] { "셋", "둘", "하나" }, Titles(a, Day));
        Assert.Equal(new[] { "셋", "둘", "하나" }, Titles(b, Day));
        Assert.Equal(1, b.Scalar("SELECT COUNT(*) FROM sync_conflict WHERE item_kind='settings' AND item_uid='day_order:2026-11-26'"));
        Assert.Equal(0, a.Scalar("SELECT COUNT(*) FROM sync_conflict"));
    }

    [Fact]
    public async Task DifferentDatesChangedOnBothPcsBothSurvive()
    {
        using var a = new TestDatabase();
        using var b = new TestDatabase();
        var calendar = new EventSyncTests.FakeCalendar();
        var drive = new FakeAppData();
        Create(a, new EventInput("26일 앞", "2026-11-26"), new EventInput("26일 뒤", "2026-11-26"),
            new EventInput("27일 앞", "2026-11-27"), new EventInput("27일 뒤", "2026-11-27"));
        await Pull(a, calendar);
        await Pull(b, calendar);

        DayEventOrder.Reorder(a.Events, Day, [Key(a, "26일 뒤"), Key(a, "26일 앞")]);
        DayEventOrder.Reorder(b.Events, Day.AddDays(1), [Key(b, "27일 뒤", Day.AddDays(1)), Key(b, "27일 앞", Day.AddDays(1))]);
        await Share(b, drive);
        await Share(a, drive);
        await Share(b, drive);

        foreach (var db in new[] { a, b })
        {
            Assert.Equal(new[] { "26일 뒤", "26일 앞" }, Titles(db, Day));
            Assert.Equal(new[] { "27일 뒤", "27일 앞" }, Titles(db, Day.AddDays(1)));
        }
        Assert.Equal(0, a.Scalar("SELECT COUNT(*) FROM sync_conflict") + b.Scalar("SELECT COUNT(*) FROM sync_conflict"));
    }

    // 오프라인 복귀·재전송: 올리지 못한 순서는 남았다가 다음에 올라간다. 일정은 Google로 다시 보내지 않는다.
    [Fact]
    public async Task OfflineReorderRetriesAndSendsOnlyOrderMetadata()
    {
        using var a = new TestDatabase();
        using var b = new TestDatabase();
        var calendar = new EventSyncTests.FakeCalendar();
        var drive = new FakeAppData { FailNextWrite = true };
        Create(a, new EventInput("가", "2026-11-26"), new EventInput("나", "2026-11-26"));
        await Pull(a, calendar);
        var inserts = calendar.InsertAttempts.Count;

        DayEventOrder.Move(a.Events, Day, Key(a, "나"), Key(a, "가")); // 앱에서 끌어 놓기도 같은 공유 항목을 쓴다
        await Assert.ThrowsAsync<HttpRequestException>(() => Share(a, drive));
        Assert.True(PendingChanges.HasUnsent(a.Database));
        Assert.Contains(PendingChanges.List(a.Database), p => p.Title == "11월 26일 일정 순서");
        await Pull(a, calendar);
        Assert.Empty(calendar.PatchAttempts);

        await Share(a, drive);
        Assert.False(PendingChanges.HasUnsent(a.Database));
        await Pull(b, calendar);
        await Share(b, drive);
        Assert.Equal(new[] { "나", "가" }, Titles(b, Day));
        Assert.Equal(inserts, calendar.InsertAttempts.Count);
        Assert.Empty(calendar.PatchAttempts);
    }

    // 삭제·추가: 지운 일정은 빠지고 새 일정은 남은 자리 뒤에 생성순으로 들어간다. 두 PC가 같다.
    [Fact]
    public async Task DeletedAndAddedEventsKeepTheRestOfTheOrder()
    {
        using var a = new TestDatabase();
        using var b = new TestDatabase();
        var calendar = new EventSyncTests.FakeCalendar();
        var drive = new FakeAppData();
        Create(a, new EventInput("A", "2026-11-26"), new EventInput("B", "2026-11-26"), new EventInput("C", "2026-11-26"));
        await Pull(a, calendar);
        await Pull(b, calendar);
        DayEventOrder.Reorder(a.Events, Day, [Key(a, "C"), Key(a, "A"), Key(a, "B")]);
        await Share(a, drive);
        await Share(b, drive);

        b.Events.Delete(Uid(b, "A"));
        a.Clock.Now = a.Clock.Now.AddMinutes(1);
        Create(a, new EventInput("E", "2026-11-26"));
        await Pull(b, calendar);
        await Pull(a, calendar);
        await Pull(b, calendar);

        Assert.Equal(new[] { "C", "B", "E" }, Titles(a, Day));
        Assert.Equal(new[] { "C", "B", "E" }, Titles(b, Day));
    }

    // 반복 일정 자리는 회차 ID로 받는다. 폰에서 만든 반복 일정은 PC마다 로컬 UID가 달라도 시리즈 Google ID와 원래 회차로 맞춘다. 다른 날 회차는 그대로다.
    [Fact]
    public async Task RecurringOccurrenceOrderTranslatesBetweenPcs()
    {
        using var a = new TestDatabase();
        using var b = new TestDatabase();
        var calendar = new EventSyncTests.FakeCalendar();
        var drive = new FakeAppData();
        calendar.Put(new Event
        {
            Id = "phoneseries", Summary = "매일 운동", Start = new EventDateTime { Date = "2026-11-25" }, End = new EventDateTime { Date = "2026-11-26" },
            Recurrence = ["RRULE:FREQ=DAILY;COUNT=5"], CreatedDateTimeOffset = Created(1)
        });
        Phone(calendar, "phone-x", "X", "2026-11-26", created: 2);
        Phone(calendar, "phone-y", "Y", "2026-11-26", created: 3);
        await Pull(a, calendar);
        await Pull(b, calendar);
        var occurrence = Key(a, "매일 운동");
        Assert.Contains('~', occurrence);
        Assert.NotEqual(occurrence, Key(b, "매일 운동"));

        DayEventOrder.Reorder(a.Events, Day, [Key(a, "Y"), occurrence, Key(a, "X")]);
        await Share(a, drive);
        await Share(b, drive);

        Assert.Equal(new[] { "Y", "매일 운동", "X" }, Titles(b, Day));
        Assert.Contains("\"phoneseries~2026-11-26\"", Text(b, "SELECT value_json FROM shared_entry WHERE entry_key='day_order:2026-11-26'"));
        Assert.Equal(new[] { "매일 운동" }, Titles(b, Day.AddDays(1)));
    }

    // S8 이전에 이 PC에만 저장한 순서(day_event_order)는 첫 공유 동기화에서 공유 항목으로 옮겨 다른 PC도 쓴다.
    [Fact]
    public async Task LegacyLocalOrderIsSeededAndShared()
    {
        using var a = new TestDatabase();
        using var b = new TestDatabase();
        var calendar = new EventSyncTests.FakeCalendar();
        var drive = new FakeAppData();
        Create(a, new EventInput("첫째", "2026-11-26"), new EventInput("둘째", "2026-11-26"), new EventInput("셋째", "2026-11-26"));
        await Pull(a, calendar);
        await Pull(b, calendar);
        a.Execute("INSERT INTO day_event_order(day, event_key, position, revision) VALUES ('2026-11-26', $c, 0, 1), ('2026-11-26', $a, 1, 1)",
            ("$c", Uid(a, "셋째")), ("$a", Uid(a, "첫째")));
        Assert.Equal(new[] { "셋째", "첫째", "둘째" }, Titles(a, Day));

        await Share(a, drive);
        Assert.Equal(0, a.Scalar("SELECT COUNT(*) FROM day_event_order"));
        Assert.Equal(new[] { "셋째", "첫째", "둘째" }, Titles(a, Day));
        await Share(b, drive);
        Assert.Equal(new[] { "셋째", "첫째", "둘째" }, Titles(b, Day));
    }

    // 예전에 가져온 일정은 이 PC가 처음 받은 시각이 생성 시각이었다. 한 번 전체를 받아 Google created로 맞추고, 그 뒤로는 증분만 받는다.
    [Fact]
    public async Task OldRowsAlignTheirCreatedTimeWithOneFullListing()
    {
        using var db = new TestDatabase();
        var calendar = new EventSyncTests.FakeCalendar();
        Phone(calendar, "phone-late", "늦게 만든", "2026-11-26", created: 9);
        Phone(calendar, "phone-early", "먼저 만든", "2026-11-26", created: 1);
        await Pull(db, calendar);
        db.Execute("UPDATE event SET created_at = CASE title WHEN '먼저 만든' THEN '2026-11-25T00:00:02.0000000+00:00' ELSE '2026-11-25T00:00:01.0000000+00:00' END;" +
            "DELETE FROM setting WHERE setting_key='event_created_basis'");
        Assert.Equal(new[] { "늦게 만든", "먼저 만든" }, Titles(db, Day));

        calendar.ListTokens.Clear();
        await Pull(db, calendar);
        await Pull(db, calendar);
        Assert.Null(calendar.ListTokens[0]); // 한 번만 전체
        Assert.NotNull(calendar.ListTokens[1]);
        Assert.Equal(new[] { "먼저 만든", "늦게 만든" }, Titles(db, Day));
        Assert.All(db.Events.GetRecords(), e => Assert.False(e.IsDirty));
    }

    private static void Create(TestDatabase db, params EventInput[] inputs)
    {
        // 생성순이 기본 순서다. 같은 시각이 되지 않게 하나씩 1초 간격으로 만든다.
        foreach (var input in inputs)
        {
            db.Events.Create([input]);
            db.Clock.Now = db.Clock.Now.AddSeconds(1);
        }
    }

    private static DateTimeOffset Created(int second) => DateTimeOffset.Parse("2026-11-01T00:00:00Z", CultureInfo.InvariantCulture).AddSeconds(second);

    private static void Phone(EventSyncTests.FakeCalendar calendar, string id, string title, string date, int created) =>
        calendar.Put(new Event
        {
            Id = id, Summary = title, Start = new EventDateTime { Date = date },
            End = new EventDateTime { Date = DateOnly.Parse(date, CultureInfo.InvariantCulture).AddDays(1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) },
            CreatedDateTimeOffset = Created(created)
        });

    private static Task Pull(TestDatabase db, EventSyncTests.FakeCalendar calendar) =>
        new EventSync(db.Database, calendar, TestDatabase.Seoul, db.Clock).SyncAsync(CancellationToken.None);

    private static Task Share(TestDatabase db, FakeAppData drive) =>
        new SharedSettingsSync(db.Database, drive, db.Clock, localTimeZone: TestDatabase.Seoul).SyncAsync(CancellationToken.None);

    private static IEnumerable<EventOccurrence> Singles(TestDatabase db, DateOnly day) =>
        DayEventOrder.Read(db.Database, day, day).Sort(day, db.Events.List(day, day).Where(o => !DayEventOrder.IsPeriod(o)));

    private static string[] Titles(TestDatabase db, DateOnly day) => Singles(db, day).Select(o => o.Event.Title).ToArray();
    private static string Key(TestDatabase db, string title, DateOnly? day = null) => DayEventOrder.Key(Singles(db, day ?? Day).Single(o => o.Event.Title == title));
    private static string Uid(TestDatabase db, string title) => db.Events.GetRecords().Single(e => e.Title == title && e.ParentEventId is null).EventUid;

    private static string Reject(TestDatabase db, params string[] ids) =>
        Assert.Throws<ArgumentException>(() => DayEventOrder.Reorder(db.Events, Day, ids)).Message;

    private static string Text(TestDatabase db, string sql)
    {
        using var connection = db.Database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (string)command.ExecuteScalar()!;
    }
}
