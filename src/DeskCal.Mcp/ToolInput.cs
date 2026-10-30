using System.Globalization;
using System.Text.Json;
using DeskCal.Core;

namespace DeskCal.Mcp;

internal sealed class ToolInput(JsonElement value)
{
    public void Only(params string[] names)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new ArgumentException("Arguments must be an object.");
        foreach (var property in value.EnumerateObject())
            if (!names.Contains(property.Name)) throw new ArgumentException($"Unknown field: {property.Name}.");
    }

    public bool Has(string name) => value.TryGetProperty(name, out _);
    public string Required(string name) => Text(name) ?? throw new ArgumentException($"{name} is required and cannot be null.");
    public string? Text(string name)
    {
        if (!value.TryGetProperty(name, out var item) || item.ValueKind == JsonValueKind.Null) return null;
        return item.ValueKind == JsonValueKind.String ? item.GetString() : throw new ArgumentException($"{name} must be a string.");
    }
    public bool? Boolean(string name)
    {
        if (!value.TryGetProperty(name, out var item)) return null;
        return item.ValueKind is JsonValueKind.True or JsonValueKind.False ? item.GetBoolean() : throw new ArgumentException($"{name} must be a boolean and cannot be null.");
    }
    public int? Integer(string name)
    {
        if (!value.TryGetProperty(name, out var item) || item.ValueKind == JsonValueKind.Null) return null;
        return item.ValueKind == JsonValueKind.Number && item.TryGetInt32(out var result) ? result : throw new ArgumentException($"{name} must be an integer.");
    }
    public Patch<string?> TextPatch(string name) => Has(name) ? new(true, Text(name)) : default;
    public Patch<bool?> BooleanPatch(string name) => Has(name) ? new(true, Boolean(name)) : default;
    public DateOnly? Date(string name) => Text(name) is { } text ? ParseDate(text) : null;
    public TimeOnly? Time(string name) => Text(name) is { } text ? ParseTime(text) : null;

    public ToolInput[] Batch(string name)
    {
        if (!value.TryGetProperty(name, out var items) || items.ValueKind != JsonValueKind.Array || items.GetArrayLength() is < 1 or > 50)
            throw new ArgumentException($"{name} must contain 1 to 50 items.");
        return items.EnumerateArray().Select(item => new ToolInput(item)).ToArray();
    }

    public RecurrenceInput? Repeat()
    {
        if (!value.TryGetProperty("repeat", out var repeat) || repeat.ValueKind == JsonValueKind.Null) return null;
        var input = new ToolInput(repeat);
        input.Only("frequency", "interval", "weekdays", "until", "count");
        DayOfWeek[]? weekdays = null;
        if (repeat.TryGetProperty("weekdays", out var days))
        {
            if (days.ValueKind != JsonValueKind.Array) throw new ArgumentException("weekdays must be an array of lowercase English weekday names.");
            weekdays = days.EnumerateArray().Select(day =>
            {
                var name = day.ValueKind == JsonValueKind.String ? day.GetString() : null;
                return name is not null && Enum.TryParse<DayOfWeek>(name, true, out var parsed) && Enum.IsDefined(parsed) && name == parsed.ToString().ToLowerInvariant()
                    ? parsed : throw new ArgumentException("weekdays must contain lowercase English weekday names, such as monday.");
            }).ToArray();
        }
        if (input.Has("interval") && input.Integer("interval") is null) throw new ArgumentException("interval cannot be null.");
        return new(input.Required("frequency"), input.Integer("interval") ?? 1, weekdays, input.Text("until"), input.Integer("count"));
    }

    public (string Id, bool IsOccurrence) EventId()
    {
        if (Has("event_id") == Has("occurrence_id")) throw new ArgumentException("Supply exactly one of event_id or occurrence_id.");
        if (Has("occurrence_id"))
        {
            var id = Required("occurrence_id");
            OccurrenceId.Parse(id);
            return (id, true);
        }
        return (Uid("event_id"), false);
    }
    public string Uid(string name)
    {
        var id = Required(name);
        if (id.Length != 32 || id.Any(c => c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
            throw new ArgumentException($"{name} must be a local 32-character lowercase hexadecimal ID.");
        return id;
    }

    public static DateOnly ParseDate(string value) => DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
        ? date : throw new ArgumentException("Date must be a real YYYY-MM-DD date.");
    private static TimeOnly ParseTime(string value) => TimeOnly.TryParseExact(value, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)
        ? time : throw new ArgumentException("Time must be HH:mm between 00:00 and 23:59.");
}
