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

    // feedback 3-7: +N 목록은 제목을 자르지 않고, 아래 줄에 시간 범위·기간·종일·할 일을 보여 준다.
    [Fact]
    public void OverflowItemsCarryFullTitleAndDetail()
    {
        events.Create([
            new EventInput("[모두의 창업] MVP 설계·세팅 + API 연동 테스트까지 한 번에 끝내기", "2026-11-13") { StartTime = "13:00", EndTime = "16:00" },
            new EventInput("AI 솔루션 2차", "2026-11-11") { EndDate = "2026-11-15" },
            new EventInput("종일 일정", "2026-11-13"),
        ]);
        todos.Create([new TodoInput("보고서 제출", "2026-11-13")]);

        var cell = Builder().BuildMonth(2026, 11, TODAY).Weeks.SelectMany(w => w).Single(c => c.Date == new DateOnly(2026, 11, 13));
        string? Detail(string title) => cell.AllItems.Single(i => i.Title == title).DetailText;

        Assert.Equal(4, cell.AllItems.Count);
        Assert.Equal("13:00–16:00", Detail("[모두의 창업] MVP 설계·세팅 + API 연동 테스트까지 한 번에 끝내기"));
        Assert.Equal("11월 11일 - 11월 15일", Detail("AI 솔루션 2차"));
        Assert.Equal("종일", Detail("종일 일정"));
        Assert.Equal("할 일 · 이날 마감", Detail("보고서 제출"));
    }

    [Theory]
    [InlineData(2027, 2, 4)]
    [InlineData(2026, 2, 5)]
    [InlineData(2026, 11, 6)]
    public void MonthUsesRequiredMondayBasedWeeks(int year, int month, int count)
    {
        var model = Builder().BuildMonth(year, month, TODAY);
        Assert.Equal(year, model.Year);
        Assert.Equal(month, model.Month);
        Assert.Equal(count, model.Weeks.Count);
        Assert.All(model.Weeks, week =>
        {
            Assert.Equal(7, week.Count);
            Assert.Equal(DayOfWeek.Monday, week[0].DayOfWeek);
            Assert.Equal(DayOfWeek.Sunday, week[6].DayOfWeek);
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

    [Theory]
    [InlineData(100)]
    [InlineData(115)]
    [InlineData(130)]
    public void MonthLimitsChipsAndOrdersEventsByCreationThenTodosByCompletion(int textSize)
    {
        var settings = new SettingStore(database);
        Assert.Equal(100, settings.TextSizePercent); // an existing DB has no text-size setting
        settings.TextSizePercent = textSize;
        var reopened = new SettingStore(new Database(databasePath));
        Assert.Equal(textSize, reopened.TextSizePercent);
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
        Assert.Equal(created.OrderBy(e => e.CreatedAt, StringComparer.Ordinal).ThenBy(e => e.EventUid, StringComparer.Ordinal).Select(e => e.Title)
            .Concat(["todo", "done todo"]), cell.AllItems.Select(c => c.Title));
        Assert.Equal(cell.AllItems.Take(2), cell.Chips);
        Assert.Equal(5, cell.OverflowCount);
        Assert.True(cell.IsToday);
        Assert.Equal("09:00", cell.AllItems.Single(c => c.Title == "early").TimeText);
        Assert.Null(cell.AllItems[0].TimeText);
        Assert.Null(cell.AllItems[5].TimeText);
        reopened.TextSizePercent = 100;
        Assert.Equal(100, settings.TextSizePercent);
        reopened.Set("text_size_percent", "invalid");
        Assert.Equal(100, settings.TextSizePercent);
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

    // feedback 3-1: 여러 날 일정은 이어진 막대. 이름은 시작일에 쓰고,
    // 다음 줄 월요일에는 그 주를 넘어 계속되거나 14일 이상일 때만, "- 마감"은 다른 주에서 끝날 때만 쓴다.
    [Fact]
    public void MonthDrawsMultiDayEventsAsJoinedBarsWithTitlesOnlyWhereNeeded()
    {
        events.Create([new("trip", "2026-11-02") { EndDate = "2026-11-11" }]);
        var model = Builder().BuildMonth(2026, 11, TODAY);
        ChipModel Bar(int day) => Assert.Single(Cell(model, new(2026, 11, day)).Chips);

        Assert.Equal(("trip", false, true), (Bar(2).Title, Bar(2).JoinsLeft, Bar(2).JoinsRight));
        Assert.Equal("11월 2일 - 11월 11일", Bar(2).PeriodText);
        foreach (var day in new[] { 3, 4, 5, 6, 7, 8, 9, 10 }) // 9일(월): 그 주 안에 끝나고 10일짜리라 생략
            Assert.Equal(("", true, true), (Bar(day).Title, Bar(day).JoinsLeft, Bar(day).JoinsRight));
        Assert.Equal(("trip - 마감", true, false), (Bar(11).Title, Bar(11).JoinsLeft, Bar(11).JoinsRight));
        Assert.Equal("trip", Assert.Single(Cell(model, new(2026, 11, 5)).AllItems).Title); // +N 목록은 원래 이름
        Assert.Empty(Cell(model, new(2026, 11, 12)).Chips);
    }

    [Fact]
    public void BarEndingOnRowStartShowsOnlyTheEndTitle()
    {
        events.Create([new("short", "2026-11-08") { EndDate = "2026-11-09" }]);
        var model = Builder().BuildMonth(2026, 11, TODAY);
        Assert.Equal("short", Assert.Single(Cell(model, new(2026, 11, 8)).Chips).Title);
        Assert.Equal("short - 마감", Assert.Single(Cell(model, new(2026, 11, 9)).Chips).Title);
    }

    [Theory]
    [InlineData("2026-11-02", "2026-11-15", "x", "x - 마감")]  // 14일짜리: 월요일에 다시 적는다
    [InlineData("2026-11-06", "2026-11-15", "", "x - 마감")]   // 그 주 일요일에 끝남: 월요일 생략
    [InlineData("2026-11-06", "2026-11-17", "x", "")]          // 다음 주로 넘어감: 월요일에 적는다(마감은 17일)
    public void RowStartTitleNeedsALongEventOrOneThatContinuesPastTheWeek(string start, string end, string monday, string sunday)
    {
        events.Create([new("x", start) { EndDate = end }]);
        var model = Builder().BuildMonth(2026, 11, TODAY);
        Assert.Equal(monday, Assert.Single(Cell(model, new(2026, 11, 9)).Chips).Title);
        Assert.Equal(sunday, Assert.Single(Cell(model, new(2026, 11, 15)).Chips).Title);
    }

    [Fact]
    public void EventEndingInItsStartWeekHasNoEndTitle()
    {
        events.Create([new("week", "2026-11-02") { EndDate = "2026-11-08" }]);
        var model = Builder().BuildMonth(2026, 11, TODAY);
        Assert.Equal("week", Assert.Single(Cell(model, new(2026, 11, 2)).Chips).Title);
        Assert.Equal("", Assert.Single(Cell(model, new(2026, 11, 8)).Chips).Title);
    }

    // 리뷰 B-M1: 같은 막대가 날짜마다 줄을 바꿔 계단처럼 끊겼다(v0.8.2 대비 회귀).
    // 인접한 날의 같은 막대는 같은 줄이다. C는 비어 있는 첫 레인에 들어가고 B는 제 줄에 남는다.
    [Fact]
    public void AdjacentDaysShowTheSameBarOnTheSameRow()
    {
        events.Create([
            new("A", "2026-11-02") { EndDate = "2026-11-04" },
            new("B", "2026-11-03") { EndDate = "2026-11-05" },
            new("solo", "2026-11-03"),
            new("late", "2026-11-06")
        ]);
        events.Create([new("C", "2026-11-05") { EndDate = "2026-11-06" }]);
        var model = Builder().BuildMonth(2026, 11, TODAY);
        string[] Titles(int day) => Cell(model, new(2026, 11, day)).Chips.Select(c => c.IsPlaceholder ? "_" : c.Title).ToArray();

        AssertBarsKeepTheirRow(model);
        Assert.Equal(new[] { "A" }, Titles(2));
        Assert.Equal(new[] { "", "B" }, Titles(3));
        Assert.Equal(1, Cell(model, new(2026, 11, 3)).OverflowCount); // solo는 막대 아래 세 번째 줄 → +1
        Assert.Equal(new[] { "", "" }, Titles(4)); // 같은 주에 끝나므로 마감 표시 없음
        Assert.Equal(new[] { "C", "" }, Titles(5));
        Assert.Equal(new[] { "", "late" }, Titles(6));
        Assert.Equal(new[] { "C", "late" }, Cell(model, new(2026, 11, 6)).AllItems.Select(i => i.Title));
    }

    // 리뷰 B의 재현: 먼저 만든 짧은 여행과 한 주 과제가 같은 날 시작하고 수요일에 단일 일정이 있다.
    // v0.8.2처럼 긴 막대가 월~금 내내 첫 줄이고, 수요일의 단일 일정은 그 아래다.
    [Fact]
    public void LongerBarStartingTheSameDayStaysOnTopAllWeek()
    {
        events.Create([new("짧은 여행", "2026-11-02") { EndDate = "2026-11-03" }]);
        var week = events.Create([new("한 주 과제", "2026-11-02") { EndDate = "2026-11-06" }, new("수요 단일", "2026-11-04")])[0];
        var model = Builder().BuildMonth(2026, 11, TODAY);

        AssertBarsKeepTheirRow(model);
        foreach (var day in new[] { 2, 3, 4, 5, 6 })
            Assert.Equal(week.EventUid, Cell(model, new(2026, 11, day)).Chips[0].EventId);
        Assert.Equal(new[] { "", "수요 단일" }, Cell(model, new(2026, 11, 4)).Chips.Select(c => c.Title));
        Assert.Equal(new[] { "한 주 과제", "짧은 여행" }, Cell(model, new(2026, 11, 2)).AllItems.Select(c => c.Title));
    }

    // 막대 사이의 빈 레인 때문에 단일 일정이 두 줄 밖으로 밀릴 때는 그 빈 자리로만 올린다. 막대는 그대로다.
    [Fact]
    public void HiddenSingleFillsALaneGapWithoutMovingTheBar()
    {
        events.Create([new("X", "2026-11-02") { EndDate = "2026-11-03" }]);
        var bar = events.Create([new("Y", "2026-11-03") { EndDate = "2026-11-06" }, new("단일", "2026-11-04")])[0];
        var model = Builder().BuildMonth(2026, 11, TODAY);

        AssertBarsKeepTheirRow(model);
        foreach (var day in new[] { 3, 4, 5, 6 })
            Assert.Equal(bar.EventUid, Cell(model, new(2026, 11, day)).Chips[1].EventId);
        Assert.Equal("단일", Cell(model, new(2026, 11, 4)).Chips[0].Title);
        Assert.Equal(0, Cell(model, new(2026, 11, 4)).OverflowCount);
        Assert.True(Cell(model, new(2026, 11, 5)).Chips[0].IsPlaceholder);
    }

    // 앞날에서 이어지는 막대(JoinsLeft)는 앞날의 같은 줄에 같은 일정으로 있어야 한다.
    private static void AssertBarsKeepTheirRow(MonthModel model)
    {
        foreach (var week in model.Weeks)
            for (var day = 1; day < 7; day++)
                for (var row = 0; row < week[day].Chips.Count; row++)
                {
                    var chip = week[day].Chips[row];
                    if (chip.IsPlaceholder || !chip.JoinsLeft) continue;
                    var before = week[day - 1].Chips.ElementAtOrDefault(row);
                    Assert.True(before is not null && before.EventId == chip.EventId && before.OccurrenceId == chip.OccurrenceId,
                        $"{week[day].Date}: '{chip.EventId}' row {row} differs from the previous day");
                }
    }

    [Fact]
    public void LaneGapBelowABarIsKeptWithAPlaceholder()
    {
        events.Create([
            new("A", "2026-11-02") { EndDate = "2026-11-03" },
            new("B", "2026-11-03") { EndDate = "2026-11-04" }
        ]);
        var cell = Cell(Builder().BuildMonth(2026, 11, TODAY), new(2026, 11, 4));
        Assert.True(cell.Chips[0].IsPlaceholder);
        Assert.Equal("", cell.Chips[1].Title);
        Assert.True(cell.Chips[1].JoinsLeft);
        Assert.Equal(0, cell.OverflowCount);
    }

    [Fact]
    public void TimedMultiDayBarShowsStartTimeOnlyOnItsFirstDay()
    {
        events.Create([new("overnight", "2026-10-29") { StartTime = "23:00", EndDate = "2026-10-30", EndTime = "01:30" }]);
        var model = Builder().BuildMonth(2026, 10, TODAY);
        var first = Assert.Single(Cell(model, new(2026, 10, 29)).Chips);
        Assert.Equal(("overnight", "23:00"), (first.Title, first.TimeText));
        var last = Assert.Single(Cell(model, TODAY).Chips);
        Assert.Equal(("", null), (last.Title, last.TimeText));
        Assert.Equal("10월 29일 오후 11:00 - 10월 30일 오전 1:30", last.PeriodText);
    }

    [Fact]
    public void WeekAllDayRowUsesTheSameBarRules()
    {
        events.Create([new("trip", "2026-11-02") { EndDate = "2026-11-11" }]);
        var builder = Builder();
        var first = builder.BuildWeek(new(2026, 11, 2), TODAY).Days;
        Assert.Equal(("trip", false, true), (first[0].AllDay[0].Title, first[0].AllDay[0].JoinsLeft, first[0].AllDay[0].JoinsRight));
        Assert.Equal("", first[6].AllDay[0].Title);
        var second = builder.BuildWeek(new(2026, 11, 9), TODAY).Days;
        Assert.Equal("", second[0].AllDay[0].Title);
        Assert.Equal("trip - 마감", second[2].AllDay[0].Title);
        Assert.Empty(second[3].AllDay);
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
        Assert.Equal(new DateOnly(2026, 10, 26), model.WeekStart);
        Assert.Equal(7, model.Days.Count);
        var thursday = model.Days[3];
        var first = Assert.Single(thursday.Blocks);
        Assert.Equal((1380, 1440), (first.StartMinute, first.EndMinute));
        var friday = model.Days[4];
        Assert.True(friday.IsToday);
        Assert.Equal((0, 90), (friday.Blocks[0].StartMinute, friday.Blocks[0].EndMinute));
        Assert.Equal("23:00", friday.Blocks[0].Chip.TimeText);
        Assert.Equal((1320, 1440), (friday.Blocks[1].StartMinute, friday.Blocks[1].EndMinute));
        Assert.Equal(new[] { "all day", "done todo" }, friday.AllDay.Select(c => c.Title));
        Assert.True(friday.AllDay[1].IsDone);
        Assert.Empty(model.Days[5].Blocks);
    }

    [Fact]
    public void WeekDrawsTimedEventsOfADayOrLongerAsAllDayBars()
    {
        events.Create([
            new("hackathon", "2026-10-30") { StartTime = "18:00", EndDate = "2026-11-01", EndTime = "12:00" },
            new("exactly day", "2026-10-27") { StartTime = "09:00", EndDate = "2026-10-28", EndTime = "09:00" },
            new("almost day", "2026-10-26") { StartTime = "09:00", EndDate = "2026-10-27", EndTime = "08:59" }
        ]);
        var days = Builder().BuildWeek(TODAY, TODAY).Days;
        Assert.DoesNotContain(days.SelectMany(d => d.Blocks), b => b.Chip.Title is "hackathon" or "exactly day");
        Assert.Equal(new[] { true, true, true }, days.Skip(4).Select(d => d.AllDay.Any(c => c.EventId is not null && c.PeriodText!.Contains("오후 6:00"))));
        Assert.Equal("hackathon", days[4].AllDay.Single(c => c.EventId is not null).Title);
        Assert.Equal(new[] { true, true }, days.Skip(1).Take(2).Select(d => d.AllDay.Any(c => c.EventId is not null)));
        Assert.Equal(new[] { "almost day", "almost day" }, days.Take(2).SelectMany(d => d.Blocks).Select(b => b.Chip.Title));
    }

    [Fact]
    public void WeekBlocksUnderAnHourAreCompactAndCarryTheFullRange()
    {
        events.Create([
            new("short", "2026-10-30") { StartTime = "09:00", EndTime = "09:50" },
            new("hour", "2026-10-30") { StartTime = "13:00", EndTime = "14:00" },
            new("overnight", "2026-10-30") { StartTime = "23:00", EndDate = "2026-10-31", EndTime = "01:30" }
        ]);
        var days = Builder().BuildWeek(TODAY, TODAY).Days;
        Assert.Equal(new[] { true, false, false }, days[4].Blocks.Select(b => b.IsCompact));
        Assert.Equal("09:00–09:50", days[4].Blocks[0].Chip.DetailText);
        Assert.Equal((0, 90), (days[5].Blocks[0].StartMinute, days[5].Blocks[0].EndMinute));
        Assert.Contains("10월 31일", days[5].Blocks[0].Chip.DetailText);
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
        var blocks = Builder().BuildWeek(TODAY, TODAY).Days[4].Blocks;
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
        var blocks = Builder().BuildWeek(TODAY, TODAY).Days[4].Blocks;
        Assert.Equal(new[] { 0, 1, 0 }, blocks.Select(b => b.Column));
        Assert.Equal(new[] { 2, 2, 2 }, blocks.Select(b => b.ColumnCount));
    }

    [Theory]
    [InlineData("2026-11-03", "2026-11-05", "2026-11-05", "여행 - 마감")]
    [InlineData("2026-11-08", "2026-11-10", "2026-11-09", "여행")]
    [InlineData("2026-11-08", "2026-11-09", "2026-11-09", "여행 - 마감")]
    [InlineData("2026-10-30", "2026-11-12", "2026-11-02", "여행")]
    public void DetailedMonthLabelsFollowOccupiedDates(string start, string end, string date, string title)
    {
        var item = events.Create([new("여행", start) { EndDate = end }]).Single();
        var builder = new CalendarModelBuilder(events, todos, database, true);
        var cell = Cell(builder.BuildMonth(2026, 11, TODAY), DateOnly.Parse(date));
        Assert.Equal(title, cell.Chips.Single().Title);
        Assert.Equal(item.EventUid, cell.AllItems.Single().EventId);
    }

    [Fact]
    public void DetailedWeekUsesSelectedBoundaryAndMovesOnlyActualOvernights()
    {
        events.Create([new("밤샘", "2026-11-03") { StartTime = "23:00", EndDate = "2026-11-04", EndTime = "01:00" },
            new("자정", "2026-11-04") { StartTime = "23:00", EndDate = "2026-11-05", EndTime = "00:00" }]);
        var builder = new CalendarModelBuilder(events, todos, database, true);
        var day = builder.BuildWeek(new(2026, 11, 04), TODAY, DayOfWeek.Wednesday).Days[0];
        Assert.Equal("밤샘 - 마감", day.AllDay.Single().Title);
        Assert.Equal("자정", day.Blocks.Single().Chip.Title);
    }

    [Fact]
    public void StudentWeeksNavigateAcrossMonthsWhileMonthStartsMonday()
    {
        var settings = new SettingStore(database);
        Assert.False(settings.Semester.Enabled);
        settings.Semester = new(true, new(2026, 10, 28), 16);
        var vm = new MainViewModel(database);
        vm.GoTo(new(2026, 11, 1));
        vm.ShowWeekCommand.Execute(null);
        Assert.Equal(new DateOnly(2026, 10, 28), vm.Week!.WeekStart);
        vm.NextCommand.Execute(null);
        Assert.Equal(new DateOnly(2026, 11, 4), vm.Week!.WeekStart);
        vm.ShowMonthCommand.Execute(null);
        Assert.Equal(DayOfWeek.Monday, vm.Month!.Weeks[0][0].DayOfWeek);
    }

    [Fact]
    public void DayOrderSurvivesRestartAndIsSharedByCalendarAndAgenda()
    {
        var values = events.Create([new("기간", "2026-10-29") { EndDate = "2026-10-31" },
            new("단일", "2026-10-30"), new("반복", "2026-10-30") { Repeat = new("daily", Count: 2) }]);
        Execute("UPDATE event SET created_at='2026-10-01T00:00:00Z'");
        var before = Cell(Builder().BuildMonth(2026, 10, TODAY), TODAY).AllItems;
        string Key(ChipModel c) => c.OccurrenceId ?? c.EventId!;
        Assert.Equal(before.Skip(1).Select(Key).Order(StringComparer.Ordinal), before.Skip(1).Select(Key));
        var next = events.List(TODAY.AddDays(1), TODAY.AddDays(1)).Single(o => o.OccurrenceId != null);
        Assert.False(DayEventOrder.Move(events, TODAY, next.OccurrenceId!, Key(before[1])));
        Assert.False(DayEventOrder.Move(events, TODAY, Key(before[1]), values[0].EventUid));
        Assert.True(DayEventOrder.Move(events, TODAY, Key(before[2]), Key(before[1])));
        var db = new Database(databasePath);
        db.Initialize();
        var ev = new EventRepository(db);
        var builder = new CalendarModelBuilder(ev, new TodoRepository(db), db);
        var cell = Cell(builder.BuildMonth(2026, 10, TODAY), TODAY);
        var expected = new[] { Key(before[0]), Key(before[2]), Key(before[1]) };
        Assert.Equal(expected, cell.AllItems.Select(Key));
        Assert.Equal(expected.Take(2), cell.Chips.Select(Key));
        Assert.Equal(expected, builder.BuildWeek(TODAY, TODAY).Days[4].AllDay.Select(Key));
        var agenda = new AgendaService(ev, todos).GetAgenda(TODAY, TODAY);
        Assert.Equal(expected, agenda.Groups.Single(g => g.Date == TODAY).Events.Select(DayEventOrder.Key));
        ev.Delete(values[1].EventUid);
        var added = ev.Create([new("추가", "2026-10-30")]).Single();
        Assert.Equal(added.EventUid, Key(Cell(builder.BuildMonth(2026, 10, TODAY), TODAY).AllItems.Last()));
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
