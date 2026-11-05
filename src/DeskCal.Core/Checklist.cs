using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeskCal.Core;

public sealed record ChecklistItem(string ItemId, string Text, bool IsDone, int Position, long Revision, bool IsDeleted = false);

/// <summary>
/// 회차(또는 단일 일정)의 체크리스트(feedback 3-4). Google에는 항목마다 extendedProperties.private 키 하나로 싣는다.
/// patch는 보내지 않은 키를 그대로 두므로 삭제도 표시만 남긴다. 값 1024자·일정당 32kB 제한 안에 들도록 개수와 길이를 제한한다.
/// </summary>
public static class Checklist
{
    public const int MAX_ITEMS = 40;
    public const int MAX_TEXT_LENGTH = 200;
    private const int MAX_ENCODED_LENGTH = 900;
    private const string KEY_PREFIX = "deskcalCheck";
    private static readonly JsonSerializerOptions Json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static IReadOnlyList<ChecklistItem> Parse(string json) => JsonSerializer.Deserialize<ChecklistItem[]>(json, Json)!;
    public static string Serialize(IReadOnlyList<ChecklistItem> items) => JsonSerializer.Serialize(items, Json);

    public static IReadOnlyList<ChecklistItem> Visible(IReadOnlyList<ChecklistItem> items) => Sorted(items.Where(i => !i.IsDeleted));

    public static IReadOnlyList<ChecklistItem> Add(IReadOnlyList<ChecklistItem> items, IReadOnlyList<string> texts)
    {
        if (items.Count + texts.Count > MAX_ITEMS) throw new InvalidOperationException($"A checklist holds at most {MAX_ITEMS} items, including deleted ones.");
        var position = items.Count == 0 ? 0 : items.Max(i => i.Position);
        return [.. items, .. texts.Select(text => new ChecklistItem(Guid.NewGuid().ToString("N")[..16], ValidText(text), false, ++position, 1))];
    }

    public static IReadOnlyList<ChecklistItem> Update(IReadOnlyList<ChecklistItem> items, string itemId, string? text = null, bool? isDone = null, bool? isDeleted = null)
    {
        var found = items.SingleOrDefault(i => i.ItemId == itemId) ?? throw new KeyNotFoundException("Checklist item not found.");
        var changed = found with { Text = text is null ? found.Text : ValidText(text), IsDone = isDone ?? found.IsDone, IsDeleted = isDeleted ?? found.IsDeleted };
        // 바뀐 것이 없으면 번호를 올리지 않는다. 올리면 다른 PC의 진짜 변경을 옛 값이 이긴다.
        if (changed == found) return items;
        return items.Select(i => i.ItemId == itemId ? changed with { Revision = found.Revision + 1 } : i).ToArray();
    }

    // 항목마다 변경 번호가 높은 쪽을 따른다(기기 시계를 믿지 않는다). 같은 번호로 내용이 다르면 서버에 나중에 도착하는 이 PC의 값을 번호를 올려 남긴다.
    public static IReadOnlyList<ChecklistItem> Merge(IReadOnlyList<ChecklistItem> local, IReadOnlyList<ChecklistItem> remote)
    {
        var merged = remote.ToDictionary(i => i.ItemId);
        foreach (var item in local)
        {
            if (!merged.TryGetValue(item.ItemId, out var other) || item.Revision > other.Revision) merged[item.ItemId] = item;
            else if (item.Revision == other.Revision && item != other) merged[item.ItemId] = item with { Revision = item.Revision + 1 };
        }
        return Sorted(merged.Values);
    }

    public static void WriteProperties(IReadOnlyList<ChecklistItem> items, IDictionary<string, string> properties)
    {
        foreach (var item in items)
            properties[KEY_PREFIX + item.ItemId] = JsonSerializer.Serialize(new Wire(item.Text, item.IsDone, item.Position, item.Revision, item.IsDeleted), Json);
    }

    public static IReadOnlyList<ChecklistItem> ReadProperties(IDictionary<string, string>? properties)
    {
        if (properties is null) return [];
        var result = new List<ChecklistItem>();
        foreach (var (key, value) in properties)
        {
            if (!key.StartsWith(KEY_PREFIX, StringComparison.Ordinal)) continue;
            try
            {
                if (JsonSerializer.Deserialize<Wire>(value, Json) is { Text: not null } wire)
                    result.Add(new ChecklistItem(key[KEY_PREFIX.Length..], wire.Text, wire.IsDone, wire.Position, wire.Revision, wire.IsDeleted));
            }
            catch (JsonException)
            {
                // 다른 앱이 고쳐 망가진 값은 건너뛴다. 일정 전체의 동기화를 멈추지 않는다.
            }
        }
        return Sorted(result);
    }

    private static string ValidText(string text)
    {
        var value = text.Trim();
        // 제어 문자는 JSON에서 6배로 늘어난다. Google은 1024자가 넘는 값을 잘라 버리므로 직렬화한 길이까지 본다.
        if (value.Length is 0 or > MAX_TEXT_LENGTH || JsonSerializer.Serialize(value, Json).Length > MAX_ENCODED_LENGTH)
            throw new ArgumentException($"Checklist text must be 1 to {MAX_TEXT_LENGTH} characters without control characters.");
        return value;
    }

    private static ChecklistItem[] Sorted(IEnumerable<ChecklistItem> items) => items.OrderBy(i => i.Position).ThenBy(i => i.ItemId, StringComparer.Ordinal).ToArray();

    private sealed record Wire(
        [property: JsonPropertyName("t")] string Text, [property: JsonPropertyName("d")] bool IsDone, [property: JsonPropertyName("p")] int Position,
        [property: JsonPropertyName("r")] long Revision, [property: JsonPropertyName("x")] bool IsDeleted);
}
