namespace DeskCal.Core.Tests;

public sealed class RepositoryTests
{
    [Fact]
    public void LocalChangesResetSyncedOrFailedOutboxStateAndPreserveRemoteMetadata()
    {
        using var db = new TestDatabase();
        var value = db.Events.Create([new("synced", "2026-10-30")])[0];
        var todo = db.Todos.Create([new("failed")])[0];
        db.Execute("UPDATE event SET local_revision=7,is_dirty=0,push_state='synced',google_etag='etag',google_gen=2 WHERE event_id=$id", ("$id", value.EventId));
        db.Execute("UPDATE todo SET local_revision=9,push_state='failed',google_task_id='remote' WHERE todo_id=$id", ("$id", todo.TodoId));
        var changed = db.Events.Update(value.EventUid, new() { IsDone = true });
        var changedTodo = db.Todos.Update(todo.TodoUid, new() { IsDone = true });
        Assert.Equal(8, changed.LocalRevision);
        Assert.Equal(10, changedTodo.LocalRevision);
        Assert.True(changed.IsDirty);
        Assert.True(changedTodo.IsDirty);
        Assert.Equal("pending", changed.PushState);
        Assert.Equal("pending", changedTodo.PushState);
        Assert.Equal(1, db.Scalar("SELECT COUNT(*) FROM event WHERE google_etag='etag' AND google_gen=2"));
        Assert.Equal(1, db.Scalar("SELECT COUNT(*) FROM todo WHERE google_task_id='remote'"));
    }

    [Fact]
    public void TimedOccurrenceUsesUtcKeyAndCanBeUncompletedWithoutChangingSeries()
    {
        using var db = new TestDatabase();
        var series = db.Events.Create([new("timed", "2026-10-30") { StartTime = "15:00", Repeat = new("daily", Count: 2) }])[0];
        var id = OccurrenceId.Create(series.EventUid, "2026-10-31T06:00:00Z");
        var exception = db.Events.SetOccurrenceDone(id, true);
        Assert.Equal("15:00", exception.StartTime);
        Assert.Equal("16:00", exception.EndTime);
        Assert.Null(exception.GoogleEventId);
        Assert.Equal(series.GoogleEventId, exception.GoogleRecurringId);
        Assert.False(db.Events.SetOccurrenceDone(id, false).IsDone);
        Assert.False(db.Events.Get(series.EventUid).IsDone);
        Assert.Equal(1, db.Events.Get(series.EventUid).LocalRevision);
        var occurrence = Assert.Single(db.Events.List(new(2026, 10, 31), new(2026, 10, 31)));
        Assert.Equal(series.EventUid, occurrence.Event.EventUid);
        Assert.Equal(id, occurrence.OccurrenceId);
    }

    [Fact]
    public void SeriesWithoutExceptionsAllowsRuleAndStartChanges()
    {
        using var db = new TestDatabase();
        var series = db.Events.Create([new("daily", "2026-10-30") { Repeat = new("daily", Count: 2) }])[0];
        var changed = db.Events.Update(series.EventUid, new() { StartDate = "2026-11-01", EndDate = "2026-11-01", Repeat = new RecurrenceInput("weekly", Weekdays: [DayOfWeek.Sunday], Count: 2) });
        Assert.Equal("2026-11-01", changed.GoogleStartRaw);
        Assert.Equal(new[] { "2026-11-01", "2026-11-08" }, db.Events.List(new(2026, 11, 1), new(2026, 11, 30)).Select(o => o.Event.StartDate));
    }

    [Fact]
    public void EventCrudKeepsRawTimesAndMarksEveryLocalWrite()
    {
        using var db = new TestDatabase();
        var value = Assert.Single(db.Events.Create([new("Dentist", "2026-10-30") { StartTime = "23:30", Memo = "memo", ColorId = "2" }]));
        Assert.Matches("^[0-9a-f]{32}$", value.EventUid);
        Assert.Equal(value.EventUid, value.GoogleEventId);
        Assert.Equal("2026-10-31", value.EndDate);
        Assert.Equal("00:30", value.EndTime);
        Assert.Equal("2026-10-30T23:30:00+09:00", value.GoogleStartRaw);
        Assert.Equal("Asia/Seoul", value.TimeZone);
        Assert.Equal(1, value.LocalRevision);
        db.Clock.Now = db.Clock.Now.AddMinutes(1);
        var changed = db.Events.Update(value.EventUid, new() { Title = "Changed", Memo = (string?)null, ColorId = (string?)null });
        Assert.Equal("", changed.Memo);
        Assert.Null(changed.ColorId);
        Assert.Equal(value.GoogleStartRaw, changed.GoogleStartRaw);
        Assert.Equal(value.GoogleEndRaw, changed.GoogleEndRaw);
        Assert.Equal(value.TimeZone, changed.TimeZone);
        Assert.NotEqual(value.UpdatedAt, changed.UpdatedAt);
        Assert.Equal(value.CreatedAt, changed.CreatedAt);
        Assert.Equal(2, changed.LocalRevision);
        Assert.Single(db.Events.List(new(2026, 10, 31), new(2026, 10, 31), "CHAN"));
        Assert.Empty(db.Events.List(new(2026, 11, 1), new(2026, 11, 1)));
        Assert.Equal(3, db.Events.Delete(value.EventUid).LocalRevision);
        Assert.Empty(db.Events.List(new(2026, 10, 30), new(2026, 10, 31)));
        Assert.Single(db.Events.List(new(2026, 10, 30), new(2026, 10, 31), includeDeleted: true));
        Assert.Throws<InvalidOperationException>(() => db.Events.Update(value.EventUid, new() { Title = "deleted" }));
        var restored = db.Events.Restore(value.EventUid);
        Assert.Null(restored.DeletedAt);
        Assert.Equal(4, restored.LocalRevision);
        Assert.True(restored.IsDirty);
        Assert.Equal("pending", restored.PushState);
    }

    [Fact]
    public void TodoCrudCompletionAndSearch()
    {
        using var db = new TestDatabase();
        var value = Assert.Single(db.Todos.Create([new("Read", "2026-10-31", "BOOK")]));
        Assert.Matches("^[0-9a-f]{32}$", value.TodoUid);
        Assert.Single(db.Todos.List(keyword: "book"));
        var changed = db.Todos.Update(value.TodoUid, new() { DueDate = (string?)null, Memo = (string?)null, IsDone = true });
        Assert.Null(changed.DueDate);
        Assert.Equal("", changed.Memo);
        Assert.Equal(db.Clock.Now.ToString("O"), changed.CompletedAt);
        Assert.Empty(db.Todos.List());
        Assert.Single(db.Todos.List("done"));
        Assert.Single(db.Todos.List("all"));
        db.Clock.Now = db.Clock.Now.AddDays(1);
        Assert.Equal(changed.CompletedAt, db.Todos.Update(value.TodoUid, new() { IsDone = true }).CompletedAt);
        Assert.Null(db.Todos.Update(value.TodoUid, new() { IsDone = false }).CompletedAt);
        Assert.Equal(5, db.Todos.Delete(value.TodoUid).LocalRevision);
        Assert.Empty(db.Todos.List());
        Assert.Single(db.Todos.List(includeDeleted: true));
        Assert.Throws<InvalidOperationException>(() => db.Todos.Update(value.TodoUid, new() { Title = "deleted" }));
        var restored = db.Todos.Restore(value.TodoUid);
        Assert.Equal(6, restored.LocalRevision);
        Assert.True(restored.IsDirty);
        Assert.Equal("pending", restored.PushState);
        Assert.Throws<ArgumentException>(() => db.Todos.List("invalid"));
    }

    [Fact]
    public void PartialUpdatesDistinguishOmittedAndNull()
    {
        using var db = new TestDatabase();
        var value = db.Events.Create([new("Event", "2026-10-30") { StartTime = "09:00", EndTime = "10:00", Memo = "keep", Repeat = new("daily") }])[0];
        var updated = db.Events.Update(value.EventUid, new() { IsDone = true });
        Assert.Equal("keep", updated.Memo);
        Assert.NotNull(updated.RecurrenceRule);
        Assert.Throws<ArgumentException>(() => db.Events.Update(value.EventUid, new() { Title = (string?)null }));
        Assert.Throws<ArgumentException>(() => db.Events.Update(value.EventUid, new() { StartDate = (string?)null }));
        Assert.Throws<ArgumentException>(() => db.Events.Update(value.EventUid, new() { EndDate = (string?)null }));
        Assert.Throws<ArgumentException>(() => db.Events.Update(value.EventUid, new() { IsDone = (bool?)null }));
        var allDay = db.Events.Update(value.EventUid, new() { StartTime = (string?)null, EndTime = (string?)null, Repeat = (RecurrenceInput?)null });
        Assert.Null(allDay.StartTime);
        Assert.Null(allDay.RecurrenceRule);
        Assert.Equal("2026-10-31", allDay.GoogleEndRaw);
        var todo = db.Todos.Create([new("Todo", Memo: "keep")])[0];
        Assert.Equal("keep", db.Todos.Update(todo.TodoUid, new() { IsDone = true }).Memo);
        Assert.Throws<ArgumentException>(() => db.Todos.Update(todo.TodoUid, new() { Title = (string?)null }));
        Assert.Throws<ArgumentException>(() => db.Todos.Update(todo.TodoUid, new() { IsDone = (bool?)null }));
    }

    [Theory]
    [InlineData("", "2026-10-30", null, null, null)]
    [InlineData("  ", "2026-10-30", null, null, null)]
    [InlineData("event", "2026-02-29", null, null, null)]
    [InlineData("event", "2026-10-30", "2026-10-29", null, null)]
    [InlineData("event", "2026-10-30", null, "24:00", null)]
    [InlineData("event", "2026-10-30", null, "9:00", null)]
    [InlineData("event", "2026-10-30", null, "09:60", null)]
    [InlineData("event", "2026-10-30", null, "09:00", "09:00")]
    [InlineData("event", "2026-10-30", null, "09:00", "08:00")]
    [InlineData("event", "2026-10-30", null, null, "10:00")]
    public void EventValidationRejectsInvalidInput(string title, string date, string? endDate, string? start, string? end)
    {
        using var db = new TestDatabase();
        Assert.Throws<ArgumentException>(() => db.Events.Create([new(title, date) { EndDate = endDate, StartTime = start, EndTime = end }]));
        Assert.Empty(db.Events.GetRecords());
    }

    [Fact]
    public void BatchRollbackAndLimits()
    {
        using var db = new TestDatabase();
        Assert.Throws<ArgumentException>(() => db.Events.Create([new("valid", "2026-10-30"), new("bad", "2026-02-30")]));
        Assert.Empty(db.Events.GetRecords());
        Assert.Throws<ArgumentException>(() => db.Todos.Create([new("valid"), new("  ")]));
        Assert.Throws<ArgumentException>(() => db.Todos.Create([new("bad", "2026-02-30")]));
        Assert.Empty(db.Todos.List("all"));
        Assert.Throws<ArgumentException>(() => db.Events.Create([]));
        Assert.Throws<ArgumentException>(() => db.Todos.Create([]));
        Assert.Throws<ArgumentException>(() => db.Events.Create(Enumerable.Repeat(new EventInput("x", "2026-10-30"), 51).ToArray()));
        Assert.Throws<ArgumentException>(() => db.Todos.Create(Enumerable.Repeat(new TodoInput("x"), 51).ToArray()));
        Assert.Equal(50, db.Events.Create(Enumerable.Repeat(new EventInput("x", "2026-10-30"), 50).ToArray()).Count);
        Assert.Equal(50, db.Todos.Create(Enumerable.Repeat(new TodoInput("x"), 50).ToArray()).Count);
    }

    [Fact]
    public void OccurrenceCompletionCancellationAndSeriesRestoreKeepExceptions()
    {
        using var db = new TestDatabase();
        var series = db.Events.Create([new("Daily", "2026-10-30") { Repeat = new("daily", Count: 3) }])[0];
        var id = OccurrenceId.Create(series.EventUid, "2026-10-31");
        var done = db.Events.SetOccurrenceDone(id, true);
        Assert.Equal(series.EventId, done.ParentEventId);
        Assert.Equal("2026-10-31", done.OriginalStartKey);
        Assert.Equal(1, done.LocalRevision);
        Assert.Equal(2, db.Events.DeleteOccurrence(id).LocalRevision);
        Assert.Equal(2, db.Events.List(new(2026, 10, 30), new(2026, 11, 1)).Count);
        Assert.Equal(3, db.Events.List(new(2026, 10, 30), new(2026, 11, 1), includeDeleted: true).Count);
        db.Events.Delete(series.EventUid);
        Assert.Empty(db.Events.List(new(2026, 10, 30), new(2026, 11, 1)));
        db.Events.Restore(series.EventUid);
        Assert.Equal(2, db.Events.List(new(2026, 10, 30), new(2026, 11, 1)).Count);
        var restored = db.Events.RestoreOccurrence(id);
        Assert.True(restored.IsDone);
        Assert.Equal(3, restored.LocalRevision);
        Assert.Equal(3, db.Events.List(new(2026, 10, 30), new(2026, 11, 1)).Count);
        Assert.Throws<InvalidOperationException>(() => db.Events.Update(series.EventUid, new() { Repeat = new RecurrenceInput("weekly", Weekdays: [DayOfWeek.Friday]) }));
        Assert.Throws<InvalidOperationException>(() => db.Events.Update(series.EventUid, new() { StartDate = "2026-10-29" }));
        Assert.Throws<KeyNotFoundException>(() => db.Events.DeleteOccurrence(OccurrenceId.Create(series.EventUid, "2026-11-02")));
        Assert.Equal(2, db.Events.GetRecords().Count);
    }

    [Fact]
    public void ComplexImportedRuleCanBeDisplayedButCannotBeEdited()
    {
        using var db = new TestDatabase();
        var series = db.Events.Create([new("Monthly", "2026-10-30")])[0];
        db.Execute("UPDATE event SET recurrence_rule='RRULE:FREQ=MONTHLY;BYDAY=-1FR' WHERE event_id=$id", ("$id", series.EventId));
        Assert.Throws<InvalidOperationException>(() => db.Events.Update(series.EventUid, new() { Repeat = (RecurrenceInput?)null }));
        var occurrence = Assert.Single(db.Events.List(new(2026, 11, 1), new(2026, 11, 30)));
        Assert.Equal("2026-11-27", occurrence.Event.StartDate);
        Assert.True(db.Events.SetOccurrenceDone(occurrence.OccurrenceId!, true).IsDone);
    }
}
