using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DeskCal.Core;

/// <summary>
/// 동기화 충돌 기록(sync_conflict, S4). 원격 우선으로 남긴 값과 버린 이 PC 값을 보여 주고, 확인(is_seen)하거나 이 PC 값을 다시 적용한다.
/// 다시 적용은 보통의 수정과 같다(dirty → 다음 동기화가 올린다). 함께 바뀌어야 하는 반복 일정의 시각·규칙은 다시 적용하지 않는다.
/// </summary>
public static class ConflictLog
{
    public sealed record Field(string Name, string Label, string Mine, string Kept, bool CanReapply);

    // Deleted: local(이 PC 삭제가 Google 수정에 밀려 취소됨) | remote(이 PC 변경이 Google 삭제에 밀림) | null
    public sealed record Entry(long Id, string Kind, string Uid, string Title, string OccurredAt, IReadOnlyList<Field> Fields, string? Deleted)
    {
        public bool CanReapply => Deleted == "local" || (Deleted is null && Fields.Any(f => f.CanReapply));

        public string Summary => Deleted switch
        {
            "local" => "이 PC에서 지웠지만 다른 곳에서 고쳐 남겨 둠",
            "remote" => "다른 곳에서 지워 이 PC의 변경은 버림",
            _ when Fields.Count == 0 => "Google 값으로 바뀜",
            _ => string.Join("·", Fields.Select(f => f.Label)) + " Google 값 유지"
        };
    }

    public static int UnseenCount(Database database)
    {
        using var connection = database.OpenConnection();
        using var command = Sql.Command(connection, null, "SELECT COUNT(*) FROM sync_conflict WHERE is_seen=0;");
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    public static IReadOnlyList<Entry> Unseen(Database database, int limit = 30)
    {
        using var connection = database.OpenConnection();
        using var command = Sql.Command(connection, null, """
            SELECT conflict_id, item_kind, item_uid, discarded_json, occurred_at, detail_json FROM sync_conflict
            WHERE is_seen=0 ORDER BY conflict_id DESC LIMIT $limit;
            """, ("$limit", limit));
        using var reader = command.ExecuteReader();
        var entries = new List<Entry>();
        while (reader.Read())
            entries.Add(Read(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5)));
        return entries;
    }

    public static void MarkSeen(Database database, long id)
    {
        using var connection = database.OpenConnection();
        using var command = Sql.Command(connection, null, "UPDATE sync_conflict SET is_seen=1 WHERE conflict_id=$id;", ("$id", id));
        command.ExecuteNonQuery();
    }

    public static void MarkAllSeen(Database database)
    {
        using var connection = database.OpenConnection();
        using var command = Sql.Command(connection, null, "UPDATE sync_conflict SET is_seen=1 WHERE is_seen=0;");
        command.ExecuteNonQuery();
    }

    /// <summary>이 PC 값을 다시 적용하고 확인한 것으로 둔다. 다시 적용할 수 없는 기록이면 InvalidOperationException이다.</summary>
    public static void Reapply(Database database, long id, TimeZoneInfo? localTimeZone = null)
    {
        var entry = Unseen(database, int.MaxValue).SingleOrDefault(e => e.Id == id) ?? throw new KeyNotFoundException("Conflict not found.");
        if (!entry.CanReapply) throw new InvalidOperationException("This conflict cannot be reapplied.");
        string discarded;
        using (var connection = database.OpenConnection())
        using (var command = Sql.Command(connection, null, "SELECT discarded_json FROM sync_conflict WHERE conflict_id=$id;", ("$id", id)))
            discarded = (string)command.ExecuteScalar()!;
        var mine = Detail(database, id);
        switch (entry.Kind)
        {
            case "event":
                ReapplyEvent(database, entry, JsonNode.Parse(discarded)!.AsObject(), mine, localTimeZone);
                break;
            case "todo":
                var todos = new TodoRepository(database);
                if (entry.Deleted == "local") todos.Delete(entry.Uid);
                else todos.Update(entry.Uid, new TodoPatch
                {
                    Title = mine.TryGetPropertyValue("title", out var title) ? new(true, title!.GetValue<string>()) : default,
                    Memo = mine.TryGetPropertyValue("memo", out var memo) ? new(true, memo!.GetValue<string>()) : default,
                    DueDate = mine.TryGetPropertyValue("due", out var due) ? new(true, due?.GetValue<string>()) : default,
                    IsDone = mine.TryGetPropertyValue("done", out var done) ? new(true, done!.GetValue<bool>()) : default,
                });
                break;
            case "settings" when entry.Uid.StartsWith(SharedSettings.CategoryPrefix, StringComparison.Ordinal):
                new CategoryRepository(database).Rename(entry.Uid[SharedSettings.CategoryPrefix.Length..],
                    JsonNode.Parse(discarded)!["value"]!.GetValue<string>());
                break;
            default:
                throw new InvalidOperationException("This conflict cannot be reapplied.");
        }
        MarkSeen(database, id);
    }

    private static void ReapplyEvent(Database database, Entry entry, JsonObject discarded, JsonObject mine, TimeZoneInfo? zone)
    {
        var events = new EventRepository(database, zone);
        var occurrence = discarded["ParentEventId"] is JsonValue parentId && parentId.TryGetValue<long>(out var parentRow)
            ? OccurrenceId.Create(events.GetRecords().Single(e => e.EventId == parentRow).EventUid, discarded["OriginalStartKey"]!.GetValue<string>())
            : null;
        if (occurrence is not null)
        {
            if (entry.Deleted == "local" || mine["status"]?.GetValue<string>() == "cancelled") events.DeleteOccurrence(occurrence);
            if (mine.TryGetPropertyValue("done", out var occurrenceDone)) events.SetOccurrenceDone(occurrence, occurrenceDone!.GetValue<bool>());
            return;
        }
        if (entry.Deleted == "local")
        {
            events.Delete(entry.Uid);
            return;
        }
        var patch = new EventPatch
        {
            Title = mine.TryGetPropertyValue("title", out var title) ? new(true, title!.GetValue<string>()) : default,
            Memo = mine.TryGetPropertyValue("memo", out var memo) ? new(true, memo!.GetValue<string>()) : default,
            ColorId = mine.TryGetPropertyValue("color", out var color) ? new(true, color?.GetValue<string>()) : default,
            IsBusy = mine.TryGetPropertyValue("busy", out var busy) ? new(true, busy!.GetValue<bool>()) : default,
            IsDone = mine.TryGetPropertyValue("done", out var done) ? new(true, done!.GetValue<bool>()) : default,
            IsDday = mine.TryGetPropertyValue("dday", out var dday) ? new(true, dday!.GetValue<bool>()) : default,
        };
        if (mine["time"] is JsonObject time && time["rule"] is null && events.Get(entry.Uid).RecurrenceRule is null)
        {
            // 한 번만 있는 일정의 시각은 이 PC 표시 시간대의 날짜·시각으로 다시 넣는다.
            var derived = new EventTime(zone).Derive(new EventRecord
            {
                GoogleStartRaw = time["start"]!.GetValue<string>(), GoogleEndRaw = time["end"]!.GetValue<string>(), TimeZone = time["zone"]?.GetValue<string>()
            });
            patch = patch with
            {
                StartDate = new(true, derived.StartDate), EndDate = new(true, derived.EndDate),
                StartTime = new(true, derived.StartTime), EndTime = new(true, derived.EndTime)
            };
        }
        events.Update(entry.Uid, patch);
    }

    // 다시 적용할 이 PC 값(필드 이름 → 값)
    private static JsonObject Detail(Database database, long id)
    {
        using var connection = database.OpenConnection();
        using var command = Sql.Command(connection, null, "SELECT detail_json FROM sync_conflict WHERE conflict_id=$id;", ("$id", id));
        var mine = new JsonObject();
        if (FieldMerge.Parse(command.ExecuteScalar() as string) is { } detail && detail["fields"] is JsonArray fields)
            foreach (var field in fields.OfType<JsonObject>())
                mine[field["field"]!.GetValue<string>()] = field["mine"]?.DeepClone();
        return mine;
    }

    private static Entry Read(long id, string kind, string uid, string discardedJson, string at, string? detailJson)
    {
        JsonObject? discarded;
        try { discarded = JsonNode.Parse(discardedJson) as JsonObject; }
        catch (JsonException) { discarded = null; }
        if (kind == "settings")
        {
            var value = discarded?["value"]?.ToJsonString() ?? "null";
            var kept = discarded?["replaced_by"]?["value"]?.ToJsonString() ?? "null";
            return new(id, kind, uid, SharedSettings.Describe(uid, value), at,
                [new("value", "값", Display(discarded?["value"]), Display(discarded?["replaced_by"]?["value"]),
                    uid.StartsWith(SharedSettings.CategoryPrefix, StringComparison.Ordinal) && value != kept)], null);
        }
        var title = discarded?["Title"]?.GetValue<string>() ?? "";
        var detail = FieldMerge.Parse(detailJson);
        var recurring = discarded?["RecurrenceRule"] is JsonValue;
        var fields = (detail?["fields"] as JsonArray ?? []).OfType<JsonObject>().Select(field =>
        {
            var name = field["field"]!.GetValue<string>();
            var reapply = name switch
            {
                "time" => !recurring && field["mine"]?["rule"] is null,
                "status" => discarded?["ParentEventId"] is JsonValue, // 회차 삭제·되살리기
                _ => true
            };
            return new Field(name, Label(name), Display(field["mine"], name), Display(field["kept"], name), reapply);
        }).ToArray();
        return new(id, kind, uid, title, at, fields, detail?["deleted"]?.GetValue<string>());
    }

    private static string Label(string field) => field switch
    {
        "title" => "제목", "memo" => "메모", "time" => "시각", "color" => "카테고리", "busy" => "바쁨 표시",
        "done" => "완료", "dday" => "D-day", "status" => "상태", "due" => "마감일", "value" => "값", _ => field
    };

    private static string Display(JsonNode? value, string? field = null)
    {
        if (value is null) return "(없음)";
        if (value is JsonObject time && field == "time")
        {
            string Point(string name)
            {
                var raw = time[name]?.GetValue<string>();
                if (raw is null) return "";
                if (raw.Length == 10) return raw;
                return TimeZoneInfo.ConvertTime(EventTime.ParseInstant(raw, time["zone"]?.GetValue<string>()), TimeZoneInfo.Local)
                    .ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
            }
            return $"{Point("start")} – {Point("end")}" + (time["rule"] is null ? "" : " (반복)");
        }
        if (value is JsonValue scalar && scalar.TryGetValue<bool>(out var flag))
            return field == "done" ? flag ? "완료" : "미완료" : flag ? "예" : "아니요";
        var text = value is JsonValue s && s.TryGetValue<string>(out var str) ? str : value.ToJsonString();
        text = text.ReplaceLineEndings(" ");
        return text.Length > 60 ? text[..60] + "…" : text.Length == 0 ? "(빈 값)" : text;
    }
}
