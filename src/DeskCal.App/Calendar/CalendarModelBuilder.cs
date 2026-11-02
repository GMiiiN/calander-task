using System.Globalization;
using DeskCal.Core;

namespace DeskCal.App.Calendar;

public sealed class CalendarModelBuilder(EventRepository events, TodoRepository todos, Database database)
{
    // 주간 시간표에서 짧은 일정도 이 길이만큼은 그려진다. 겹침 판단도 이 길이로 해야 화면에서 서로 가리지 않는다.
    public const int MIN_BLOCK_MINUTES = 30;

    private readonly Dictionary<string, string> _colors = ReadColors(database);

    public MonthModel BuildMonth(int year, int month, DateOnly today)
    {
        var first = new DateOnly(year, month, 1);
        var start = WeekStartOf(first);
        var dayCount = (DaysFromMonday(first) + DateTime.DaysInMonth(year, month) + 6) / 7 * 7;
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
                var chips = row[day].Take(2).ToList();
                while (chips.Count > 0 && chips[^1].IsPlaceholder) chips.RemoveAt(chips.Count - 1);
                week.Add(new(date, date.Month == month && date.Year == year, date == today,
                    date.DayOfWeek, chips, items.Count - chips.Count(c => !c.IsPlaceholder), items, holidays.GetValueOrDefault(date)));
            }
            weeks.Add(week);
        }
        return new(year, month, weeks);
    }

    public WeekModel BuildWeek(DateOnly anyDay, DateOnly today)
    {
        var start = WeekStartOf(anyDay);
        var occurrences = events.List(start, start.AddDays(6));
        var dueTodos = todos.List("all");
        var holidays = new HolidayRepository(database).List(start, start.AddDays(6));
        var days = new List<WeekDayModel>();
        // 종일 줄에는 종일 일정과 할 일만 둔다. 시간 일정은 시간표에 그린다.
        var allDay = LayOutRow(start, occurrences.Where(o => o.IsAllDay).ToArray(), dueTodos);
        for (var offset = 0; offset < 7; offset++)
        {
            var date = start.AddDays(offset);
            var lower = date.ToDateTime(TimeOnly.MinValue);
            var upper = lower.AddDays(1);
            var blocks = occurrences.Where(o => !o.IsAllDay && o.Start < upper && o.End > lower)
                .Select(o => new WeekBlockModel(ToChip(o),
                    (int)(Max(o.Start, lower) - lower).TotalMinutes,
                    (int)(Min(o.End, upper) - lower).TotalMinutes, 0, 1))
                .Where(b => b.EndMinute > b.StartMinute)
                .OrderBy(b => b.StartMinute).ThenBy(b => b.EndMinute).ToArray();
            days.Add(new(date, date == today, allDay[offset], AssignColumns(blocks), holidays.GetValueOrDefault(date)));
        }
        return new(start, days);
    }

    private IReadOnlyList<ChipModel> ItemsForDay(DateOnly date, IReadOnlyList<EventOccurrence> occurrences, IReadOnlyList<TodoRecord> dueTodos)
    {
        var lower = date.ToDateTime(TimeOnly.MinValue);
        var upper = lower.AddDays(1);
        return occurrences.Where(o => o.Start < upper && o.End > lower).Select(ToChip)
            .Concat(dueTodos.Where(t => t.DueDate == date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
                .Select(t => new ChipModel(t.Title, null, "#B0BACB", t.IsDone, true, false, null, null, t.TodoUid, t.CreatedBy)))
            .OrderBy(c => c.IsTodo ? 2 : c.TimeText is null ? 0 : 1)
            .ThenBy(c => c.IsDone).ThenBy(c => c.TimeText, StringComparer.Ordinal).ToArray();
    }

    // 한 줄(월~일)의 칸별 칩. 여러 날 일정은 줄 안에서 같은 높이(lane)를 지켜 막대로 이어지고,
    // 하루짜리 항목은 남은 빈 자리를 위에서부터 채운다. 막대 사이의 빈 자리는 자리 표시로 남긴다.
    private IReadOnlyList<ChipModel>[] LayOutRow(DateOnly rowStart, IReadOnlyList<EventOccurrence> occurrences, IReadOnlyList<TodoRecord> dueTodos)
    {
        var rowEnd = rowStart.AddDays(6);
        var spans = occurrences.Where(o => LastDate(o) > FirstDate(o) && FirstDate(o) <= rowEnd && LastDate(o) >= rowStart)
            .OrderBy(FirstDate).ThenByDescending(LastDate).ToArray();
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
            for (var day = from; day <= to; day++) lanes[lane][day] = BarChip(occurrence, rowStart.AddDays(day), day == 0);
        }
        var singles = occurrences.Where(o => !spans.Contains(o)).ToArray();
        var result = new IReadOnlyList<ChipModel>[7];
        for (var day = 0; day < 7; day++)
        {
            var slots = lanes.Select(l => l[day]).ToList();
            foreach (var chip in ItemsForDay(rowStart.AddDays(day), singles, dueTodos))
            {
                var free = slots.IndexOf(null);
                if (free < 0) slots.Add(chip);
                else slots[free] = chip;
            }
            while (slots.Count > 0 && slots[^1] is null) slots.RemoveAt(slots.Count - 1);
            result[day] = slots.Select(c => c ?? ChipModel.Placeholder).ToArray();
        }
        return result;
    }

    // 이름은 시작일, 줄의 첫날(월요일), 마지막 날("이름 - 마감")에만 쓴다. 마지막 날이 겹치면 마감 표시만.
    private ChipModel BarChip(EventOccurrence occurrence, DateOnly date, bool isRowStart)
    {
        var chip = ToChip(occurrence);
        var first = FirstDate(occurrence);
        var last = LastDate(occurrence);
        var title = date == last ? $"{chip.Title} - 마감" : date == first || isRowStart ? chip.Title : "";
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
            value.IsDone, false, value.IsDday, value.EventUid, occurrence.OccurrenceId, null, value.CreatedBy);
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
    private static DateOnly WeekStartOf(DateOnly date) => date.AddDays(-DaysFromMonday(date));

    private static DateTime Min(DateTime left, DateTime right) => left < right ? left : right;
    private static DateTime Max(DateTime left, DateTime right) => left > right ? left : right;
}
