using System.Globalization;

namespace DeskCal.Core;

public sealed class EventTime(TimeZoneInfo? localTimeZone = null)
{
    public TimeZoneInfo LocalTimeZone { get; } = localTimeZone ?? TimeZoneInfo.Local;
    public string IanaId => TimeZoneInfo.TryConvertWindowsIdToIanaId(LocalTimeZone.Id, out var iana) ? iana : LocalTimeZone.Id;

    public EventRecord Normalize(EventRecord value)
    {
        var startDate = Validation.Date(value.StartDate);
        var endDate = Validation.Date(value.EndDate);
        if (endDate < startDate) throw new ArgumentException("End date must not precede start date.");
        if (value.StartTime is null)
        {
            if (value.EndTime is not null) throw new ArgumentException("End time requires start time.");
            return value with { GoogleStartRaw = value.StartDate, GoogleEndRaw = endDate.AddDays(1).ToString("yyyy-MM-dd"), TimeZone = null };
        }
        var start = startDate.ToDateTime(Validation.Time(value.StartTime));
        if (value.EndTime is null && endDate > startDate)
            throw new ArgumentException("여러 날에 걸친 시간 일정은 end_time이 필요합니다.");
        var end = value.EndTime is null ? start.AddHours(1) : endDate.ToDateTime(Validation.Time(value.EndTime));
        if (end <= start) throw new ArgumentException("End must be after start.");
        if (LocalTimeZone.IsInvalidTime(start) || LocalTimeZone.IsInvalidTime(end))
            throw new ArgumentException("The local time does not exist because of daylight saving time.");
        return value with
        {
            EndDate = end.ToString("yyyy-MM-dd"), EndTime = end.ToString("HH:mm"),
            GoogleStartRaw = new DateTimeOffset(start, LocalTimeZone.GetUtcOffset(start)).ToString("yyyy-MM-dd'T'HH:mm:sszzz"),
            GoogleEndRaw = new DateTimeOffset(end, LocalTimeZone.GetUtcOffset(end)).ToString("yyyy-MM-dd'T'HH:mm:sszzz"),
            TimeZone = IanaId
        };
    }

    public EventRecord Derive(EventRecord value)
    {
        var startRaw = value.GoogleStartRaw ?? throw new ArgumentException("Start source is required.");
        var endRaw = value.GoogleEndRaw ?? throw new ArgumentException("End source is required.");
        if (startRaw.Length == 10)
            return value with { StartDate = Validation.Date(startRaw).ToString("yyyy-MM-dd"), EndDate = Validation.Date(endRaw).AddDays(-1).ToString("yyyy-MM-dd"), StartTime = null, EndTime = null };
        var start = ToLocal(ParseInstant(startRaw, value.TimeZone));
        var end = ToLocal(ParseInstant(endRaw, value.TimeZone));
        return value with { StartDate = start.ToString("yyyy-MM-dd"), EndDate = end.ToString("yyyy-MM-dd"), StartTime = start.ToString("HH:mm"), EndTime = end.ToString("HH:mm") };
    }

    public EventOccurrence ToOccurrence(EventRecord value, string? occurrenceId = null)
    {
        var derived = Derive(value);
        if (derived.StartTime is null)
            return new(derived, occurrenceId, Validation.Date(derived.StartDate).ToDateTime(TimeOnly.MinValue), Validation.Date(derived.EndDate).AddDays(1).ToDateTime(TimeOnly.MinValue));
        return new(derived, occurrenceId, ToLocal(ParseInstant(value.GoogleStartRaw!, value.TimeZone)), ToLocal(ParseInstant(value.GoogleEndRaw!, value.TimeZone)));
    }

    public DateTime ToLocal(DateTimeOffset instant) => TimeZoneInfo.ConvertTime(instant, LocalTimeZone).DateTime;

    public static DateTimeOffset ParseInstant(string raw, string? timeZone)
    {
        if (raw.EndsWith('Z') || raw.LastIndexOf('+') > 10 || raw.LastIndexOf('-') > 10)
            return DateTimeOffset.Parse(raw, CultureInfo.InvariantCulture);
        var wallTime = DateTime.SpecifyKind(DateTime.Parse(raw, CultureInfo.InvariantCulture), DateTimeKind.Unspecified);
        var zone = TimeZoneInfo.FindSystemTimeZoneById(timeZone ?? "UTC");
        return new(wallTime, zone.GetUtcOffset(wallTime));
    }
}

internal static class Validation
{
    public static DateOnly Date(string value) => DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var result)
        ? result : throw new ArgumentException("Date must be a real YYYY-MM-DD date.");
    public static TimeOnly Time(string value) => TimeOnly.TryParseExact(value, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var result)
        ? result : throw new ArgumentException("Time must be HH:mm between 00:00 and 23:59.");
    public static string Title(string? value) => !string.IsNullOrWhiteSpace(value) ? value : throw new ArgumentException("Title must not be blank.");
    public static string Required(Patch<string?> patch, string previous) => patch.IsSpecified ? patch.Value ?? throw new ArgumentException("This field cannot be cleared.") : previous;
    public static bool Required(Patch<bool?> patch, bool previous) => patch.IsSpecified ? patch.Value ?? throw new ArgumentException("This field cannot be cleared.") : previous;
    public static void Batch(int count)
    {
        if (count is < 1 or > 50) throw new ArgumentException("A batch must contain 1 to 50 items.");
    }
    public static void Range(DateOnly start, DateOnly end, int maximumDays)
    {
        if (end < start || end.DayNumber - start.DayNumber >= maximumDays) throw new ArgumentException($"Range must contain 1 to {maximumDays} days.");
    }
    public static void CreatedBy(string value)
    {
        if (value is not ("user" or "claude")) throw new ArgumentException("Local creator must be user or claude.");
    }
    public static string Timestamp(TimeProvider clock) => clock.GetUtcNow().ToString("O", CultureInfo.InvariantCulture);
}
