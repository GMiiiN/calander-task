using System.Globalization;
using DeskCal.Core;

namespace DeskCal.App.Calendar;

public sealed class CalendarModelBuilder(EventRepository events, TodoRepository todos, Database database, bool detailedBars = false)
{
    // 주간 시간표에서 짧은 일정도 이 길이만큼은 그려진다. 겹침 판단도 이 길이로 해야 화면에서 서로 가리지 않는다.
    public const int MIN_BLOCK_MINUTES = 30;

    private DayEventOrder _order = new();
    private readonly Dictionary<string, string> _colors = ReadColors(database);

    public MonthModel BuildMonth(int year, int month, DateOnly today)
    {
        var first = new DateOnly(year, month, 1);
        var start = WeekStartOf(first);
        var dayCount = (DaysFromMonday(first) + DateTime.DaysInMonth(year, month) + 6) / 7 * 7;
        _order = DayEventOrder.Read(database, start, start.AddDays(dayCount - 1));
        var occurrences = events.List(start, start.AddDays(dayCount - 1));
        var dueTodos = todos.List("all");
        var holidays = new HolidayRepository(database).List(start, start.AddDays(dayCount - 1));
        var weeks = new List<IReadOnlyList<DayCellModel>>();
        for (var offset = 0; offset < dayCount; offset += 7)
        {
            var week = new List<DayCellModel>();
            var row = LayOutRow(start.AddDays(offset), occurrences, dueTodos);
            for (var day = 0; day < 7; day++)
            {
                var date = start.AddDays(offset + day);
                var items = ItemsForDay(date, occurrences, dueTodos);
                var chips = VisibleChips(row[day].Bars, row[day].Singles);
                week.Add(new(date, date.Month == month && date.Year == year, date == today,
                    date.DayOfWeek, chips, items.Count - chips.Count(c => !c.IsPlaceholder), items, holidays.GetValueOrDefault(date)));
            }
            weeks.Add(week);
        }
        return new(year, month, weeks);
    }

    public WeekModel BuildWeek(DateOnly anyDay, DateOnly today, DayOfWeek firstDay = DayOfWeek.Monday)
    {
        var start = WeekStartOf(anyDay, firstDay);
        _order = DayEventOrder.Read(database, start, start.AddDays(6));
        var occurrences = events.List(start, start.AddDays(6));
        var dueTodos = todos.List("all");
        var holidays = new HolidayRepository(database).List(start, start.AddDays(6));
        var days = new List<WeekDayModel>();
        // 종일 줄에는 종일 일정, 하루(24시간) 이상 이어지는 시간 일정, 할 일을 둔다. 나머지 시간 일정은 시간표에 그린다.
        // 금 18시~일 12시 같은 일정을 시간표에 그리면 가운데 날 열 전체를 덮어 다른 일정을 가린다.
        var allDay = LayOutRow(start, occurrences.Where(IsWeekBar).ToArray(), dueTodos)
            .Select(d => (IReadOnlyList<ChipModel>)d.Bars.Select(c => c ?? ChipModel.Placeholder).Concat(d.Singles).ToArray()).ToArray();
        for (var offset = 0; offset < 7; offset++)
        {
            var date = start.AddDays(offset);
            var lower = date.ToDateTime(TimeOnly.MinValue);
            var upper = lower.AddDays(1);
            var blocks = occurrences.Where(o => !IsWeekBar(o) && o.Start < upper && o.End > lower)
                .Select(o => new WeekBlockModel(ToChip(o) with { DetailText = DetailText(o) },
                    (int)(Max(o.Start, lower) - lower).TotalMinutes,
                    (int)(Min(o.End, upper) - lower).TotalMinutes, 0, 1))
                .Where(b => b.EndMinute > b.StartMinute)
                .OrderBy(b => b.StartMinute).ThenBy(b => b.EndMinute).ToArray();
            days.Add(new(date, date == today, allDay[offset], AssignColumns(blocks), holidays.GetValueOrDefault(date)));
        }
        return new(start, days);
    }

    private bool IsWeekBar(EventOccurrence occurrence) =>
        occurrence.IsAllDay || (detailedBars && LastDate(occurrence) > FirstDate(occurrence)) || occurrence.End - occurrence.Start >= TimeSpan.FromHours(24);

    private IReadOnlyList<ChipModel> ItemsForDay(DateOnly date, IReadOnlyList<EventOccurrence> occurrences, IReadOnlyList<TodoRecord> dueTodos)
    {
        var lower = date.ToDateTime(TimeOnly.MinValue);
        var upper = lower.AddDays(1);
        return _order.Sort(date, occurrences.Where(o => o.Start < upper && o.End > lower)).Select(o => ToChip(o) with { DetailText = DetailText(o) })
            .Concat(dueTodos.Where(t => t.DueDate == date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
                .OrderBy(t => t.IsDone).Select(t => new ChipModel(t.Title, null, "#B0BACB", t.IsDone, true, false, null, null, t.TodoUid, t.CreatedBy) { DetailText = "할 일 · 이날 마감" }))
            .ToArray();
    }

    // 한 줄(월~일)의 칸별 막대 레인과 하루짜리 항목. 여러 날 일정은 줄 안에서 한 레인을 지켜 막대로 이어진다.
    // 막대는 시작일순(같은 날 시작하면 긴 것 먼저)으로 그 기간이 모두 비어 있는 가장 위 레인에 넣는다(v0.8.2와 같다).
    // 레인 사이의 빈 자리는 null로 남기고, 하루짜리 항목은 그날의 마지막 막대 아래에 둔다.
    private (IReadOnlyList<ChipModel?> Bars, IReadOnlyList<ChipModel> Singles)[] LayOutRow(DateOnly rowStart,
        IReadOnlyList<EventOccurrence> occurrences, IReadOnlyList<TodoRecord> dueTodos)
    {
        var rowEnd = rowStart.AddDays(6);
        var spans = _order.Sort(rowStart, occurrences.Where(o => LastDate(o) > FirstDate(o) && FirstDate(o) <= rowEnd && LastDate(o) >= rowStart)).ToArray();
        var lanes = new List<ChipModel?[]>();
        foreach (var occurrence in spans)
        {
            var from = Math.Max(0, FirstDate(occurrence).DayNumber - rowStart.DayNumber);
            var to = Math.Min(6, LastDate(occurrence).DayNumber - rowStart.DayNumber);
            var lane = lanes.FindIndex(l => l.Skip(from).Take(to - from + 1).All(c => c is null));
            if (lane < 0)
            {
                lane = lanes.Count;
                lanes.Add(new ChipModel?[7]);
            }
            for (var day = from; day <= to; day++) lanes[lane][day] = BarChip(occurrence, rowStart.AddDays(day), rowStart);
        }
        var singles = occurrences.Where(o => !spans.Contains(o)).ToArray();
        var result = new (IReadOnlyList<ChipModel?>, IReadOnlyList<ChipModel>)[7];
        for (var day = 0; day < 7; day++)
        {
            var bars = lanes.Select(l => l[day]).ToList();
            while (bars.Count > 0 && bars[^1] is null) bars.RemoveAt(bars.Count - 1);
            result[day] = (bars, ItemsForDay(rowStart.AddDays(day), singles, dueTodos));
        }
        return result;
    }

    // 월간 칸에는 두 줄만 보인다. 막대는 제 레인(옆 날과 같은 줄)을 지키고 하루짜리 항목은 막대 아래에 둔다.
    // 막대 사이의 빈 레인 때문에 하루짜리 항목이 두 줄 밖으로 밀릴 때만 그 빈 자리로 올린다. 막대는 압축하지 않는다.
    private const int VISIBLE_CHIPS = 2;

    private static IReadOnlyList<ChipModel> VisibleChips(IReadOnlyList<ChipModel?> bars, IReadOnlyList<ChipModel> singles)
    {
        var visible = bars.Take(VISIBLE_CHIPS).ToList();
        var next = 0;
        while (visible.Count < VISIBLE_CHIPS && next < singles.Count) visible.Add(singles[next++]);
        for (var slot = 0; slot < visible.Count && next < singles.Count; slot++)
            if (visible[slot] is null) visible[slot] = singles[next++];
        while (visible.Count > 0 && visible[^1] is null) visible.RemoveAt(visible.Count - 1);
        return visible.Select(c => c ?? ChipModel.Placeholder).ToArray();
    }

    // 이름은 시작일에 쓴다. 줄의 첫날(월요일)에는 일정이 그 주를 넘어 계속되거나 14일 이상일 때만 다시 쓰고,
    // 마지막 날의 "이름 - 마감"은 시작한 주와 다른 주에서 끝날 때만 쓴다. 둘이 겹치면 마감 표시만.
    private const int LONG_EVENT_DAYS = 14;

    private ChipModel BarChip(EventOccurrence occurrence, DateOnly date, DateOnly rowStart)
    {
        var chip = ToChip(occurrence);
        var first = FirstDate(occurrence);
        var last = LastDate(occurrence);
        var showEnd = date == last && (detailedBars || first < rowStart);
        var showRowStart = date == rowStart && (detailedBars || last.DayNumber - first.DayNumber + 1 >= LONG_EVENT_DAYS || last > date.AddDays(6));
        var title = showEnd ? $"{chip.Title} - 마감" : date == first || showRowStart ? chip.Title : "";
        return chip with
        {
            Title = title, TimeText = date == first ? chip.TimeText : null,
            JoinsLeft = date > first, JoinsRight = date < last, PeriodText = PeriodText(occurrence)
        };
    }

    private static string PeriodText(EventOccurrence occurrence)
    {
        var format = occurrence.IsAllDay ? "M월 d일" : "M월 d일 tt h:mm";
        var end = occurrence.IsAllDay ? LastDate(occurrence).ToDateTime(TimeOnly.MinValue) : occurrence.End;
        return $"{occurrence.Start.ToString(format, KOREAN)} - {end.ToString(format, KOREAN)}";
    }

    private static string DetailText(EventOccurrence occurrence) =>
        LastDate(occurrence) > FirstDate(occurrence) ? PeriodText(occurrence)
        : occurrence.IsAllDay ? "종일"
        : $"{occurrence.Start.ToString("HH:mm", CultureInfo.InvariantCulture)}–{occurrence.End.ToString("HH:mm", CultureInfo.InvariantCulture)}";

    private static readonly CultureInfo KOREAN = CultureInfo.GetCultureInfo("ko-KR");
    private static DateOnly FirstDate(EventOccurrence occurrence) => DateOnly.FromDateTime(occurrence.Start);
    // 끝 시각은 그 시각을 포함하지 않는다. 자정에 끝나면 앞날이 마지막 날이다.
    private static DateOnly LastDate(EventOccurrence occurrence) =>
        DateOnly.FromDateTime(occurrence.End > occurrence.Start ? occurrence.End.AddTicks(-1) : occurrence.Start);

    private ChipModel ToChip(EventOccurrence occurrence)
    {
        var value = occurrence.Event;
        return new(value.Title, occurrence.IsAllDay ? null : occurrence.Start.ToString("HH:mm", CultureInfo.InvariantCulture),
            value.ColorId is not null && _colors.TryGetValue(value.ColorId, out var color) ? color : "#246BDE",
            value.IsDone, false, value.IsDday, value.EventUid, occurrence.OccurrenceId, null, value.CreatedBy)
        { OrderDate = DayEventOrder.IsPeriod(occurrence) ? null : FirstDate(occurrence) };
    }

    private static IReadOnlyList<WeekBlockModel> AssignColumns(WeekBlockModel[] blocks)
    {
        var groupStart = 0;
        while (groupStart < blocks.Length)
        {
            var groupEnd = groupStart;
            var latestEnd = ShownEnd(blocks[groupStart]);
            var columnEnds = new List<int>();
            while (groupEnd < blocks.Length && (groupEnd == groupStart || blocks[groupEnd].StartMinute < latestEnd))
            {
                var block = blocks[groupEnd];
                var column = columnEnds.FindIndex(end => end <= block.StartMinute);
                if (column < 0)
                {
                    column = columnEnds.Count;
                    columnEnds.Add(ShownEnd(block));
                }
                else columnEnds[column] = ShownEnd(block);
                blocks[groupEnd] = block with { Column = column };
                latestEnd = Math.Max(latestEnd, ShownEnd(block));
                groupEnd++;
            }
            for (var index = groupStart; index < groupEnd; index++)
                blocks[index] = blocks[index] with { ColumnCount = columnEnds.Count };
            groupStart = groupEnd;
        }
        return blocks;
    }

    private static int ShownEnd(WeekBlockModel block) => Math.Max(block.EndMinute, block.StartMinute + MIN_BLOCK_MINUTES);

    private static Dictionary<string, string> ReadColors(Database database)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT color_id, color_hex FROM category;";
        using var reader = command.ExecuteReader();
        var result = new Dictionary<string, string>();
        while (reader.Read()) result.Add(reader.GetString(0), reader.GetString(1));
        return result;
    }

    // 주는 월요일에 시작한다(월~일).
    private static int DaysFromMonday(DateOnly date) => ((int)date.DayOfWeek + 6) % 7;
    private static DateOnly WeekStartOf(DateOnly date, DayOfWeek firstDay = DayOfWeek.Monday) =>
        date.AddDays(-((7 + (int)date.DayOfWeek - (int)firstDay) % 7));

    private static DateTime Min(DateTime left, DateTime right) => left < right ? left : right;
    private static DateTime Max(DateTime left, DateTime right) => left > right ? left : right;
}
