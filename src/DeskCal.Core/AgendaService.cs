namespace DeskCal.Core;

public sealed record DdayItem(EventOccurrence Occurrence, int DaysRemaining);
public sealed record AgendaGroup(string Kind, DateOnly? Date, IReadOnlyList<EventOccurrence> Events, IReadOnlyList<TodoRecord> Todos);
public sealed record Agenda(DateOnly Today, IReadOnlyList<DdayItem> Ddays, IReadOnlyList<AgendaGroup> Groups);

public sealed class AgendaService(EventRepository events, TodoRepository todos, TimeZoneInfo? localTimeZone = null, Func<DateOnly>? today = null)
{
    private readonly TimeZoneInfo _zone = localTimeZone ?? TimeZoneInfo.Local;

    /// <param name="laterEventsUntil">주면 기간 다음 날부터 이 날짜까지의 일정도 "later" 묶음에 넣는다(사이드바, feedback 3-6).
    /// 주지 않으면 지금처럼 later에는 할 일만 있다(MCP get_agenda).</param>
    public Agenda GetAgenda(DateOnly? startDate = null, DateOnly? endDate = null, DateOnly? laterEventsUntil = null)
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
        // 여러 날 일정은 기간 중 날마다 넣지 않고 보이는 첫 날에만 넣는다. 그래야 할 일 하나가 날짜 수만큼 세어지지 않는다.
        var placed = new HashSet<string>();
        for (var day = start; day <= end; day = day.AddDays(1))
        {
            var lower = day.ToDateTime(TimeOnly.MinValue);
            var upper = lower.AddDays(1);
            groups.Add(new("date", day,
                occurrences.Where(o => o.Start < upper && o.End > lower && placed.Add(o.OccurrenceId ?? o.Event.EventUid)).ToArray(),
                visibleTodos.Where(t => t.IsDone ? day == current : t.DueDate == day.ToString("yyyy-MM-dd")).ToArray()));
        }
        // 기간에 이미 나온 여러 날 일정(placed)은 다시 넣지 않는다.
        var laterEvents = laterEventsUntil is { } until && until > end
            ? events.List(end.AddDays(1), until).Where(o => placed.Add(o.OccurrenceId ?? o.Event.EventUid)).OrderBy(o => o.Start).ToArray()
            : [];
        groups.Add(new("later", null, laterEvents, visibleTodos.Where(t => !t.IsDone && t.DueDate is not null && Validation.Date(t.DueDate) > end).ToArray()));
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
