using System.Globalization;

namespace DeskCal.Core;

public sealed record RecurrenceInput(string Frequency, int Interval = 1, IReadOnlyList<DayOfWeek>? Weekdays = null, string? Until = null, int? Count = null)
{
    private static readonly string[] DayNames = ["SU", "MO", "TU", "WE", "TH", "FR", "SA"];

    public string ToRule(EventRecord value)
    {
        if (Frequency is not ("daily" or "weekly" or "monthly" or "yearly")) throw new ArgumentException("Unsupported frequency.");
        if (Interval < 1 || Count is < 1) throw new ArgumentException("Interval and count must be positive.");
        if (Count.HasValue && Until is not null) throw new ArgumentException("Count and until cannot be combined.");
        var start = Validation.Date(value.StartDate);
        if (Frequency == "weekly" && (Weekdays is null || !Weekdays.Contains(start.DayOfWeek)))
            throw new ArgumentException("Weekly recurrence must include the start weekday.");
        if (Weekdays is not null && (Frequency != "weekly" || Weekdays.Any(day => !Enum.IsDefined(day))))
            throw new ArgumentException("Weekdays are only supported for weekly recurrence.");
        var rule = $"RRULE:FREQ={Frequency.ToUpperInvariant()};INTERVAL={Interval}";
        if (Frequency == "weekly") rule += ";BYDAY=" + string.Join(',', Weekdays!.Distinct().Order().Select(day => DayNames[(int)day]));
        if (Count.HasValue) rule += $";COUNT={Count.Value}";
        if (Until is not null)
        {
            var until = Validation.Date(Until);
            if (until < start) throw new ArgumentException("Until must not precede the start date.");
            if (value.StartTime is null) rule += ";UNTIL=" + until.ToString("yyyyMMdd");
            else
            {
                var zone = TimeZoneInfo.FindSystemTimeZoneById(value.TimeZone!);
                var last = until.ToDateTime(new TimeOnly(23, 59, 59));
                rule += ";UNTIL=" + TimeZoneInfo.ConvertTimeToUtc(last, zone).ToString("yyyyMMdd'T'HHmmss'Z'");
            }
        }
        return rule;
    }

    public static bool IsEditable(string? rule)
    {
        if (rule is null) return true;
        if (!rule.StartsWith("RRULE:", StringComparison.Ordinal) || rule.Contains('\n')) return false;
        var parts = rule[6..].Split(';').Select(part => part.Split('=')).ToArray();
        if (parts.Any(part => part.Length != 2) || parts.Select(part => part[0]).Distinct().Count() != parts.Length) return false;
        var fields = parts.ToDictionary(part => part[0], part => part[1]);
        if (!fields.TryGetValue("FREQ", out var frequency) || frequency is not ("DAILY" or "WEEKLY" or "MONTHLY" or "YEARLY")) return false;
        if (fields.ContainsKey("UNTIL") && fields.ContainsKey("COUNT")) return false;
        foreach (var (name, value) in fields)
        {
            switch (name)
            {
                case "FREQ": break;
                case "INTERVAL": case "COUNT":
                    if (!int.TryParse(value, out var number) || number < 1) return false;
                    break;
                case "BYDAY":
                    if (frequency != "WEEKLY" || value.Split(',').Any(day => !DayNames.Contains(day))) return false;
                    break;
                case "UNTIL":
                    if (!DateTime.TryParseExact(value, ["yyyyMMdd", "yyyyMMdd'T'HHmmss'Z'"], CultureInfo.InvariantCulture, DateTimeStyles.None, out _)) return false;
                    break;
                default: return false;
            }
        }
        return true;
    }
}

public static class OccurrenceId
{
    public static string Create(string eventUid, string originalStartKey)
    {
        var id = eventUid + "~" + originalStartKey;
        Parse(id);
        return id;
    }

    public static (string EventUid, string OriginalStartKey) Parse(string value)
    {
        var parts = value.Split('~');
        if (parts.Length != 2 || parts[0].Length != 32 || parts[0].Any(c => c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
            throw new ArgumentException("Invalid occurrence ID.");
        if (!DateTime.TryParseExact(parts[1], ["yyyy-MM-dd", "yyyy-MM-dd'T'HH:mm:ss'Z'"], CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            throw new ArgumentException("Invalid original start key.");
        return (parts[0], parts[1]);
    }
}
