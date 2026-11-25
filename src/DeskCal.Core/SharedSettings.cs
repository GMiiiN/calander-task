using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace DeskCal.Core;

/// <summary>
/// 기기 사이에 맞추는 DeskCal 설정(S6). Google Drive 앱 전용 공간으로 다른 기기와 주고받는다(SharedSettingsSync).
/// 키마다 값과 버전 (rev, 쓴 기기)을 둔다. 버전은 rev, 같으면 기기 ID 순으로 비교하고 큰 쪽이 새 값이다.
/// 이 PC에서 바꾼 값은 올리기 전까지 dirty이고, 바꿀 때 알던 버전(base)보다 새 값이 서버에 먼저 확정되면 원격 우선이다.
/// 키: category_name:&lt;color_id&gt;(카테고리 별칭). 학기(S7)와 날짜별 순서(S8)도 같은 저장소를 쓴다.
/// </summary>
public static class SharedSettings
{
    public const string CategoryPrefix = "category_name:";
    internal const string DeviceKey = "shared_device_id";

    public readonly record struct Version(long Rev, string Writer) : IComparable<Version>
    {
        public static readonly Version None = new(0, "");
        public int CompareTo(Version other) => Rev != other.Rev ? Rev.CompareTo(other.Rev) : string.CompareOrdinal(Writer, other.Writer);
        public static bool operator >(Version left, Version right) => left.CompareTo(right) > 0;
        public static bool operator <(Version left, Version right) => left.CompareTo(right) < 0;
        public static bool operator >=(Version left, Version right) => left.CompareTo(right) >= 0;
        public static bool operator <=(Version left, Version right) => left.CompareTo(right) <= 0;
    }

    // 원격 파일의 항목. Value는 JSON 값이다.
    public sealed record Entry(string Key, JsonNode? Value, Version Version, string WrittenAt);

    // 이 PC의 행. dirty면 Version은 올릴 때 쓸 버전(Base.Rev + 1, 이 기기)이다.
    internal sealed record Row(string Key, string ValueJson, Version Version, string WrittenAt, bool IsDirty, Version Base, long EditSeq);

    // 기기(이 DB) ID. 처음 필요할 때 만든다. PC 전용 setting 표에 둔다(동기화하지 않음).
    internal static string DeviceId(SqliteConnection connection, SqliteTransaction? transaction)
    {
        using var read = Sql.Command(connection, transaction, "SELECT setting_value FROM setting WHERE setting_key=$key", ("$key", DeviceKey));
        if (read.ExecuteScalar() is string id) return id;
        using var write = Sql.Command(connection, transaction,
            "INSERT INTO setting(setting_key, setting_value) VALUES ($key, $id) ON CONFLICT(setting_key) DO NOTHING",
            ("$key", DeviceKey), ("$id", Guid.NewGuid().ToString("N")));
        write.ExecuteNonQuery();
        return (string)read.ExecuteScalar()!; // 다른 연결이 먼저 만들었으면 그 값
    }

    // 사람이 DB·파일을 읽을 수 있게 한글을 \uXXXX로 바꾸지 않는다. 값 비교는 늘 이 함수를 거친 문자열끼리 한다.
    private static readonly JsonSerializerOptions Readable = new() { Encoder = TaskSync.ConflictJson.Encoder };
    internal static string Serialize(JsonNode? value) => value?.ToJsonString(Readable) ?? "null";

    // 이 PC에서 값을 바꾼다. 아직 올리지 않은 변경이 있으면 그 기준 버전을 그대로 두고 값만 바꾼다.
    internal static void Edit(SqliteConnection connection, SqliteTransaction transaction, string key, JsonNode? value, string now)
    {
        var me = DeviceId(connection, transaction);
        var row = Read(connection, transaction, key);
        var json = Serialize(value);
        if (row is { IsDirty: false } && row.ValueJson == json) return;
        var baseVersion = row is { IsDirty: true } ? row.Base : row?.Version ?? Version.None;
        using var command = Sql.Command(connection, transaction, """
            INSERT INTO shared_entry(entry_key, value_json, rev, writer, written_at, is_dirty, base_rev, base_writer, edit_seq)
            VALUES ($key, $value, $rev, $me, $now, 1, $baseRev, $baseWriter, 1)
            ON CONFLICT(entry_key) DO UPDATE SET value_json=excluded.value_json, rev=excluded.rev, writer=excluded.writer,
                written_at=excluded.written_at, is_dirty=1, base_rev=excluded.base_rev, base_writer=excluded.base_writer, edit_seq=edit_seq+1
            """, ("$key", key), ("$value", json), ("$rev", baseVersion.Rev + 1), ("$me", me), ("$now", now),
            ("$baseRev", baseVersion.Rev), ("$baseWriter", baseVersion.Writer));
        command.ExecuteNonQuery();
    }

    internal static Row? Read(SqliteConnection connection, SqliteTransaction? transaction, string key) =>
        ReadAll(connection, transaction, "WHERE entry_key=$key", ("$key", key)).SingleOrDefault();

    internal static List<Row> ReadAll(SqliteConnection connection, SqliteTransaction? transaction, string filter = "",
        params (string Name, object? Value)[] parameters)
    {
        using var command = Sql.Command(connection, transaction, $"SELECT * FROM shared_entry {filter} ORDER BY entry_key", parameters);
        using var reader = command.ExecuteReader();
        var rows = new List<Row>();
        while (reader.Read())
            rows.Add(new(Sql.Text(reader, "entry_key")!, Sql.Text(reader, "value_json")!, new(Sql.Number(reader, "rev"), Sql.Text(reader, "writer")!),
                Sql.Text(reader, "written_at")!, Sql.Number(reader, "is_dirty") != 0,
                new(Sql.Number(reader, "base_rev"), Sql.Text(reader, "base_writer")!), Sql.Number(reader, "edit_seq")));
        return rows;
    }

    /// <summary>
    /// S6 이전에 이 PC에서만 바꿔 둔 카테고리 이름도 다른 기기와 맞추도록 올릴 변경으로 만든다(처음 버전, 기준 없음).
    /// 다른 기기가 이미 올린 이름이 있으면 합칠 때 원격 우선으로 밀리고 충돌 기록에 남는다. 기본 이름은 올리지 않는다.
    /// </summary>
    internal static void SeedCategoryNames(SqliteConnection connection, SqliteTransaction transaction, string now)
    {
        var shared = ReadAll(connection, transaction, "WHERE entry_key LIKE 'category_name:%'").Select(r => r.Key).ToHashSet();
        foreach (var category in CategoryRepository.List(connection, transaction))
            if (!shared.Contains(CategoryPrefix + category.ColorId) && category.Name != CategoryRepository.DefaultName(category.ColorId))
                Edit(connection, transaction, CategoryPrefix + category.ColorId, JsonValue.Create(category.Name), now);
    }

    /// <summary>
    /// 다른 기기들의 항목을 합친다. 키마다 가장 큰 버전이 이긴다.
    /// 이 PC에서 바꾸고 아직 올리지 않은 값은, 바꿀 때 알던 버전보다 새 값이 먼저 올라왔으면 버리고(원격 우선) 충돌 기록에 남긴다.
    /// 바뀐 키를 돌려준다.
    /// </summary>
    internal static IReadOnlyList<string> Merge(SqliteConnection connection, SqliteTransaction transaction, IEnumerable<Entry> remote, string now)
    {
        var me = DeviceId(connection, transaction);
        var best = new Dictionary<string, Entry>();
        foreach (var entry in remote)
            if (!best.TryGetValue(entry.Key, out var known) || entry.Version > known.Version) best[entry.Key] = entry;
        var changed = new List<string>();
        foreach (var (key, incoming) in best.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            var row = Read(connection, transaction, key);
            var json = Serialize(incoming.Value);
            if (row is null)
            {
                Store(connection, transaction, incoming, json);
                changed.Add(key);
            }
            else if (row.IsDirty)
            {
                if (incoming.Version <= row.Base) continue;
                if (incoming.Version.Writer == me)
                {
                    // 이 PC가 올렸지만 정리하기 전에 끝났거나(같은 값) 올리는 사이 또 바꿨다. 그 버전을 기준으로 삼는다.
                    if (row.ValueJson == json) Store(connection, transaction, incoming, json);
                    else Rebase(connection, transaction, key, incoming.Version);
                    continue;
                }
                RecordConflict(connection, transaction, row, incoming, now);
                Store(connection, transaction, incoming, json);
                changed.Add(key);
            }
            else if (incoming.Version > row.Version)
            {
                // 이 PC가 올린 값이 같은 rev에서 동시에 올라간 다른 기기의 값에 밀렸다(기기 ID 순). 알려 둔다.
                if (row.Version.Writer == me && incoming.Version.Rev == row.Version.Rev && row.ValueJson != json)
                    RecordConflict(connection, transaction, row, incoming, now);
                Store(connection, transaction, incoming, json);
                if (row.ValueJson != json) changed.Add(key);
            }
        }
        return changed;
    }

    private static void Store(SqliteConnection connection, SqliteTransaction transaction, Entry entry, string json)
    {
        using var command = Sql.Command(connection, transaction, """
            INSERT INTO shared_entry(entry_key, value_json, rev, writer, written_at, is_dirty, base_rev, base_writer)
            VALUES ($key, $value, $rev, $writer, $at, 0, $rev, $writer)
            ON CONFLICT(entry_key) DO UPDATE SET value_json=excluded.value_json, rev=excluded.rev, writer=excluded.writer,
                written_at=excluded.written_at, is_dirty=0, base_rev=excluded.rev, base_writer=excluded.writer
            """, ("$key", entry.Key), ("$value", json), ("$rev", entry.Version.Rev), ("$writer", entry.Version.Writer), ("$at", entry.WrittenAt));
        command.ExecuteNonQuery();
    }

    private static void Rebase(SqliteConnection connection, SqliteTransaction transaction, string key, Version published)
    {
        using var command = Sql.Command(connection, transaction,
            "UPDATE shared_entry SET base_rev=$rev, base_writer=$writer, rev=$rev+1 WHERE entry_key=$key AND is_dirty=1",
            ("$rev", published.Rev), ("$writer", published.Writer), ("$key", key));
        command.ExecuteNonQuery();
    }

    /// <summary>올린 뒤: 올리는 사이 또 바꾸지 않은 행은 그 버전으로 확정하고, 또 바꾼 행은 올린 버전을 기준으로 다시 올릴 변경으로 둔다.</summary>
    internal static void MarkPublished(SqliteConnection connection, SqliteTransaction transaction, IEnumerable<Row> published)
    {
        foreach (var row in published.Where(r => r.IsDirty))
        {
            using var clean = Sql.Command(connection, transaction, """
                UPDATE shared_entry SET is_dirty=0, base_rev=rev, base_writer=writer
                WHERE entry_key=$key AND is_dirty=1 AND edit_seq=$seq AND rev=$rev AND writer=$writer
                """, ("$key", row.Key), ("$seq", row.EditSeq), ("$rev", row.Version.Rev), ("$writer", row.Version.Writer));
            if (clean.ExecuteNonQuery() == 0) Rebase(connection, transaction, row.Key, row.Version);
        }
    }

    private static void RecordConflict(SqliteConnection connection, SqliteTransaction transaction, Row discarded, Entry kept, string now)
    {
        var json = new JsonObject
        {
            ["key"] = discarded.Key, ["value"] = JsonNode.Parse(discarded.ValueJson),
            ["version"] = new JsonObject { ["rev"] = discarded.Version.Rev, ["writer"] = discarded.Version.Writer },
            ["written_at"] = discarded.WrittenAt,
            ["replaced_by"] = new JsonObject
            {
                ["value"] = kept.Value?.DeepClone(), ["rev"] = kept.Version.Rev, ["writer"] = kept.Version.Writer, ["written_at"] = kept.WrittenAt
            }
        };
        using var command = Sql.Command(connection, transaction,
            "INSERT INTO sync_conflict(item_kind, item_uid, discarded_json, occurred_at) VALUES ('settings', $key, $json, $at)",
            ("$key", discarded.Key), ("$json", json.ToJsonString(new JsonSerializerOptions { Encoder = TaskSync.ConflictJson.Encoder })), ("$at", now));
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// 공유한 카테고리 별칭을 category 표에 반영한다. 이름은 서로 달라야 하므로(MCP가 이름으로 찾는다), 두 색이 같은 이름이면
    /// 버전이 큰 쪽이 그 이름을 갖고 나머지는 그 색의 기본 이름을 쓴다. 기기마다 같은 항목에서 같은 결과가 나온다.
    /// 별칭 항목이 없는 색은 지금 이름을 그대로 둔다.
    /// </summary>
    internal static void ApplyCategoryNames(SqliteConnection connection, SqliteTransaction transaction)
    {
        var categories = CategoryRepository.List(connection, transaction);
        var ids = categories.Select(c => c.ColorId).ToHashSet();
        var wanted = new Dictionary<string, (string Name, Version Version)>();
        foreach (var row in ReadAll(connection, transaction, "WHERE entry_key LIKE 'category_name:%'"))
        {
            var id = row.Key[CategoryPrefix.Length..];
            if (ids.Contains(id) && Text(row.ValueJson) is { } name) wanted[id] = (name, row.Version);
        }
        var taken = new HashSet<string>(StringComparer.Ordinal);
        var result = new Dictionary<string, string>();
        bool Free(string id, string name) => name.Length > 0 && !taken.Contains(name) && !ids.Any(other => other != id && other == name);
        void Assign(string id, string name)
        {
            result[id] = name;
            taken.Add(name);
        }
        // 버전이 큰 별칭부터, 그다음 별칭이 없는 색의 지금 이름(색 번호순)
        foreach (var (id, (name, _)) in wanted.OrderByDescending(pair => pair.Value.Version))
            if (Free(id, name)) Assign(id, name);
        foreach (var category in categories.Where(c => !result.ContainsKey(c.ColorId) && !wanted.ContainsKey(c.ColorId)))
            if (Free(category.ColorId, category.Name)) Assign(category.ColorId, category.Name);
        foreach (var category in categories.Where(c => !result.ContainsKey(c.ColorId)))
        {
            var fallback = CategoryRepository.DefaultName(category.ColorId) ?? $"색 {category.ColorId}";
            Assign(category.ColorId, Free(category.ColorId, fallback) ? fallback : $"{fallback} ({category.ColorId})");
        }
        foreach (var category in categories.Where(c => result[c.ColorId] != c.Name))
        {
            using var command = Sql.Command(connection, transaction, "UPDATE category SET name=$name WHERE color_id=$id",
                ("$name", result[category.ColorId]), ("$id", category.ColorId));
            command.ExecuteNonQuery();
        }
    }

    // 별칭 값은 앞뒤 공백을 뺀 문자열이어야 한다. 아니면(다른 형식·빈 값) 쓰지 않는다.
    private static string? Text(string json)
    {
        try
        {
            return JsonNode.Parse(json) is JsonValue value && value.TryGetValue<string>(out var text) && text.Trim() is { Length: > 0 } name
                ? name : null;
        }
        catch (JsonException) { return null; }
    }

    // 상태 상세에 보일 이름. 모르는 키는 키 그대로 보인다.
    public static string Describe(string key, string valueJson) =>
        key.StartsWith(CategoryPrefix, StringComparison.Ordinal) ? $"카테고리 이름 ‘{Text(valueJson) ?? "?"}’"
        : key.StartsWith(DayEventOrder.Prefix, StringComparison.Ordinal)
            && DateOnly.TryParseExact(key[DayEventOrder.Prefix.Length..], "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var day)
            ? $"{day.Month}월 {day.Day}일 일정 순서"
        : key;
}
