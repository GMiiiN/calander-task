namespace DeskCal.Core.Tests;

public sealed class AgendaTests
{
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
        Assert.Single(agenda.Groups[2].Events);
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
