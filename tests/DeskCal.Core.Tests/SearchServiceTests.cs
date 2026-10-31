namespace DeskCal.Core.Tests;

public sealed class SearchServiceTests
{
    private static SearchService Service(TestDatabase db) => new(db.Events, db.Todos, TestDatabase.Seoul, () => new(2026, 10, 30));

    [Fact]
    public void MatchesTitlesAndMemosIgnoringCaseAndTrimsQuery()
    {
        using var db = new TestDatabase();
        var values = db.Events.Create([
            new("Team Meeting", "2026-10-30") { ColorId = "3", StartTime = "09:00" },
            new("일정", "2026-10-31") { Memo = "TEAM 회의 준비" },
            new("unrelated", "2026-10-30")
        ]);
        var tasks = db.Todos.Create([new("team task"), new("할 일", Memo: "Team notes")]);
        db.Todos.Update(tasks[0].TodoUid, new() { IsDone = true });
        var hits = Service(db).Search("  tEaM  ");
        Assert.Equal(4, hits.Count);
        Assert.Equal(new SearchHit("event", values[0].EventUid, "Team Meeting", new(2026, 10, 30), "09:00", "3", false), hits[0]);
        var done = Assert.Single(hits, hit => hit.Uid == tasks[0].TodoUid);
        Assert.Equal("todo", done.Kind);
        Assert.True(done.IsDone);
        Assert.Null(done.Date);
        Assert.Null(done.StartTime);
        Assert.Null(done.ColorId);
        Assert.Equal(values[1].EventUid, Assert.Single(Service(db).Search("회의")).Uid);
    }

    [Fact]
    public void ExcludesDeletedCancelledAndSeparateExceptionRows()
    {
        using var db = new TestDatabase();
        var values = db.Events.Create([
            new("match series", "2026-10-30") { Repeat = new("daily") },
            new("match deleted", "2026-10-30"), new("match cancelled", "2026-10-30"),
            new("match orphan", "2026-10-30")
        ]);
        db.Events.SetOccurrenceDone(OccurrenceId.Create(values[0].EventUid, "2026-10-30"), true);
        db.Events.Delete(values[1].EventUid);
        db.Execute("UPDATE event SET status='cancelled' WHERE event_uid=$uid", ("$uid", values[2].EventUid));
        db.Execute("UPDATE event SET google_recurring_id='remote-series' WHERE event_uid=$uid", ("$uid", values[3].EventUid));
        var task = db.Todos.Create([new("match deleted todo")])[0];
        db.Todos.Delete(task.TodoUid);
        var hit = Assert.Single(Service(db).Search("match"));
        Assert.Equal(values[0].EventUid, hit.Uid);
    }

    [Fact]
    public void RecurrencesUseNextVisibleDateIncludingTodayAndFallBackToSeriesStart()
    {
        using var db = new TestDatabase();
        var values = db.Events.Create([
            new("match today", "2020-10-30") { Repeat = new("yearly") },
            new("match cancelled", "2026-10-30") { Repeat = new("daily", Count: 3) },
            new("match finished", "2025-01-01") { Repeat = new("daily", Count: 2) }
        ]);
        db.Events.DeleteOccurrence(OccurrenceId.Create(values[1].EventUid, "2026-10-30"));
        var hits = Service(db).Search("match");
        Assert.Equal(new DateOnly(2026, 10, 30), hits[0].Date);
        Assert.Equal(values[0].EventUid, hits[0].Uid);
        Assert.Equal(new DateOnly(2026, 10, 31), hits[1].Date);
        Assert.Equal(values[1].EventUid, hits[1].Uid);
        Assert.Equal(new DateOnly(2025, 1, 1), hits[2].Date);
    }

    [Fact]
    public void RecurrenceHitTakesTimeAndDoneStateFromThatOccurrence()
    {
        using var db = new TestDatabase();
        var series = db.Events.Create([new("match daily", "2026-10-29") { StartTime = "09:00", EndTime = "10:00", Repeat = new("daily") }])[0];
        db.Events.SetOccurrenceDone(OccurrenceId.Create(series.EventUid, "2026-10-30T00:00:00Z"), true);
        var hit = Assert.Single(Service(db).Search("match"));
        Assert.Equal(new SearchHit("event", series.EventUid, "match daily", new(2026, 10, 30), "09:00", null, true), hit);
    }

    [Fact]
    public void SortsFutureThenUndatedThenRecentPastWithAllDayTimeAndTitleTies()
    {
        using var db = new TestDatabase();
        db.Events.Create([
            new("match old", "2026-10-01"), new("match recent", "2026-10-29"),
            new("match later", "2026-11-01"), new("match late time", "2026-10-30") { StartTime = "12:00" },
            new("match B", "2026-10-30") { StartTime = "09:00" },
            new("match A", "2026-10-30") { StartTime = "09:00" },
            new("match all day", "2026-10-30")
        ]);
        db.Todos.Create([new("match undated"), new("match tomorrow todo", "2026-10-31")]);
        Assert.Equal(new[]
        {
            "match all day", "match A", "match B", "match late time", "match tomorrow todo",
            "match later", "match undated", "match recent", "match old"
        }, Service(db).Search("match").Select(hit => hit.Title));
        Assert.Equal(new[] { "match all day", "match A" }, Service(db).Search("match", 2).Select(hit => hit.Title));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" \t\r\n ")]
    public void EmptyQueryHasNoResults(string query)
    {
        using var db = new TestDatabase();
        db.Events.Create([new("event", "2026-10-30")]);
        db.Todos.Create([new("todo")]);
        Assert.Empty(Service(db).Search(query));
    }

    [Theory]
    [InlineData("%", "100% complete")]
    [InlineData("_", "file_name")]
    public void WildcardCharactersAreMatchedLiterally(string query, string title)
    {
        using var db = new TestDatabase();
        db.Events.Create([new("100% complete", "2026-10-30"), new("file_name", "2026-10-30"), new("other", "2026-10-30")]);
        Assert.Equal(title, Assert.Single(Service(db).Search(query)).Title);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void NonPositiveLimitHasNoResults(int limit)
    {
        using var db = new TestDatabase();
        db.Events.Create([new("match", "2026-10-30")]);
        Assert.Empty(Service(db).Search("match", limit));
    }

    [Fact]
    public void DefaultLimitReturnsFirstFiftyResults()
    {
        using var db = new TestDatabase();
        db.Events.Create(Enumerable.Range(0, 50).Select(i => new EventInput($"match {i:D2}", "2026-10-30")).ToArray());
        db.Events.Create([new("match 50", "2026-10-30")]);
        var hits = Service(db).Search("match");
        Assert.Equal(50, hits.Count);
        Assert.Equal("match 49", hits[^1].Title);
    }
}
