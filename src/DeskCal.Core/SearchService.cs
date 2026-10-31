namespace DeskCal.Core;

public sealed record SearchHit(string Kind, string Uid, string Title, DateOnly? Date, string? StartTime, string? ColorId, bool IsDone);

public sealed class SearchService(EventRepository events, TodoRepository todos, TimeZoneInfo? localTimeZone = null, Func<DateOnly>? today = null)
{
    private readonly TimeZoneInfo _zone = localTimeZone ?? TimeZoneInfo.Local;

    public IReadOnlyList<SearchHit> Search(string query, int limit = 50)
    {
        query = query.Trim();
        if (query.Length == 0 || limit <= 0) return [];
        var current = today?.Invoke() ?? DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, _zone).DateTime);
        var records = events.GetRecords();
        var expander = new RecurrenceExpander(_zone);
        var hits = new List<SearchHit>();
        foreach (var value in records.Where(e => e.DeletedAt is null && e.Status != "cancelled" &&
                     e.ParentEventId is null && e.GoogleRecurringId is null && Matches(e.Title, e.Memo)))
        {
            var date = Validation.Date(value.StartDate);
            if (value.RecurrenceRule is not null)
            {
                var next = expander.Next(value, records.Where(e => e.ParentEventId == value.EventId), current);
                if (next is not null) date = DateOnly.FromDateTime(next.Start);
            }
            hits.Add(new("event", value.EventUid, value.Title, date, value.StartTime, value.ColorId, value.IsDone));
        }
        hits.AddRange(todos.List("all").Where(t => Matches(t.Title, t.Memo))
            .Select(t => new SearchHit("todo", t.TodoUid, t.Title,
                t.DueDate is null ? null : Validation.Date(t.DueDate), null, null, t.IsDone)));
        return hits.OrderBy(hit => hit.Date is null ? 1 : hit.Date >= current ? 0 : 2)
            .ThenBy(hit => hit.Date is null ? 0 : hit.Date >= current ? hit.Date.Value.DayNumber : -hit.Date.Value.DayNumber)
            .ThenBy(hit => hit.StartTime, StringComparer.Ordinal)
            .ThenBy(hit => hit.Title, StringComparer.OrdinalIgnoreCase)
            .Take(limit).ToArray();

        bool Matches(string title, string memo) => title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                                                  memo.Contains(query, StringComparison.OrdinalIgnoreCase);
    }
}
