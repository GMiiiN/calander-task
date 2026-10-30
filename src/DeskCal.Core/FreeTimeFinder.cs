namespace DeskCal.Core;

public sealed record FreeTimeSlot(DateTime Start, DateTime End);

public sealed class FreeTimeFinder(EventRepository events)
{
    public IReadOnlyList<FreeTimeSlot> Find(DateOnly startDate, DateOnly endDate, int durationMinutes, TimeOnly? dayStart = null, TimeOnly? dayEnd = null)
    {
        Validation.Range(startDate, endDate, 31);
        if (durationMinutes <= 0) throw new ArgumentException("Duration must be positive.");
        var startTime = dayStart ?? new TimeOnly(9, 0);
        var endTime = dayEnd ?? new TimeOnly(22, 0);
        if (endTime <= startTime) throw new ArgumentException("Day end must be after day start.");
        var busy = events.List(startDate, endDate).Where(o => !o.IsAllDay && o.Event.IsBusy).OrderBy(o => o.Start).ToArray();
        var result = new List<FreeTimeSlot>();
        for (var day = startDate; day <= endDate; day = day.AddDays(1))
        {
            var cursor = day.ToDateTime(startTime);
            var end = day.ToDateTime(endTime);
            foreach (var occurrence in busy.Where(o => o.Start < end && o.End > cursor))
            {
                var gapEnd = occurrence.Start < end ? occurrence.Start : end;
                if ((gapEnd - cursor).TotalMinutes >= durationMinutes) result.Add(new(cursor, gapEnd));
                if (occurrence.End > cursor) cursor = occurrence.End;
                if (cursor >= end) break;
            }
            if ((end - cursor).TotalMinutes >= durationMinutes) result.Add(new(cursor, end));
        }
        return result;
    }
}
