using System.IO;
using DeskCal.App.Calendar;
using DeskCal.Core;

namespace DeskCal.App.Tests;

// +N 목록에서 끌어 순서 바꾸기(오너 요청, v0.14.10). 받는 규칙, 놓을 자리 선, 놓은 뒤 같은 날짜의 새 목록·칸을 본다.
// 오너 화면과 같은 꼴이다: 위에 기간 막대, 그 아래 단일 일정 다섯, 완료한 단일 하나, 그날 마감 할 일 하나.
public sealed class OverflowReorderTests : IDisposable
{
    private static readonly DateOnly Day = new(2026, 11, 25);
    private readonly string databasePath = Path.Combine(Path.GetTempPath(), "deskcal-overflow-" + Guid.NewGuid().ToString("N") + ".db");
    private readonly Database database;
    private readonly EventRepository events;
    private readonly TodoRepository todos;
    private readonly Dictionary<string, string> uids = [];

    public OverflowReorderTests()
    {
        database = new(databasePath);
        database.Initialize();
        events = new(database, TimeZoneInfo.FindSystemTimeZoneById("Asia/Seoul"));
        todos = new(database);
        Create("기간", new("기간", "2026-11-19") { EndDate = "2026-11-25" });
        foreach (var title in new[] { "A", "B", "C", "D", "E", "F" }) Create(title, new(title, "2026-11-25"));
        events.Update(uids["F"], new() { IsDone = true });
        todos.Create([new TodoInput("할 일", "2026-11-25")]);
    }

    // 생성순이 기본 순서다. 같은 시각이면 무작위 UID 순이 되므로 시각을 정해 둔다.
    private void Create(string title, EventInput input)
    {
        var uid = events.Create([input]).Single().EventUid;
        uids[title] = uid;
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE event SET created_at=$at WHERE event_uid=$uid";
        command.Parameters.AddWithValue("$at", $"2026-11-01T00:00:{uids.Count:00}.0000000+00:00");
        command.Parameters.AddWithValue("$uid", uid);
        command.ExecuteNonQuery();
    }

    private MonthModel Month() => new CalendarModelBuilder(events, todos, database).BuildMonth(2026, 11, Day);
    private DayCellModel Cell() => Month().CellOn(Day)!;
    private static string[] Titles(IEnumerable<ChipModel> chips) => chips.Select(chip => chip.Title).ToArray();
    private static DayOrderItem? Item(DayCellModel cell, string title) => DayOrderItem.From(cell.AllItems.Single(chip => chip.Title == title));

    [Fact]
    public void OnlySingleEventsOfTheDayMoveAndOnlyWithinTheirCompletionGroup()
    {
        var cell = Cell();
        Assert.Equal(new[] { "기간", "A", "B", "C", "D", "E", "F", "할 일" }, Titles(cell.AllItems));
        Assert.Null(Item(cell, "기간")); // 여러 날 일정은 위에 고정이라 끌 수 없다
        Assert.Null(Item(cell, "할 일")); // 할 일은 일정 순서에 들지 않는다
        var a = Item(cell, "A")!;
        Assert.Equal(new DayOrderItem(Day, uids["A"], false), a);
        Assert.True(a.Accepts(Item(cell, "C")));
        Assert.False(a.Accepts(a));
        Assert.False(a.Accepts(null)); // 기간·할 일 위에 놓으면 대상이 없다
        Assert.False(a.Accepts(Item(cell, "F"))); // 완료한 일정은 늘 미완료 아래다
        Assert.False(Item(cell, "F")!.Accepts(a));
        Assert.False(a.Accepts(Item(cell, "C")! with { Day = Day.AddDays(1) }));
        // 우측 목록 행도 같은 규칙으로 읽는다.
        Assert.Equal(a, DayOrderItem.From(new SidebarItemViewModel("A", "", false, null, null, uids["A"], null) { OrderDate = Day }));
        Assert.Null(DayOrderItem.From(new SidebarItemViewModel("기간", "", false, null, null, uids["기간"], null)));
    }

    // 놓기 전에 긋는 선의 자리(LandsBelow)와 DayEventOrder.Move가 실제로 넣는 자리가 같아야 한다.
    [Fact]
    public void DropLineMarksTheSlotTheMoveLandsIn()
    {
        var cell = Cell();
        var a = Item(cell, "A")!;
        var c = Item(cell, "C")!;
        Assert.True(a.LandsBelow(cell.AllItems, c)); // 아래로 옮기면 대상 다음
        Assert.True(DayEventOrder.Move(events, Day, a.Key, c.Key));

        cell = Cell();
        Assert.Equal(new[] { "기간", "B", "C", "A", "D", "E", "F", "할 일" }, Titles(cell.AllItems));
        var e = Item(cell, "E")!;
        var b = Item(cell, "B")!;
        Assert.False(e.LandsBelow(cell.AllItems, b)); // 위로 옮기면 대상 앞
        Assert.True(DayEventOrder.Move(events, Day, e.Key, b.Key));
        Assert.Equal(new[] { "기간", "E", "B", "C", "A", "D", "F", "할 일" }, Titles(Cell().AllItems));

        // 목록에 없는 일정(다른 날 칸에서 끌어온 경우 등)은 받지 않으므로 선 방향은 기본값(앞)이다.
        Assert.False(new DayOrderItem(Day, "missing", false).LandsBelow(cell.AllItems, b));
    }

    // 놓은 뒤 열린 목록은 다시 그린 달력에서 같은 날짜 칸을 찾아 바꾼다. 칸의 칩 두 개도 같은 순서다.
    [Fact]
    public void ReopenedCellAndVisibleChipsFollowTheNewOrder()
    {
        string?[] Keys(IEnumerable<ChipModel> chips) => chips.Select(chip => chip.OccurrenceId ?? chip.EventId).ToArray();
        var before = Month();
        Assert.Equal(new string?[] { uids["기간"], uids["A"] }, Keys(before.CellOn(Day)!.Chips));
        Assert.Equal(6, before.CellOn(Day)!.OverflowCount);
        Assert.Null(before.CellOn(new DateOnly(2027, 1, 1)));

        Assert.True(DayEventOrder.Move(events, Day, uids["A"], uids["C"]));
        Assert.True(DayEventOrder.Move(events, Day, uids["E"], uids["B"]));
        var after = Month().CellOn(Day)!;
        Assert.Equal(new string?[] { uids["기간"], uids["E"] }, Keys(after.Chips));
        Assert.Equal(new[] { "기간", "E", "B", "C", "A", "D", "F", "할 일" }, Titles(after.AllItems));
        Assert.Equal(before.CellOn(Day)!.AllItems.Count, after.AllItems.Count);
    }

    public void Dispose()
    {
        foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(databasePath + suffix);
    }
}
