using System.IO;
using DeskCal.App.Calendar;
using DeskCal.Core;

namespace DeskCal.App.Tests;

public sealed class ChipDoneTests : IDisposable
{
    private static readonly DateOnly Day = new(2024, 5, 15);
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"deskcal-chip-{Guid.NewGuid():N}.db");
    private readonly EventRepository _events;
    private readonly TodoRepository _todos;
    private readonly MainViewModel _model;

    public ChipDoneTests()
    {
        var database = new Database(_path);
        database.Initialize();
        _events = new(database);
        _todos = new(database);
        _model = new(database);
        _model.GoTo(Day);
    }

    private DayCellModel Cell => _model.Month!.CellOn(Day)!;
    private WeekDayModel WeekDay => _model.Week!.Days.Single(day => day.Date == Day);
    private void Set(ChipModel chip, bool done) => _model.SetDone(chip.EventId, chip.OccurrenceId, chip.TodoId, done);

    [Fact]
    public void PastSingleEventCanBeCompletedAndReopenedFromMonthAndWeek()
    {
        var id = _events.Create([new EventInput("지난 회의", "2024-05-15"), new("다른 일정", "2024-05-15")])[0].EventUid;
        _model.Refresh();
        var chip = Cell.Chips.Single(c => c.EventId == id);
        Assert.Equal("완료", chip.DoneMenuText);
        Set(chip, true);
        Assert.True(Cell.Chips.Last().IsDone);
        Assert.Equal(id, Cell.Chips.Last().EventId);
        Assert.DoesNotContain(_model.Groups.SelectMany(g => g.Items), i => i.EventId == id);

        _model.ShowWeekCommand.Execute(null);
        chip = WeekDay.AllDay.Single(c => c.EventId == id);
        Assert.Equal("완료 취소", chip.DoneMenuText);
        Set(chip, false);
        Assert.False(_events.Get(id).IsDone);
        Assert.False(WeekDay.AllDay.Single(c => c.EventId == id).IsDone);
    }

    [Fact]
    public void PastWeekBlockChangesOnlyTheSelectedOccurrenceInBothDirections()
    {
        var series = _events.Create([new EventInput("주간 회의", "2024-05-08") {
            StartTime = "14:00", EndTime = "15:00", Repeat = new("weekly", Weekdays: [DayOfWeek.Wednesday]) }])[0];
        _model.ShowWeekCommand.Execute(null);
        var targetId = Assert.Single(WeekDay.Blocks).Chip.OccurrenceId;
        Assert.NotNull(targetId);
        foreach (var done in new[] { true, false })
        {
            var chip = Assert.Single(WeekDay.Blocks).Chip;
            Assert.Equal(targetId, chip.OccurrenceId);
            Assert.Equal(done ? "이 회차 완료" : "이 회차 완료 취소", chip.DoneMenuText);
            Set(chip, done);
            Assert.Equal(done, Assert.Single(WeekDay.Blocks).Chip.IsDone);
            var occurrences = _events.List(Day.AddDays(-7), Day.AddDays(7));
            Assert.Equal(new[] { false, done, false }, occurrences.Select(o => o.Event.IsDone));
            Assert.False(_events.Get(series.EventUid).IsDone);
        }
    }

    [Fact]
    public void MiddleCellOfMultiDayBarChangesEveryDayInBothDirections()
    {
        var id = _events.Create([new EventInput("출장", "2024-05-14") { EndDate = "2024-05-16" }])[0].EventUid;
        _model.Refresh();
        foreach (var done in new[] { true, false })
        {
            var middle = Assert.Single(Cell.Chips);
            Assert.True(middle.JoinsLeft && middle.JoinsRight);
            Set(middle, done);
            Assert.Equal(done, _events.Get(id).IsDone);
            Assert.All(new[] { -1, 0, 1 }, offset =>
                Assert.Equal(done, Assert.Single(_model.Month!.CellOn(Day.AddDays(offset))!.Chips).IsDone));
        }
    }

    [Fact]
    public void PastDueTodoCanBeCompletedAndReopenedOnItsDueDate()
    {
        var id = _todos.Create([new TodoInput("자료 제출", "2024-05-15")])[0].TodoUid;
        _model.Refresh();
        foreach (var done in new[] { true, false })
        {
            var chip = Assert.Single(Cell.Chips);
            Assert.Equal(id, chip.TodoId);
            Set(chip, done);
            Assert.Equal(done, Assert.Single(Cell.Chips).IsDone);
            Assert.Equal(done, _todos.Get(id).CompletedAt is not null);
        }
    }

    [Fact]
    public void TomorrowChipAndSidebarCanReopenEachOthersChanges()
    {
        var tomorrow = DateOnly.FromDateTime(DateTime.Today).AddDays(1);
        var id = _events.Create([new EventInput("내일 회의", tomorrow.ToString("yyyy-MM-dd"))])[0].EventUid;
        _model.GoTo(tomorrow);
        ChipModel Chip() => Assert.Single(_model.Month!.CellOn(tomorrow)!.Chips);
        SidebarItemViewModel Row() => _model.Groups.SelectMany(g => g.Items).Single(i => i.EventId == id);

        Set(Chip(), true);
        Assert.True(Row().IsDone);
        _model.ToggleDoneCommand.Execute(Row());
        Assert.False(Chip().IsDone);
        _model.ToggleDoneCommand.Execute(Row());
        Assert.True(Chip().IsDone);
        Set(Chip(), false);
        Assert.False(Row().IsDone);
    }

    [Fact]
    public void OverflowItemsRefreshAfterCompletingAndReopeningAllFourKinds()
    {
        _events.Create([new EventInput("출장", "2024-05-14") { EndDate = "2024-05-16" },
            new("회의", "2024-05-15"), new("반복", "2024-05-08") { Repeat = new("weekly", Weekdays: [DayOfWeek.Wednesday]) }]);
        _todos.Create([new TodoInput("제출", "2024-05-15")]);
        _model.Refresh();
        Assert.Equal(2, Cell.OverflowCount);
        foreach (var title in new[] { "출장", "회의", "반복", "제출" })
        {
            foreach (var done in new[] { true, false })
            {
                Set(Cell.AllItems.Single(c => c.Title == title), done);
                Assert.Equal(done, Cell.AllItems.Single(c => c.Title == title).IsDone);
                Assert.All(Cell.AllItems.Where(c => c.Title != title), other => Assert.False(other.IsDone));
            }
        }
    }

    public void Dispose()
    {
        foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(_path + suffix);
    }
}
