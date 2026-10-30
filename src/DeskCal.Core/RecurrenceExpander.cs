using Ical.Net;
using Ical.Net.DataTypes;

namespace DeskCal.Core;

public sealed class RecurrenceExpander(TimeZoneInfo? localTimeZone = null)
{
    private readonly EventTime _time = new(localTimeZone);

    public IReadOnlyList<EventOccurrence> Expand(EventRecord parent, IEnumerable<EventRecord> exceptions, DateOnly start, DateOnly end, bool includeDeleted = false)
    {
        if (end < start) throw new ArgumentException("End date must not precede start date.");
        if (!includeDeleted && !Visible(parent)) return [];
        var lower = start.ToDateTime(TimeOnly.MinValue);
        var upper = end.AddDays(1).ToDateTime(TimeOnly.MinValue);
        if (parent.RecurrenceRule is null)
        {
            var single = _time.ToOccurrence(parent);
            return Overlaps(single, lower, upper) ? [single] : [];
        }
        var overrides = exceptions.ToDictionary(e => e.OriginalStartKey!);
        var original = _time.ToOccurrence(parent);
        // 원본 시간대와 표시 시간대의 날짜 차이 및 DST도 여유에 포함한다.
        var daysBack = Math.Max(0, (original.End.Date - original.Start.Date).Days) + 2;
        var from = lower.AddDays(-Math.Min(daysBack, start.DayNumber));
        var result = Originals(parent, from).TakeWhile(o => o.Start < upper)
            .Where(o => !overrides.ContainsKey(o.Event.OriginalStartKey!) && Overlaps(o, lower, upper)).ToList();
        result.AddRange(overrides.Values.Where(e => includeDeleted || Visible(e))
            .Select(e => _time.ToOccurrence(e with { EventUid = parent.EventUid }, OccurrenceId.Create(parent.EventUid, e.OriginalStartKey!)))
            .Where(o => Overlaps(o, lower, upper)));
        return result.OrderBy(o => o.Start).ThenBy(o => o.OccurrenceId).ToArray();
    }

    public EventOccurrence? Next(EventRecord parent, IEnumerable<EventRecord> exceptions, DateOnly today)
    {
        if (!Visible(parent)) return null;
        var lower = today.ToDateTime(TimeOnly.MinValue);
        if (parent.RecurrenceRule is null)
        {
            var single = _time.ToOccurrence(parent);
            return single.Start >= lower ? single : null;
        }
        var overrides = exceptions.ToDictionary(e => e.OriginalStartKey!);
        var nextOriginal = Originals(parent, lower).FirstOrDefault(o => o.Start >= lower && !overrides.ContainsKey(o.Event.OriginalStartKey!));
        var nextException = overrides.Values.Where(Visible)
            .Select(e => _time.ToOccurrence(e with { EventUid = parent.EventUid }, OccurrenceId.Create(parent.EventUid, e.OriginalStartKey!)))
            .Where(o => o.Start >= lower).OrderBy(o => o.Start).FirstOrDefault();
        if (nextOriginal is null) return nextException;
        return nextException is not null && nextException.Start < nextOriginal.Start ? nextException : nextOriginal;
    }

    public EventOccurrence? GetOriginal(EventRecord parent, string originalStartKey)
    {
        OccurrenceId.Create(parent.EventUid, originalStartKey);
        var target = originalStartKey.Length == 10
            ? Validation.Date(originalStartKey).ToDateTime(TimeOnly.MinValue)
            : _time.ToLocal(EventTime.ParseInstant(originalStartKey, "UTC"));
        return Originals(parent, target).TakeWhile(o => o.Start <= target)
            .FirstOrDefault(o => o.Event.OriginalStartKey == originalStartKey);
    }

    private IEnumerable<EventOccurrence> Originals(EventRecord parent, DateTime localFrom)
    {
        var allDay = parent.GoogleStartRaw!.Length == 10;
        var startLine = DateLine("DTSTART", parent.GoogleStartRaw!, parent.TimeZone);
        var endLine = DateLine("DTEND", parent.GoogleEndRaw!, parent.TimeZone);
        var calendar = Calendar.Load($"BEGIN:VCALENDAR\r\nVERSION:2.0\r\nBEGIN:VEVENT\r\nUID:{parent.EventUid}\r\n{startLine}\r\n{endLine}\r\n{parent.RecurrenceRule}\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n")!;
        var from = allDay ? new CalDateTime(localFrom.Date, false)
            : new CalDateTime(TimeZoneInfo.ConvertTimeToUtc(localFrom, _time.LocalTimeZone), "UTC");
        foreach (var occurrence in calendar.Events[0]!.GetOccurrences(from))
        {
            var start = occurrence.Period.StartTime;
            var end = occurrence.Period.EffectiveEndTime!;
            var key = allDay ? start.Value.ToString("yyyy-MM-dd") : start.AsUtc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
            var value = parent with
            {
                OriginalStartKey = key,
                GoogleStartRaw = allDay ? key : start.AsUtc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"),
                GoogleEndRaw = allDay ? end.Value.ToString("yyyy-MM-dd") : end.AsUtc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'")
            };
            yield return _time.ToOccurrence(value, OccurrenceId.Create(parent.EventUid, key));
        }
    }

    private static string DateLine(string name, string raw, string? timeZone)
    {
        if (raw.Length == 10) return $"{name};VALUE=DATE:{Validation.Date(raw):yyyyMMdd}";
        var instant = EventTime.ParseInstant(raw, timeZone);
        if (timeZone is null) return $"{name}:{instant.UtcDateTime:yyyyMMdd'T'HHmmss'Z'}";
        var wall = TimeZoneInfo.ConvertTime(instant, TimeZoneInfo.FindSystemTimeZoneById(timeZone));
        return $"{name};TZID={timeZone}:{wall:yyyyMMdd'T'HHmmss}";
    }

    private static bool Visible(EventRecord value) => value.DeletedAt is null && value.Status != "cancelled";
    private static bool Overlaps(EventOccurrence value, DateTime lower, DateTime upper) => value.Start < upper && value.End > lower;
}
