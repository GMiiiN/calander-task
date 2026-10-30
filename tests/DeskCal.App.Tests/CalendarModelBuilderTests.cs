using System.IO;
using DeskCal.App.Calendar;
using DeskCal.Core;

namespace DeskCal.App.Tests;

public sealed class CalendarModelBuilderTests : IDisposable
{
    private readonly string databasePath = Path.Combine(Path.GetTempPath(), "deskcal-calendar-" + Guid.NewGuid().ToString("N") + ".db");
    private readonly Database database;
    private readonly EventRepository events;
    private readonly TodoRepository todos;
    private static readonly DateOnly TODAY = new(2026, 10, 30);

    public CalendarModelBuilderTests()
    {
        database = new(databasePath);
        database.Initialize();
        events = new(database, TimeZoneInfo.FindSystemTimeZoneById("Asia/Seoul"));
        todos = new(database);
    }

    private CalendarModelBuilder Builder() => new(events, todos, database);

    [Theory]
    [InlineData(2026, 2, 4)]
    [InlineData(2026, 11, 5)]
    [InlineData(2026, 8, 6)]
    public void MonthUsesRequiredSundayBasedWeeks(int year, int month, int count)
    {
        var model = Builder().BuildMonth(year, month, TODAY);
        Assert.Equal(year, model.Year);
        Assert.Equal(month, model.Month);
        Assert.Equal(count, model.Weeks.Count);
        Assert.All(model.Weeks, week =>
        {
            Assert.Equal(7, week.Count);
            Assert.Equal(DayOfWeek.Sunday, week[0].DayOfWeek);
            Assert.Equal(DayOfWeek.Saturday, week[6].DayOfWeek);
        });
        Assert.Equal(DateTime.DaysInMonth(year, month), model.Weeks.SelectMany(w => w).Count(d => d.IsCurrentMonth));
    }

    [Fact]
    public void AdjacentMonthCellsIncludeTheirEventsAndTodayFlag()
    {
        // August has both preceding July and following September cells.
        events.Create([new("July", "2026-07-31"), new("September", "2026-09-01")]);
        var cells = Builder().BuildMonth(2026, 8, new(2026, 7, 31)).Weeks.SelectMany(w => w).ToArray();
        var previous = Assert.Single(cells, d => d.Date == new DateOnly(2026, 7, 31));
        Assert.False(previous.IsCurrentMonth);
        Assert.True(previous.IsToday);
        Assert.Equal("July", Assert.Single(previous.AllItems).Title);
        var next = Assert.Single(cells, d => d.Date == new DateOnly(2026, 9, 1));
        Assert.False(next.IsCurrentMonth);
        Assert.Equal("September", Assert.Single(next.AllItems).Title);
    }

    [Fact]
    public void MonthLimitsChipsAndOrdersGroupsWithCompletedItemsLast()
    {
        var created = events.Create([
            new("done all day", "2026-10-30"),
            new("late", "2026-10-30") { StartTime = "15:00" },
            new("done early", "2026-10-30") { StartTime = "08:00" },
            new("all day", "2026-10-30"),
            new("early", "2026-10-30") { StartTime = "09:00" }
        ]);
        events.Update(created[0].EventUid, new() { IsDone = true });
        events.Update(created[2].EventUid, new() { IsDone = true });
        var tasks = todos.Create([new("done todo", "2026-10-30"), new("todo", "2026-10-30"), new("undated")]);
        todos.Update(tasks[0].TodoUid, new() { IsDone = true });
        var cell = Cell(Builder().BuildMonth(2026, 10, TODAY), TODAY);
        Assert.Equal(new[] { "all day", "done all day", "early", "late", "done early", "todo", "done todo" }, cell.AllItems.Select(c => c.Title));
        Assert.Equal(cell.AllItems.Take(2), cell.Chips);
        Assert.Equal(5, cell.OverflowCount);
        Assert.True(cell.IsToday);
        Assert.Equal("09:00", cell.AllItems[2].TimeText);
        Assert.Null(cell.AllItems[0].TimeText);
        Assert.Null(cell.AllItems[5].TimeText);
    }

    [Fact]
    public void ChipsCarryCategoryFallbackAndPublicIdentifiers()
    {
        Execute("INSERT INTO category(color_id, name, color_hex) VALUES ('red', 'Red', '#FF0000');");
        var created = events.Create([
            new("colored", "2026-10-30") { ColorId = "red", IsDday = true, CreatedBy = "claude" },
            new("missing", "2026-10-30") { ColorId = "unknown" },
            new("default", "2026-10-30")
        ]);
        var task = todos.Create([new("task", "2026-10-30", CreatedBy: "claude")])[0];
        var chips = Cell(Builder().BuildMonth(2026, 10, TODAY), TODAY).AllItems;
        var colored = Assert.Single(chips, c => c.Title == "colored");
        Assert.Equal("#FF0000", colored.ColorHex);
        Assert.Equal(created[0].EventUid, colored.EventId);
        Assert.True(colored.IsDday);
        Assert.False(colored.IsTodo);
        Assert.Null(colored.OccurrenceId);
        Assert.Null(colored.TodoId);
        Assert.Equal("claude", colored.CreatedBy);
        Assert.All(chips.Where(c => c.Title is "missing" or "default"), c => Assert.Equal("#246BDE", c.ColorHex));
        var todo = Assert.Single(chips, c => c.IsTodo);
        Assert.Equal("#B0BACB", todo.ColorHex);
        Assert.Equal(task.TodoUid, todo.TodoId);
        Assert.Equal("claude", todo.CreatedBy);
        Assert.Null(todo.EventId);
        Assert.False(todo.IsDday);
    }

    [Fact]
    public void MonthRepeatsMultiDayItemsAndExcludesMidnightEnd()
    {
        events.Create([
            new("all day", "2026-10-29") { EndDate = "2026-10-31" },
            new("timed", "2026-10-29") { EndDate = "2026-10-31", StartTime = "23:00", EndTime = "00:00" }
        ]);
        var model = Builder().BuildMonth(2026, 10, TODAY);
        Assert.Equal(2, Cell(model, new(2026, 10, 29)).AllItems.Count);
        Assert.Equal(2, Cell(model, TODAY).AllItems.Count);
        Assert.Equal("all day", Assert.Single(Cell(model, new(2026, 10, 31)).AllItems).Title);
        Assert.Empty(Cell(model, new(2026, 10, 28)).AllItems);
    }

    [Fact]
    public void RecurrencesUseOccurrenceIdsAndExcludeCancelledDeletedItems()
    {
        var series = events.Create([new("repeat", "2026-10-28") { Repeat = new("daily", Count: 4) }])[0];
        var occurrences = events.List(new(2026, 10, 28), new(2026, 10, 31));
        events.DeleteOccurrence(occurrences[1].OccurrenceId!);
        events.SetOccurrenceDone(occurrences[2].OccurrenceId!, true);
        var deleted = events.Create([new("deleted", "2026-10-30")])[0];
        events.Delete(deleted.EventUid);
        var deletedTodo = todos.Create([new("deleted todo", "2026-10-30")])[0];
        todos.Delete(deletedTodo.TodoUid);
        var builder = Builder();
        var model = builder.BuildMonth(2026, 10, TODAY);
        Assert.Empty(Cell(model, new(2026, 10, 29)).AllItems);
        var done = Assert.Single(Cell(model, TODAY).AllItems);
        Assert.True(done.IsDone);
        Assert.Equal(series.EventUid, done.EventId);
        Assert.Equal(occurrences[2].OccurrenceId, done.OccurrenceId);
        Assert.Equal(occurrences[0].OccurrenceId, Assert.Single(Cell(model, new(2026, 10, 28)).AllItems).OccurrenceId);
        Assert.Equal(3, builder.BuildWeek(TODAY, TODAY).Days.Sum(d => d.AllDay.Count));
    }

    [Fact]
    public void WeekSplitsAtMidnightAndIncludesDueTodosInAllDayRow()
    {
        events.Create([
            new("overnight", "2026-10-29") { StartTime = "23:00", EndDate = "2026-10-30", EndTime = "01:30" },
            new("midnight", "2026-10-30") { StartTime = "22:00", EndDate = "2026-10-31", EndTime = "00:00" },
            new("all day", "2026-10-30")
        ]);
        var task = todos.Create([new("done todo", "2026-10-30")])[0];
        todos.Update(task.TodoUid, new() { IsDone = true });
        var model = Builder().BuildWeek(TODAY, TODAY);
        Assert.Equal(new DateOnly(2026, 10, 25), model.WeekStart);
        Assert.Equal(7, model.Days.Count);
        var thursday = model.Days[4];
        var first = Assert.Single(thursday.Blocks);
        Assert.Equal((1380, 1440), (first.StartMinute, first.EndMinute));
        var friday = model.Days[5];
        Assert.True(friday.IsToday);
        Assert.Equal((0, 90), (friday.Blocks[0].StartMinute, friday.Blocks[0].EndMinute));
        Assert.Equal("23:00", friday.Blocks[0].Chip.TimeText);
        Assert.Equal((1320, 1440), (friday.Blocks[1].StartMinute, friday.Blocks[1].EndMinute));
        Assert.Equal(new[] { "all day", "done todo" }, friday.AllDay.Select(c => c.Title));
        Assert.True(friday.AllDay[1].IsDone);
        Assert.Empty(model.Days[6].Blocks);
    }

    [Fact]
    public void WeekAssignsColumnsAcrossConnectedGroupsAndReusesFreeColumns()
    {
        events.Create([
            new("A", "2026-10-30") { StartTime = "09:00", EndTime = "10:00" },
            new("B", "2026-10-30") { StartTime = "09:30", EndTime = "11:00" },
            new("C", "2026-10-30") { StartTime = "10:00", EndTime = "10:30" },
            new("D", "2026-10-30") { StartTime = "10:15", EndTime = "10:45" },
            new("adjacent", "2026-10-30") { StartTime = "11:00", EndTime = "12:00" },
            new("separate", "2026-10-30") { StartTime = "13:00", EndTime = "14:00" }
        ]);
        var blocks = Builder().BuildWeek(TODAY, TODAY).Days[5].Blocks;
        Assert.Equal(new[] { 0, 1, 0, 2, 0, 0 }, blocks.Select(b => b.Column));
        Assert.Equal(new[] { 3, 3, 3, 3, 1, 1 }, blocks.Select(b => b.ColumnCount));
        foreach (var left in blocks)
        foreach (var right in blocks)
            if (left != right && left.StartMinute < right.EndMinute && right.StartMinute < left.EndMinute)
                Assert.NotEqual(left.Column, right.Column);
    }

    [Fact]
    public void WeekSeparatesShortBackToBackBlocksThatWouldOverlapOnScreen()
    {
        events.Create([
            new("short1", "2026-10-30") { StartTime = "09:00", EndTime = "09:10" },
            new("short2", "2026-10-30") { StartTime = "09:10", EndTime = "09:20" },
            new("after", "2026-10-30") { StartTime = "09:30", EndTime = "10:00" }
        ]);
        var blocks = Builder().BuildWeek(TODAY, TODAY).Days[5].Blocks;
        Assert.Equal(new[] { 0, 1, 0 }, blocks.Select(b => b.Column));
        Assert.Equal(new[] { 2, 2, 2 }, blocks.Select(b => b.ColumnCount));
    }

    private static DayCellModel Cell(MonthModel model, DateOnly date) => model.Weeks.SelectMany(w => w).Single(d => d.Date == date);

    private void Execute(string sql)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    public void Dispose()
    {
        foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(databasePath + suffix);
    }
}
