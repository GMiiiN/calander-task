namespace DeskCal.Core.Tests;

public sealed class AgendaTests
{
    [Fact]
    public void MultiDayEventAppearsOnceOnItsFirstVisibleDay()
    {
        using var db = new TestDatabase();
        db.Events.Create([new EventInput("개인 파트 개발", "2026-11-05") { EndDate = "2026-11-18" }]);
        var service = new AgendaService(db.Events, db.Todos, TestDatabase.Seoul, () => new(2026, 11, 10));

        var agenda = service.GetAgenda();

        Assert.Equal("개인 파트 개발", Assert.Single(agenda.Groups.SelectMany(g => g.Events)).Event.Title);
        Assert.Single(agenda.Groups[1].Events);
    }

    // 리뷰 B-L1: get_agenda도 날짜별 저장 순서를 써서 list_events(시간순)와 달랐다.
    // 기본값(MCP)은 시작 시각순이고, 앱 우측만 공통 순서(완료는 아래, 생성순)를 고른다.
    [Fact]
    public void AgendaDefaultsToStartTimeOrderAndDayOrderIsOptIn()
    {
        using var db = new TestDatabase();
        db.Events.Create([new("늦은", "2026-10-30") { StartTime = "14:00" }]);
        db.Clock.Now = db.Clock.Now.AddMinutes(1);
        var early = db.Events.Create([new("이른", "2026-10-30") { StartTime = "09:00" }]).Single();
        db.Clock.Now = db.Clock.Now.AddMinutes(1);
        db.Events.Create([new("종일", "2026-10-30")]);
        db.Events.Update(early.EventUid, new() { IsDone = true });
        var day = new DateOnly(2026, 10, 30);
        var service = new AgendaService(db.Events, db.Todos, TestDatabase.Seoul, () => day);
        string[] Titles(bool dayOrder) => service.GetAgenda(day, day, dayOrder: dayOrder).Groups.Single(g => g.Date == day).Events.Select(o => o.Event.Title).ToArray();

        Assert.Equal(["종일", "이른", "늦은"], Titles(false));
        Assert.Equal(["늦은", "종일", "이른"], Titles(true));
    }

    [Fact]
    public void LaterEventsAreAddedOnlyWhenAsked()
    {
        using var db = new TestDatabase();
        db.Events.Create([
            new("2주 안", "2026-11-20"), new("3주 뒤", "2026-12-01"), new("석 달 뒤", "2027-02-01"),
            new("걸쳐 있음", "2026-11-22") { EndDate = "2026-11-26" }
        ]);
        var service = new AgendaService(db.Events, db.Todos, TestDatabase.Seoul, () => new(2026, 11, 11));

        // MCP get_agenda는 그대로: later에 일정이 없다.
        Assert.Empty(service.GetAgenda().Groups.Single(g => g.Kind == "later").Events);

        var agenda = service.GetAgenda(laterEventsUntil: new(2027, 1, 10));
        var later = agenda.Groups.Single(g => g.Kind == "later");
        Assert.Equal(["3주 뒤"], later.Events.Select(o => o.Event.Title));
        // 2주 안에서 시작해 그 뒤로 이어지는 일정은 날짜 목록에 한 번만 나온다.
        Assert.Single(agenda.Groups.SelectMany(g => g.Events), o => o.Event.Title == "걸쳐 있음");
    }

    [Fact]
    public void EachOccurrenceOfARepeatingEventStillAppears()
    {
        using var db = new TestDatabase();
        db.Events.Create([new EventInput("카공", "2026-11-08") { Repeat = new RecurrenceInput("weekly", Weekdays: [DayOfWeek.Sunday]) }]);
        var service = new AgendaService(db.Events, db.Todos, TestDatabase.Seoul, () => new(2026, 11, 5));

        Assert.Equal(2, service.GetAgenda().Groups.Sum(g => g.Events.Count));
    }

    [Fact]
    public void GroupsIncludeAllOpenTodosAndExactlyFourteenDaysOfEvents()
    {
        using var db = new TestDatabase();
        db.Events.Create([
            new("past", "2026-10-29"), new("today", "2026-10-30"), new("last", "2026-11-12"),
            new("outside", "2026-11-13"), new("multi", "2026-10-29") { EndDate = "2026-10-31" }
        ]);
        db.Todos.Create([new("overdue", "2026-10-29"), new("today", "2026-10-30"), new("last", "2026-11-12"), new("later", "2027-12-01"), new("anytime")]);
        var service = new AgendaService(db.Events, db.Todos, TestDatabase.Seoul, () => new(2026, 10, 30));
        var agenda = service.GetAgenda();
        Assert.Equal(new DateOnly(2026, 10, 30), agenda.Today);
        Assert.Equal(17, agenda.Groups.Count);
        Assert.Equal("overdue", agenda.Groups[0].Kind);
        Assert.Equal("overdue", Assert.Single(agenda.Groups[0].Todos).Title);
        Assert.Equal("later", agenda.Groups[^2].Kind);
        Assert.Equal("later", Assert.Single(agenda.Groups[^2].Todos).Title);
        Assert.Equal("anytime", agenda.Groups[^1].Kind);
        Assert.Equal("anytime", Assert.Single(agenda.Groups[^1].Todos).Title);
        Assert.Equal(14, agenda.Groups.Count(g => g.Kind == "date"));
        var today = agenda.Groups[1];
        Assert.Equal(2, today.Events.Count);
        Assert.Single(today.Todos);
        Assert.Empty(agenda.Groups[2].Events); // 여러 날 일정은 보이는 첫 날에만 나온다.
        Assert.Equal("last", Assert.Single(agenda.Groups[14].Events).Event.Title);
        Assert.DoesNotContain(agenda.Groups.SelectMany(g => g.Events), e => e.Event.Title is "outside" or "past");
        Assert.Equal(5, agenda.Groups.Sum(g => g.Todos.Count));
    }

    [Fact]
    public void CompletedTodosUseLocalCompletionDateAndDisappearTomorrow()
    {
        using var db = new TestDatabase();
        var items = db.Todos.Create([new("past due", "2026-10-01"), new("future due", "2027-01-01"), new("no due"), new("old done")]);
        db.Clock.Now = DateTimeOffset.Parse("2026-10-29T14:59:59Z");
        db.Todos.Update(items[3].TodoUid, new() { IsDone = true });
        db.Clock.Now = DateTimeOffset.Parse("2026-10-29T15:00:00Z");
        foreach (var item in items.Take(3)) db.Todos.Update(item.TodoUid, new() { IsDone = true });
        var current = new DateOnly(2026, 10, 30);
        var service = new AgendaService(db.Events, db.Todos, TestDatabase.Seoul, () => current);
        var result = service.GetAgenda();
        Assert.Equal(3, result.Groups[1].Todos.Count);
        Assert.All(result.Groups[1].Todos, t => Assert.True(t.IsDone));
        Assert.Equal(3, result.Groups.Sum(g => g.Todos.Count));
        current = current.AddDays(1);
        Assert.Empty(service.GetAgenda().Groups.SelectMany(g => g.Todos));
    }

    [Fact]
    public void CompletedEventsRemainOnOccurrenceDatesAndDdaysSelectNextOccurrence()
    {
        using var db = new TestDatabase();
        var values = db.Events.Create([
            new("past dday", "2026-10-29") { IsDday = true },
            new("today dday", "2026-10-30") { IsDday = true },
            new("later dday", "2027-01-01") { IsDday = true },
            new("anniversary", "2020-11-02") { IsDday = true, Repeat = new("yearly") },
            new("done today", "2026-10-30"), new("done yesterday", "2026-10-29"),
            new("weekly", "2026-10-30") { Repeat = new("weekly", Weekdays: [DayOfWeek.Friday], Count: 3) }
        ]);
        db.Events.Update(values[4].EventUid, new() { IsDone = true });
        db.Events.Update(values[5].EventUid, new() { IsDone = true });
        db.Events.SetOccurrenceDone(OccurrenceId.Create(values[6].EventUid, "2026-10-30"), true);
        var agenda = new AgendaService(db.Events, db.Todos, TestDatabase.Seoul, () => new(2026, 10, 30)).GetAgenda();
        Assert.Equal(new[] { 0, 3, 63 }, agenda.Ddays.Select(d => d.DaysRemaining));
        Assert.Equal(new[] { "today dday", "anniversary", "later dday" }, agenda.Ddays.Select(d => d.Occurrence.Event.Title));
        Assert.Equal(2, agenda.Groups[1].Events.Count(o => o.Event.IsDone));
        Assert.DoesNotContain(agenda.Groups.SelectMany(g => g.Events), o => o.Event.Title == "done yesterday");
        Assert.False(Assert.Single(agenda.Groups.Single(g => g.Date == new DateOnly(2026, 11, 6)).Events).Event.IsDone);
    }

    [Fact]
    public void CancelledNextDdayUsesFollowingOccurrenceAndFinitePastSeriesDisappears()
    {
        using var db = new TestDatabase();
        var values = db.Events.Create([
            new("daily", "2026-10-30") { IsDday = true, Repeat = new("daily", Count: 3) },
            new("finished", "2025-01-01") { IsDday = true, Repeat = new("daily", Count: 2) }
        ]);
        db.Events.DeleteOccurrence(OccurrenceId.Create(values[0].EventUid, "2026-10-30"));
        var agenda = new AgendaService(db.Events, db.Todos, TestDatabase.Seoul, () => new(2026, 10, 30)).GetAgenda();
        Assert.Equal(1, Assert.Single(agenda.Ddays).DaysRemaining);
    }
}
