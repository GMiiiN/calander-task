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
    public async Task<JsonObject> SendAndVerifyAsync(JsonObject receipt, Func<Task<SyncOutcome>> send, CancellationToken token)
    {
        var sending = Task.Run(send);
        try
        {
            var outcome = await sending.WaitAsync(ReceiptWait, token);
            return Verify((JsonObject)receipt.DeepClone(), Waiting(outcome));
        }
        catch (Exception ex)
        {
            // 늦게 끝난 전송의 실패를 관찰해 둔다. 결과는 다음 조회·영수증이 행 상태로 보여 준다.
            _ = sending.ContinueWith(static t => t.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            var reason = ex is TimeoutException or OperationCanceledException ? "sync_timeout" : "sync_unavailable";
            foreach (var item in ReceiptItems(receipt).Append(receipt))
                if ((string?)item["reason"] == "app_not_running") item["reason"] = reason;
            return receipt;
        }
    }

    // 보내고도 남은 행의 이유(S4). 사용 중(다른 DeskCal이 보내는 중), 로그인 없음, 클라이언트 파일 없음, 인증 오류, 다음 차례 재시도를 구분한다.
    private string Waiting(SyncOutcome outcome) => outcome switch
    {
        SyncOutcome.Busy => "sync_busy",
        SyncOutcome.NotSignedIn => "not_signed_in",
        SyncOutcome.NoClientFile => "client_file_missing",
        _ => GoogleAccount.ReadAuthError(database) is not null ? "sign_in_error" : "retry_pending"
    };

    private JsonObject Verify(JsonObject result, string waiting)
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
            else { item["sync"] = "pending"; item["reason"] = reason ?? waiting; }
        }
        if (_renamed is not null) result["category"] = CategoryReceipt(_renamed, signedIn, waiting);
        if (_reordered is { } order) VerifyOrder(result, order.Day, order.Items, signedIn, waiting);
        return result;
    }

    private CategoryRecord? _renamed;
    public JsonObject CategoryOutput(CategoryRecord value)
    {
        _renamed = value;
        return CategoryReceipt(value, _signedIn, null);
    }

    private JsonObject CategoryReceipt(CategoryRecord requested, bool signedIn, string? waiting)
    {
        var state = SharedState(SharedSettings.CategoryPrefix + requested.ColorId, requested.ColorId);
        var actual = state.Name ?? requested.Name;
        var item = Object(new { requested.ColorId, Name = actual, requested.ColorHex });
        if (actual != requested.Name) item["requested_name"] = requested.Name;
        SetSharedSync(item, state, actual != requested.Name, signedIn, waiting);
        return item;
    }

    private (DateOnly Day, IReadOnlyList<EventOccurrence> Items)? _reordered;
    public JsonObject OrderOutput(DateOnly date, IReadOnlyList<EventOccurrence> ordered)
    {
        _reordered = (date, ordered);
        var result = new JsonObject { ["date"] = date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) };
        VerifyOrder(result, date, ordered, _signedIn, null);
        return result;
    }

    private void VerifyOrder(JsonObject item, DateOnly date, IReadOnlyList<EventOccurrence> requested, bool signedIn, string? waiting)
    {
        var state = SharedState(DayEventOrder.Prefix + (string?)item["date"]);
        var keys = JsonSerializer.SerializeToNode(requested.Select(DayEventOrder.RemoteKey));
        var differs = !JsonNode.DeepEquals(keys, state.Value);
        IEnumerable<EventOccurrence> actual = requested;
        if (differs)
        {
            item["requested_event_ids"] = JsonSerializer.SerializeToNode(requested.Select(DayEventOrder.Key));
            actual = DayEventOrder.Read(database, date, date).Sort(date, events.List(date, date).Where(o => !DayEventOrder.IsPeriod(o)));
        }
        else item.Remove("requested_event_ids");
        item["events"] = new JsonArray(actual.Select(o => (JsonNode)Object(new
        {
            EventId = o.OccurrenceId is { } id ? OccurrenceId.Parse(id).EventUid : o.Event.EventUid, o.OccurrenceId, o.Event.Title,
            o.Event.StartTime, o.Event.IsDone
        })).ToArray());
        SetSharedSync(item, state, differs, signedIn, waiting);
    }

    private sealed record SharedReceiptState(bool Dirty, JsonNode? Value, string? Name, bool Conflict);

    private SharedReceiptState SharedState(string key, string? colorId = null)
    {
        using var connection = database.OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT s.is_dirty, s.value_json, c.name,
                   EXISTS(SELECT 1 FROM sync_conflict WHERE item_kind='settings' AND item_uid=$key AND conflict_id>$before)
            FROM (SELECT 1) LEFT JOIN shared_entry s ON s.entry_key=$key LEFT JOIN category c ON c.color_id=$color
            """;
        cmd.Parameters.AddWithValue("$key", key);
        cmd.Parameters.AddWithValue("$color", (object?)colorId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$before", _conflictBefore);
        using var reader = cmd.ExecuteReader();
        reader.Read();
        return new(!reader.IsDBNull(0) && reader.GetBoolean(0), reader.IsDBNull(1) ? null : JsonNode.Parse(reader.GetString(1)),
            reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetBoolean(3));
    }

    private void SetSharedSync(JsonObject item, SharedReceiptState state, bool differs, bool signedIn, string? waiting)
    {
        item["sync"] = state.Dirty || differs ? "pending" : "synced";
        string? reason = null;
        if (differs) reason = state.Conflict ? "conflict" : "newer_revision";
        else if (state.Dirty) reason = PendingChanges.Reason("pending", signedIn, waiting is null ? _appRunning : null)
            ?? SharedSettingsSync.UnavailableReason(database) ?? waiting;
        if (reason is null) item.Remove("reason");
        else item["reason"] = reason;
    }

    private static IEnumerable<JsonObject> ReceiptItems(JsonObject receipt)
    {
        foreach (var field in new[] { "event", "todo", "events", "todos", "category" })
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
