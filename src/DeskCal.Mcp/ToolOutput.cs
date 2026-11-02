using System.Text.Json;
using System.Text.Json.Nodes;
using DeskCal.Core;

namespace DeskCal.Mcp;

internal sealed record Category(string ColorId, string Name, string ColorHex);

internal sealed class ToolOutput(Database database, EventRepository events)
{
    internal static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
    private readonly IReadOnlyList<Category> _categories = ReadCategories(database);
    private readonly bool _signedIn = ReadSignedIn(database);
    private Dictionary<string, EventRecord>? _parents;

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
        var reason = pushState switch
        {
            "synced" => null,
            _ when !_signedIn => "not_signed_in",
            "failed" => "push_failed",
            "unknown" => "needs_review",
            _ => null
        };
        if (reason is not null) item["reason"] = reason;
        return item;
    }

    private static bool ReadSignedIn(Database database)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM setting WHERE setting_key = $key;";
        command.Parameters.AddWithValue("$key", ProtectedTokenStore.SettingKey);
        return command.ExecuteScalar() is not null;
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
