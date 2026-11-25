using System.Text.Json;
using System.Text.Json.Nodes;
using DeskCal.Core;

namespace DeskCal.Mcp;

internal sealed record Category(string ColorId, string Name, string ColorHex);

internal sealed class ToolOutput(Database database, EventRepository events, bool isWrite)
{
    internal static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
    private readonly IReadOnlyList<Category> _categories = database.Measure("categories", () => ReadCategories(database));
    private readonly bool _signedIn = database.Measure("account", () => PendingChanges.HasToken(database));
    // Capture the optional receipt metadata before mutating. A failed metadata read must never
    // turn a committed create into an error response that the caller could retry.
    private readonly bool? _appRunning = isWrite ? AppPresence.IsRunning(database) : null;
    private readonly JsonObject? _lastSync = isWrite ? SyncTimes(database) : null;
    private readonly Dictionary<string, (string Table, long Id, long Revision, string Uid)> _writes = [];
    private readonly long _conflictBefore = isWrite ? LatestConflict(database) : 0;
    public bool ShouldSend => _appRunning == false && _signedIn;
    private Dictionary<string, EventRecord>? _parents;

    private static JsonObject SyncTimes(Database database)
    {
        var times = new JsonObject { ["calendar"] = null, ["tasks"] = null };
        using var connection = database.OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT sync_key,last_synced_at FROM sync_state WHERE sync_key IN ('calendar_primary','tasks_default')";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            if (!reader.IsDBNull(1) && DateTimeOffset.TryParse(reader.GetString(1), out var at))
                times[reader.GetString(0) == "tasks_default" ? "tasks" : "calendar"] = at.UtcDateTime;
        return times;
    }

    private static long LatestConflict(Database database)
    {
        using var connection = database.OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COALESCE(MAX(conflict_id),0) FROM sync_conflict";
        return (long)cmd.ExecuteScalar()!;
    }

    // 응답은 이 시간까지만 기다린다(설계 §2.4). 전송 자체의 시간 제한이 아니다.
    internal static readonly TimeSpan ReceiptWait = TimeSpan.FromSeconds(5);

    // 커밋 뒤 전송이나 상태 조회가 실패해도 클라이언트에는 기존 로컬 영수증을 반환한다.
    // 마감·요청 취소는 기다림만 끊는다. 전송은 send가 쥔 MCP 수명 토큰으로 끝까지 간다.
    // 할 일 생성은 보내기 전에 결과 불명으로 표시하므로, 도중에 취소하면 Google에 닿지 않았어도 needs_review로 남는다.
    public async Task<JsonObject> SendAndVerifyAsync(JsonObject receipt, Func<Task<bool>> send, CancellationToken token)
    {
        var sending = Task.Run(send);
        try
        {
            var sent = await sending.WaitAsync(ReceiptWait, token);
            return Verify((JsonObject)receipt.DeepClone(), sent);
        }
        catch (Exception ex)
        {
            // 늦게 끝난 전송의 실패를 관찰해 둔다. 결과는 다음 조회·영수증이 행 상태로 보여 준다.
            _ = sending.ContinueWith(static t => t.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            var reason = ex is TimeoutException or OperationCanceledException ? "sync_timeout" : "sync_unavailable";
            foreach (var item in ReceiptItems(receipt))
                if ((string?)item["reason"] == "app_not_running") item["reason"] = reason;
            return receipt;
        }
    }

    private JsonObject Verify(JsonObject result, bool sent)
    {
        result["last_verified_sync"] = SyncTimes(database);
        var signedIn = PendingChanges.HasToken(database);
        using var connection = database.OpenConnection();
        foreach (var item in ReceiptItems(result))
        {
            var key = (string?)(item["occurrence_id"] ?? item["event_id"] ?? item["todo_id"]);
            if (key is null || !_writes.TryGetValue(key, out var row)) continue;
            using var cmd = connection.CreateCommand();
            cmd.CommandText = $"SELECT push_state, local_revision, EXISTS(SELECT 1 FROM sync_conflict WHERE item_uid=$uid AND conflict_id>$before) FROM {row.Table} WHERE {row.Table}_id=$id";
            cmd.Parameters.AddWithValue("$id", row.Id);
            cmd.Parameters.AddWithValue("$uid", row.Uid);
            cmd.Parameters.AddWithValue("$before", _conflictBefore);
            using var reader = cmd.ExecuteReader();
            if (!reader.Read()) continue;
            var state = reader.GetString(0);
            var reason = reader.GetBoolean(2) ? "conflict" : reader.GetInt64(1) != row.Revision ? "newer_revision"
                : PendingChanges.Reason(state, signedIn);
            if (reason is null && state == "synced") { item["sync"] = "synced"; item.Remove("reason"); }
            else { item["sync"] = "pending"; item["reason"] = reason ?? (sent ? "retry_pending" : "sync_deferred"); }
        }
        return result;
    }

    private static IEnumerable<JsonObject> ReceiptItems(JsonObject receipt)
    {
        foreach (var field in new[] { "event", "todo", "events", "todos" })
            if (receipt[field] is JsonObject item) yield return item;
            else if (receipt[field] is JsonArray items)
                foreach (var value in items.OfType<JsonObject>()) yield return value;
    }

    public void AddReceipt(JsonObject body)
    {
        body["saved_locally"] = true;
        body["app_running"] = _appRunning;
        if (_appRunning == false) body["app_reason"] = "app_not_running";
        body["last_verified_sync"] = _lastSync;
    }


    public IReadOnlyList<Category> Categories => _categories;
    public string? ResolveCategory(string? value)
    {
        if (value is null) return null;
        var byId = _categories.SingleOrDefault(c => c.ColorId == value);
        if (byId is not null) return byId.ColorId;
        var byName = _categories.Where(c => c.Name == value).ToArray();
        return byName.Length switch
        {
            1 => byName[0].ColorId,
            0 => throw new ArgumentException("Category not found. Use list_categories to select a category."),
            _ => throw new ArgumentException("Category name is ambiguous. Use its color_id.")
        };
    }

    public JsonObject Event(EventRecord value, string? occurrenceId = null)
    {
        if (isWrite) _writes[occurrenceId ?? value.EventUid] = ("event", value.EventId, value.LocalRevision, value.EventUid);
        var parent = value;
        var eventUid = value.EventUid;
        if (occurrenceId is not null)
        {
            eventUid = OccurrenceId.Parse(occurrenceId).EventUid;
            _parents ??= events.GetRecords().Where(e => e.ParentEventId is null).ToDictionary(e => e.EventUid);
            parent = _parents[eventUid];
        }
        var output = Object(new
        {
            EventId = eventUid, OccurrenceId = occurrenceId, value.Title, value.Memo,
            Category = _categories.SingleOrDefault(c => c.ColorId == value.ColorId)?.Name,
            value.StartDate, value.EndDate, value.StartTime, value.EndTime, value.IsDone, value.IsDday,
            IsRecurring = parent.RecurrenceRule is not null, RepeatEditable = RecurrenceInput.IsEditable(parent.RecurrenceRule),
            value.CreatedBy, IsDeleted = parent.DeletedAt is not null || parent.Status == "cancelled" || value.DeletedAt is not null || value.Status == "cancelled"
        });
        var checklist = Checklist.Visible(value.Checklist);
        if (checklist.Count > 0) output["checklist"] = JsonSerializer.SerializeToNode(checklist.Select(i => new { i.ItemId, i.Text, i.IsDone }), JsonOptions);
        return AddSync(output, value.PushState);
    }
    public JsonObject Occurrence(EventOccurrence value) => Event(value.Event, value.OccurrenceId);
    public JsonObject Todo(TodoRecord value)
    {
        if (isWrite) _writes[value.TodoUid] = ("todo", value.TodoId, value.LocalRevision, value.TodoUid);
        return AddSync(Object(new
        {
            TodoId = value.TodoUid, value.Title, value.Memo, value.DueDate, value.IsDone, value.CompletedAt,
            value.CreatedBy, IsDeleted = value.DeletedAt is not null
        }), value.PushState);
    }

    // SyncEngine records each row's push outcome in push_state; report it as is (§7.3).
    // Anything not yet on Google is "pending"; reason says why, when there is something to fix.
    private JsonObject AddSync(JsonObject item, string pushState)
    {
        item["sync"] = pushState == "synced" ? "synced" : "pending";
        var reason = PendingChanges.Reason(pushState, _signedIn, _appRunning);
        if (reason is not null) item["reason"] = reason;
        return item;
    }

    public static JsonObject Object(object value) => JsonSerializer.SerializeToNode(value, JsonOptions)!.AsObject();

    private static IReadOnlyList<Category> ReadCategories(Database database)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT color_id, name, color_hex FROM category ORDER BY color_id;";
        using var reader = command.ExecuteReader();
        var result = new List<Category>();
        while (reader.Read()) result.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        return result;
    }
}
