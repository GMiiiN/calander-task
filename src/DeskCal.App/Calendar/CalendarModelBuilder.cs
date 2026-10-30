using System.Globalization;
using DeskCal.Core;

namespace DeskCal.App.Calendar;

public sealed class CalendarModelBuilder(EventRepository events, TodoRepository todos, Database database)
{
    private readonly Dictionary<string, string> _colors = ReadColors(database);

    public MonthModel BuildMonth(int year, int month, DateOnly today)
    {
        var first = new DateOnly(year, month, 1);
        var start = first.AddDays(-(int)first.DayOfWeek);
        var dayCount = ((int)first.DayOfWeek + DateTime.DaysInMonth(year, month) + 6) / 7 * 7;
        var occurrences = events.List(start, start.AddDays(dayCount - 1));
        var dueTodos = todos.List("all");
        var weeks = new List<IReadOnlyList<DayCellModel>>();
        for (var offset = 0; offset < dayCount; offset += 7)
        {
            var week = new List<DayCellModel>();
            for (var day = 0; day < 7; day++)
            {
                var date = start.AddDays(offset + day);
                var items = ItemsForDay(date, occurrences, dueTodos);
                week.Add(new(date, date.Month == month && date.Year == year, date == today,
                    date.DayOfWeek, items.Take(2).ToArray(), Math.Max(0, items.Count - 2), items));
            }
            weeks.Add(week);
        }
        return new(year, month, weeks);
    }

    public WeekModel BuildWeek(DateOnly anyDay, DateOnly today)
    {
        var start = anyDay.AddDays(-(int)anyDay.DayOfWeek);
        var occurrences = events.List(start, start.AddDays(6));
        var dueTodos = todos.List("all");
        var days = new List<WeekDayModel>();
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
            days.Add(new(date, date == today,
                ItemsForDay(date, occurrences, dueTodos).Where(c => c.TimeText is null).ToArray(),
                AssignColumns(blocks)));
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
            var latestEnd = blocks[groupStart].EndMinute;
            var columnEnds = new List<int>();
            while (groupEnd < blocks.Length && (groupEnd == groupStart || blocks[groupEnd].StartMinute < latestEnd))
            {
                var block = blocks[groupEnd];
                var column = columnEnds.FindIndex(end => end <= block.StartMinute);
                if (column < 0)
                {
                    column = columnEnds.Count;
                    columnEnds.Add(block.EndMinute);
                }
                else columnEnds[column] = block.EndMinute;
                blocks[groupEnd] = block with { Column = column };
                latestEnd = Math.Max(latestEnd, block.EndMinute);
                groupEnd++;
            }
            for (var index = groupStart; index < groupEnd; index++)
                blocks[index] = blocks[index] with { ColumnCount = columnEnds.Count };
            groupStart = groupEnd;
        }
        return blocks;
    }

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

    private static DateTime Min(DateTime left, DateTime right) => left < right ? left : right;
    private static DateTime Max(DateTime left, DateTime right) => left > right ? left : right;
}
