using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Google.Apis.Calendar.v3.Data;
using GoogleTask = Google.Apis.Tasks.v1.Data.Task;

namespace DeskCal.Core;

/// <summary>
/// 412(다른 곳에서 먼저 바뀜) 때의 3자 병합(S4). 마지막으로 Google과 맞춘 값(base), 이 PC 값(mine), 지금 Google 값(theirs)을
/// 필드 묶음마다 비교한다. 한쪽만 바꿨으면 그쪽 값을, 둘 다 같은 값으로 바꿨으면 그 값을, 둘 다 다르게 바꿨으면 Google 값을 남기고
/// 이 PC 값을 충돌로 돌려준다. 시각·반복·시간대처럼 함께 바뀌어야 하는 값은 한 묶음(time)이라 갈라지지 않는다.
/// </summary>
public static class FieldMerge
{
    public sealed record Conflict(string Field, JsonNode? Mine, JsonNode? Kept);

    public sealed record Result(JsonObject Values, IReadOnlyList<Conflict> Conflicts, IReadOnlySet<string> FromTheirs);

    public static Result Merge(JsonObject @base, JsonObject mine, JsonObject theirs, Func<string, JsonNode?, JsonNode?, bool> same)
    {
        var values = new JsonObject();
        var conflicts = new List<Conflict>();
        var fromTheirs = new HashSet<string>();
        foreach (var (field, theirValue) in theirs)
        {
            var mineValue = mine[field];
            var baseValue = @base[field];
            JsonNode? chosen;
            if (same(field, mineValue, baseValue) || same(field, mineValue, theirValue))
            {
                chosen = theirValue; // 이 PC는 그대로였거나 같은 값으로 바꿨다
                if (!same(field, mineValue, theirValue)) fromTheirs.Add(field);
            }
            else if (same(field, theirValue, baseValue)) chosen = mineValue; // 이 PC만 바꿨다
            else
            {
                chosen = theirValue; // 둘 다 다르게 바꿨다: Google 값을 남긴다
                fromTheirs.Add(field);
                conflicts.Add(new(field, mineValue?.DeepClone(), theirValue?.DeepClone()));
            }
            values[field] = chosen?.DeepClone();
        }
        return new(values, conflicts, fromTheirs);
    }

    public static bool Changed(JsonObject @base, JsonObject value, Func<string, JsonNode?, JsonNode?, bool> same) =>
        value.Any(pair => !same(pair.Key, pair.Value, @base[pair.Key]));

    internal static JsonObject? Parse(string? json)
    {
        if (json is null) return null;
        try { return JsonNode.Parse(json) as JsonObject; }
        catch (JsonException) { return null; }
    }

    // sync_conflict.detail_json. deleted: local(이 PC 삭제가 Google 변경에 밀림) | remote(이 PC 변경이 Google 삭제에 밀림)
    internal static string Detail(IEnumerable<Conflict> conflicts, string? deleted = null)
    {
        var fields = new JsonArray(conflicts.Select(c => (JsonNode)new JsonObject
        {
            ["field"] = c.Field, ["mine"] = c.Mine?.DeepClone(), ["kept"] = c.Kept?.DeepClone()
        }).ToArray());
        var detail = new JsonObject { ["fields"] = fields };
        if (deleted is not null) detail["deleted"] = deleted;
        return detail.ToJsonString(new JsonSerializerOptions { Encoder = TaskSync.ConflictJson.Encoder });
    }
}

/// <summary>일정의 필드 묶음. Google 표현(base·theirs)과 이 PC 행(mine)을 같은 꼴로 만든다.</summary>
public static class EventFields
{
    public static JsonObject Of(EventRecord value) => Of(value.Title, value.Memo, value.GoogleStartRaw, value.GoogleEndRaw, value.TimeZone,
        value.RecurrenceRule, value.ColorId, value.IsBusy, value.IsDone, value.IsDday, value.Status);

    public static JsonObject Baseline(Event item)
    {
        var fields = Of(item);
        fields["checklistHash"] = Checklist.Fingerprint(item.ExtendedProperties?.Private__);
        return fields;
    }

    public static JsonObject Of(Event item)
    {
        var properties = item.ExtendedProperties?.Private__;
        string? Property(string name) => properties is not null && properties.TryGetValue(name, out var text) ? text : null;
        var start = item.Start?.Date ?? item.Start?.DateTimeRaw;
        return Of(item.Summary ?? "", item.Description ?? "", start, item.End?.Date ?? item.End?.DateTimeRaw ?? start, item.Start?.TimeZone ?? item.End?.TimeZone,
            item.Recurrence is { Count: > 0 } lines ? string.Join('\n', lines) : null, item.ColorId, item.Transparency != "transparent",
            Property("deskcalDone") == "1", Property("deskcalDday") == "1", item.Status ?? "confirmed");
    }

    private static JsonObject Of(string title, string memo, string? start, string? end, string? zone, string? rule, string? color, bool busy, bool done,
        bool dday, string status) => new()
    {
        ["title"] = title, ["memo"] = memo,
        ["time"] = new JsonObject { ["start"] = start, ["end"] = end, ["zone"] = zone, ["rule"] = rule },
        ["color"] = string.IsNullOrEmpty(color) ? null : color, ["busy"] = busy, ["done"] = done, ["dday"] = dday, ["status"] = status,
    };

    // 시각은 표현(오프셋·Z)이 달라도 같은 순간이면 같다. 반복이 있을 때만 시간대도 비교한다(전개 기준이라서).
    public static bool Same(string field, JsonNode? left, JsonNode? right)
    {
        if (field != "time" || left is not JsonObject a || right is not JsonObject b) return JsonNode.DeepEquals(left, right);
        return TimeKey(a) == TimeKey(b);
    }

    private static string TimeKey(JsonObject time)
    {
        string? Text(string name) => time[name]?.GetValue<string>();
        string Instant(string? raw) => raw is null ? "" : raw.Length == 10 ? raw
            : EventTime.ParseInstant(raw, Text("zone")).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
        var rule = Text("rule");
        return $"{Instant(Text("start"))}/{Instant(Text("end"))}" + (rule is null ? "" : $"|{Text("zone")}|{rule}");
    }

    // 병합 결과를 이 PC 행에 넣는다. 시각이 바뀌면 표시용 날짜·시각도 다시 계산한다.
    public static EventRecord Apply(EventRecord value, JsonObject fields, EventTime time)
    {
        var t = (JsonObject)fields["time"]!;
        var result = value with
        {
            Title = fields["title"]!.GetValue<string>(), Memo = fields["memo"]!.GetValue<string>(),
            GoogleStartRaw = t["start"]?.GetValue<string>(), GoogleEndRaw = t["end"]?.GetValue<string>(), TimeZone = t["zone"]?.GetValue<string>(),
            RecurrenceRule = t["rule"]?.GetValue<string>(), ColorId = fields["color"]?.GetValue<string>(),
            IsBusy = fields["busy"]!.GetValue<bool>(), IsDone = fields["done"]!.GetValue<bool>(), IsDday = fields["dday"]!.GetValue<bool>(),
            Status = fields["status"]!.GetValue<string>(),
        };
        return time.Derive(result);
    }
}

/// <summary>할 일의 필드 묶음. 완료 여부와 완료 시각은 한 묶음(done)이다. 메모는 DeskCal 시도 표식을 뺀 본문이다.</summary>
public static class TodoFields
{
    public static JsonObject Of(string title, string memo, string? due, bool done) =>
        new() { ["title"] = title, ["memo"] = memo, ["due"] = due, ["done"] = done };

    public static JsonObject Of(GoogleTask item) =>
        Of(item.Title ?? "", TaskMarker.Read(item.Notes).Memo, item.Due is { Length: >= 10 } due ? due[..10] : null, item.Status == "completed");

    public static bool Same(string field, JsonNode? left, JsonNode? right) => JsonNode.DeepEquals(left, right);
}
