using System.IO;
using DeskCal.Core;

namespace DeskCal.App.Tests;

public sealed class EditorModelTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "deskcal-editor-" + Guid.NewGuid().ToString("N") + ".db");
    private readonly Database _database;
    private readonly EventRepository _events;
    private readonly TodoRepository _todos;
    private static readonly DateOnly Date = new(2026, 11, 2);

    public EditorModelTests()
    {
        _database = new(_path);
        _database.Initialize();
        _events = new(_database, TimeZoneInfo.FindSystemTimeZoneById("Asia/Seoul"));
        _todos = new(_database);
    }

    [Theory]
    [InlineData(null, null, "2026-11-02", null)]
    [InlineData("09:30", "10:30", "2026-11-02", "09:30")]
    [InlineData("23:30", "00:30", "2026-11-03", "23:30")]
    public void NewEventSavesAllDayOrDefaultHour(string? start, string? end, string endDate, string? savedStart)
    {
        var model = EditorModel.NewEvent(Date, start is null ? null : TimeOnly.Parse(start));
        Assert.True(model.CanChangeKind);
        model.Title = "일정";
        Assert.Null(model.Save(_events, _todos));
        var saved = Assert.Single(_events.GetRecords());
        Assert.Equal("일정", saved.Title);
        Assert.Equal("2026-11-02", saved.StartDate);
        Assert.Equal(endDate, saved.EndDate);
        Assert.Equal(savedStart, saved.StartTime);
        Assert.Equal(end, saved.EndTime);
        Assert.Equal("user", saved.CreatedBy);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NewTodoSavesOptionalDueDate(bool dated)
    {
        var model = EditorModel.NewTodo(dated ? Date : null);
        model.Title = "할 일";
        model.Memo = "메모";
        Assert.Null(model.Save(_events, _todos));
        var saved = Assert.Single(_todos.List());
        Assert.Equal(dated ? "2026-11-02" : null, saved.DueDate);
        Assert.Equal("메모", saved.Memo);
        Assert.Equal("user", saved.CreatedBy);
    }

    [Fact]
    public void ExistingEventUpdatesEditableFieldsAndClearsTimes()
    {
        var original = _events.Create([new("전", "2026-11-02") { StartTime = "09:00", CreatedBy = "claude" }])[0];
        var model = EditorModel.FromEvent(original, null);
        Assert.False(model.CanChangeKind);
        Assert.Equal("claude", model.CreatedBy);
        model.Title = "후";
        model.ColorId = "3";
        model.IsDday = true;
        model.Memo = "메모";
        model.IsAllDay = true;
        Assert.Null(model.Save(_events, _todos));
        var saved = _events.Get(original.EventUid);
        Assert.Equal("후", saved.Title);
        Assert.Equal("3", saved.ColorId);
        Assert.True(saved.IsDday);
        Assert.Equal("메모", saved.Memo);
        Assert.Null(saved.StartTime);
        Assert.Null(saved.EndTime);
        Assert.Equal("claude", saved.CreatedBy);
        model = EditorModel.FromEvent(saved, null);
        model.IsAllDay = false;
        model.StartTime = new(13, 0);
        model.EndTime = new(14, 0);
        model.ColorId = null;
        Assert.Null(model.Save(_events, _todos));
        saved = _events.Get(original.EventUid);
        Assert.Equal("13:00", saved.StartTime);
        Assert.Equal("14:00", saved.EndTime);
        Assert.Null(saved.ColorId);
    }

    [Fact]
    public void MovingMultiDayEventPreservesDuration()
    {
        var original = _events.Create([new("여행", "2026-11-02") { EndDate = "2026-11-05" }])[0];
        var model = EditorModel.FromEvent(original, null);
        model.Date = new(2026, 11, 12);
        Assert.Null(model.Save(_events, _todos));
        var saved = _events.Get(original.EventUid);
        Assert.Equal("2026-11-12", saved.StartDate);
        Assert.Equal("2026-11-15", saved.EndDate);
    }

    [Fact]
    public void WeeklyCreationUsesStartWeekday()
    {
        var model = EditorModel.NewEvent(Date, null);
        model.Title = "주간";
        model.Repeat = RepeatKind.Weekly;
        Assert.Null(model.Save(_events, _todos));
        Assert.Equal("RRULE:FREQ=WEEKLY;INTERVAL=1;BYDAY=MO", Assert.Single(_events.GetRecords()).RecurrenceRule);
    }

    [Theory]
    [InlineData("RRULE:FREQ=DAILY;INTERVAL=2", true)]
    [InlineData("RRULE:FREQ=WEEKLY", true)]
    [InlineData("RRULE:FREQ=MONTHLY;BYDAY=1MO", false)]
    [InlineData("RRULE:FREQ=DAILY;COUNT=5", true)]
    public void CustomRulesSurviveUnrelatedEdits(string rule, bool editable)
    {
        var original = _events.Create([new("반복", "2026-11-02")])[0];
        using (var connection = _database.OpenConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE event SET recurrence_rule=$rule WHERE event_uid=$uid";
            command.Parameters.AddWithValue("$rule", rule);
            command.Parameters.AddWithValue("$uid", original.EventUid);
            command.ExecuteNonQuery();
        }
        var model = EditorModel.FromEvent(_events.Get(original.EventUid), null);
        Assert.Equal(RepeatKind.Custom, model.Repeat);
        Assert.Equal(editable, model.CanEditRepeat);
        Assert.True(model.IsRecurring);
        model.Title = "수정";
        Assert.Null(model.Save(_events, _todos));
        Assert.Equal(rule, _events.Get(original.EventUid).RecurrenceRule);
    }

    [Fact]
    public void UnchangedFieldsDoNotOverwriteConcurrentChanges()
    {
        var original = _events.Create([new("원본", "2026-11-02") { Repeat = new("daily") }])[0];
        var model = EditorModel.FromEvent(original, null);
        _events.Update(original.EventUid, new() { Memo = "외부 변경", ColorId = "7", StartDate = "2026-11-03", EndDate = "2026-11-03" });
        model.Title = "편집";
        Assert.Null(model.Save(_events, _todos));
        var saved = _events.Get(original.EventUid);
        Assert.Equal("외부 변경", saved.Memo);
        Assert.Equal("7", saved.ColorId);
        Assert.Equal("2026-11-03", saved.StartDate);
    }

    [Fact]
    public void ValidationReturnsKoreanErrorsWithoutSaving()
    {
        var model = EditorModel.NewEvent(Date, new(10, 0));
        model.Title = "  ";
        Assert.Equal("제목을 입력하세요.", model.Save(_events, _todos));
        model.Title = "일정";
        model.EndTime = new(9, 0);
        Assert.Equal("끝 시간이 시작 시간보다 늦어야 합니다.", model.Save(_events, _todos));
        model.EndTime = new(10, 0);
        Assert.Equal("끝 시간이 시작 시간보다 늦어야 합니다.", model.Save(_events, _todos));
        Assert.Empty(_events.GetRecords());
    }

    [Fact]
    public void NewLateNightEventDropsExtraDayWhenTimesNoLongerCrossMidnight()
    {
        var model = EditorModel.NewEvent(Date, new(23, 30));
        model.Title = "낮으로 옮김";
        model.StartTime = new(9, 0);
        model.EndTime = new(10, 0);
        Assert.Null(model.Save(_events, _todos));
        Assert.Equal("2026-11-02", Assert.Single(_events.GetRecords()).EndDate);
    }

    [Fact]
    public void RefusedSeriesChangeExplainsWhy()
    {
        var series = _events.Create([new("반복", "2026-11-02") { Repeat = new("daily") }])[0];
        var occurrence = _events.List(Date.AddDays(1), Date.AddDays(1)).Single();
        _events.SetOccurrenceDone(occurrence.OccurrenceId!, true);
        var model = EditorModel.FromEvent(_events.Get(series.EventUid), occurrence.OccurrenceId);
        model.Repeat = RepeatKind.Weekly;
        Assert.StartsWith("따로 바뀐 회차", model.Save(_events, _todos));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DeleteTargetsOccurrenceOrWholeSeries(bool onlyOccurrence)
    {
        var original = _events.Create([new("반복", "2026-11-02") { Repeat = new("daily") }])[0];
        var occurrence = _events.List(Date, Date.AddDays(2))[1];
        var model = EditorModel.FromEvent(original, occurrence.OccurrenceId);
        Assert.Null(model.Delete(_events, _todos, onlyOccurrence));
        if (onlyOccurrence)
        {
            Assert.Null(_events.Get(original.EventUid).DeletedAt);
            Assert.Equal("cancelled", Assert.Single(_events.GetRecords(), e => e.ParentEventId == original.EventId).Status);
            Assert.Equal(new[] { "2026-11-02", "2026-11-04" }, _events.List(Date, Date.AddDays(2)).Select(o => o.Start.ToString("yyyy-MM-dd")));
        }
        else
        {
            Assert.NotNull(_events.Get(original.EventUid).DeletedAt);
            Assert.Empty(_events.List(Date, Date.AddDays(2)));
        }
    }

    [Fact]
    public void OccurrenceEditorChangesWholeSeriesEvenWithExceptions()
    {
        var original = _events.Create([new("반복", "2026-11-02") { Repeat = new("daily") }])[0];
        var occurrence = _events.List(Date, Date.AddDays(1))[1];
        _events.SetOccurrenceDone(occurrence.OccurrenceId!, true);
        var model = EditorModel.FromEvent(original, occurrence.OccurrenceId);
        model.Title = "시리즈";
        Assert.Null(model.Save(_events, _todos));
        Assert.Equal("시리즈", _events.Get(original.EventUid).Title);
        Assert.True(Assert.Single(_events.GetRecords(), e => e.ParentEventId == original.EventId).IsDone);
    }

    [Fact]
    public void ExceptionRecordEditorUsesOccurrenceSeriesForEditsAndDeletion()
    {
        var original = _events.Create([new("반복", "2026-11-02") { Repeat = new("daily") }])[0];
        var occurrence = _events.List(Date, Date.AddDays(1))[1];
        var exception = _events.SetOccurrenceDone(occurrence.OccurrenceId!, true);
        var model = EditorModel.FromEvent(exception, occurrence.OccurrenceId);
        model.Title = "시리즈 수정";
        Assert.Null(model.Save(_events, _todos));
        Assert.Equal("시리즈 수정", _events.Get(original.EventUid).Title);
        Assert.Equal("2026-11-02", _events.Get(original.EventUid).StartDate);
        Assert.Equal("RRULE:FREQ=DAILY;INTERVAL=1", _events.Get(original.EventUid).RecurrenceRule);
        Assert.Null(model.Delete(_events, _todos, false));
        Assert.NotNull(_events.Get(original.EventUid).DeletedAt);
    }

    [Fact]
    public void TodoCanBeEditedAndDeleted()
    {
        var original = _todos.Create([new("전", "2026-11-02", CreatedBy: "claude")])[0];
        var model = EditorModel.FromTodo(original);
        Assert.False(model.CanChangeKind);
        model.Title = "후";
        model.Date = null;
        model.Memo = "메모";
        Assert.Null(model.Save(_events, _todos));
        var saved = _todos.Get(original.TodoUid);
        Assert.Equal("후", saved.Title);
        Assert.Null(saved.DueDate);
        Assert.Equal("메모", saved.Memo);
        Assert.Equal("claude", saved.CreatedBy);
        Assert.Null(model.Delete(_events, _todos, true));
        Assert.NotNull(_todos.Get(original.TodoUid).DeletedAt);
    }

    [Fact]
    public void DeletedEventSaveReturnsAnError()
    {
        var original = _events.Create([new("일정", "2026-11-02")])[0];
        var model = EditorModel.FromEvent(original, null);
        _events.Delete(original.EventUid);
        model.Title = "수정";
        Assert.NotNull(model.Save(_events, _todos));
        Assert.Equal("일정", _events.Get(original.EventUid).Title);
    }

    public void Dispose()
    {
        foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(_path + suffix);
    }
}
