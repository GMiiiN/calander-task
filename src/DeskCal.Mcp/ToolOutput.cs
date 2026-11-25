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
    private readonly DateTimeOffset? _lastCalendarSync = isWrite ? LastCalendarSync(database) : null;
    private Dictionary<string, EventRecord>? _parents;

    private static DateTimeOffset? LastCalendarSync(Database database)
    {
        try { return EventSync.LastSyncedAt(database); }
        catch (FormatException) { return null; }
    }

    public void AddReceipt(JsonObject body)
    {
        body["saved_locally"] = true;
        body["app_running"] = _appRunning;
        if (_appRunning == false) body["app_reason"] = "app_not_running";
        body["last_verified_sync"] = new JsonObject { ["calendar"] = _lastCalendarSync?.UtcDateTime, ["tasks"] = null };
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
    public JsonObject Todo(TodoRecord value) => AddSync(Object(new
    {
        TodoId = value.TodoUid, value.Title, value.Memo, value.DueDate, value.IsDone, value.CompletedAt,
        value.CreatedBy, IsDeleted = value.DeletedAt is not null
    }), value.PushState);

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
