namespace DeskCal.Core;

public sealed record DdayItem(EventOccurrence Occurrence, int DaysRemaining);
public sealed record AgendaGroup(string Kind, DateOnly? Date, IReadOnlyList<EventOccurrence> Events, IReadOnlyList<TodoRecord> Todos);
public sealed record Agenda(DateOnly Today, IReadOnlyList<DdayItem> Ddays, IReadOnlyList<AgendaGroup> Groups);

public sealed class AgendaService(EventRepository events, TodoRepository todos, TimeZoneInfo? localTimeZone = null, Func<DateOnly>? today = null)
{
    private readonly TimeZoneInfo _zone = localTimeZone ?? TimeZoneInfo.Local;

    public Agenda GetAgenda(DateOnly? startDate = null, DateOnly? endDate = null)
    {
        var current = today?.Invoke() ?? DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, _zone).DateTime);
        var start = startDate ?? current;
        var end = endDate ?? start.AddDays(13);
        Validation.Range(start, end, 366);
        var occurrences = events.List(start, end);
        var visibleTodos = todos.List("all").Where(t => !t.IsDone || CompletedToday(t, current)).ToArray();
        var groups = new List<AgendaGroup>
        {
            new("overdue", null, [], visibleTodos.Where(t => !t.IsDone && t.DueDate is not null && Validation.Date(t.DueDate) < start).ToArray())
        };
        for (var day = start; day <= end; day = day.AddDays(1))
        {
            var lower = day.ToDateTime(TimeOnly.MinValue);
            var upper = lower.AddDays(1);
            groups.Add(new("date", day,
                occurrences.Where(o => o.Start < upper && o.End > lower).ToArray(),
                visibleTodos.Where(t => t.IsDone ? day == current : t.DueDate == day.ToString("yyyy-MM-dd")).ToArray()));
        }
        groups.Add(new("later", null, [], visibleTodos.Where(t => !t.IsDone && t.DueDate is not null && Validation.Date(t.DueDate) > end).ToArray()));
        groups.Add(new("anytime", null, [], visibleTodos.Where(t => !t.IsDone && t.DueDate is null).ToArray()));
        var records = events.GetRecords();
        var expander = new RecurrenceExpander(_zone);
        var ddays = records.Where(e => e.ParentEventId is null && e.GoogleRecurringId is null && e.IsDday)
            .Select(e => expander.Next(e, records.Where(ex => ex.ParentEventId == e.EventId), current))
            .OfType<EventOccurrence>().Select(o => new DdayItem(o, DateOnly.FromDateTime(o.Start).DayNumber - current.DayNumber))
            .OrderBy(d => d.DaysRemaining).ThenBy(d => d.Occurrence.Event.Title).ToArray();
        return new(current, ddays, groups);
    }

    private bool CompletedToday(TodoRecord todo, DateOnly today) => todo.CompletedAt is not null &&
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.Parse(todo.CompletedAt), _zone).DateTime) == today;
}
