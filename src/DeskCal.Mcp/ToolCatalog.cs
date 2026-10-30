using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DeskCal.Mcp;

internal static class ToolCatalog
{
    private const string RELATIVE_DATES = " Call get_agenda first if you need today's date before calculating relative dates.";
    private const string DELETE_GUIDANCE = " Look up the ID before deleting. If titles are ambiguous, ask the user which item they mean.";
    private const string SERIES_GUIDANCE = " Before editing or deleting a recurring event, confirm with the user whether they mean this occurrence or the entire series.";

    public static IEnumerable<McpServerTool> Create(CalendarTools service)
    {
        yield return Define("get_agenda", "Use get_agenda for briefings and summaries. Returns sidebar groups and D-days." + RELATIVE_DATES,
            Object(new() { ["start_date"] = Date(), ["end_date"] = Date() }), true);
        yield return Define("list_events", "List expanded events, including overlaps. Defaults to today through +30 days; at most 366 days. Pages contain at most 200 events. Pass cursor with the same filters; restart if results change." + RELATIVE_DATES,
            Object(new() { ["start_date"] = Date(), ["end_date"] = Date(), ["keyword"] = String(), ["category"] = Category(), ["include_deleted"] = Boolean(), ["cursor"] = String() }), true);
        yield return Define("get_event", "Get an event or occurrence, including deleted items. Supply exactly one ID.", Ids(), true);
        yield return Define("find_free_time", "Find all free intervals of at least duration_minutes within each day's window (default 09:00–22:00), over at most 31 days. All-day and transparent events do not count as busy. Intervals are half-open [start, end)." + RELATIVE_DATES,
            Object(new() { ["start_date"] = Date(), ["end_date"] = Date(), ["duration_minutes"] = Integer(), ["day_start"] = Time(), ["day_end"] = Time() }, "start_date", "end_date", "duration_minutes"), true);
        yield return Define("list_todos", "List todos, defaulting to open items. Deleted items are excluded unless requested.",
            Object(new() { ["status"] = Enum("open", "done", "all"), ["keyword"] = String(), ["include_deleted"] = Boolean() }), true);
        yield return Define("list_categories", "List category color IDs, names and colors.", Object(new()), true);
        yield return Define("create_events", "Create 1–50 events atomically in the local database. Dates and HH:mm times are local. A missing end_time defaults to one hour after start, including midnight rollover; multi-day timed events require end_time. Sync is reported per item." + RELATIVE_DATES,
            Object(new() { ["events"] = Batch(Object(EventFields(false), "title", "start_date")) }, "events"));
        var eventPatch = EventFields(true);
        eventPatch["event_id"] = String();
        eventPatch["occurrence_id"] = String();
        yield return Define("update_event", "Update an event. Omitted fields are unchanged; null clears nullable fields; end_time null resets the end to one hour after start. For occurrence_id only is_done is allowed. Supply exactly one ID." + SERIES_GUIDANCE,
            WithIds(Object(eventPatch)), idempotent: true);
        yield return Define("delete_event", "Soft-delete an event series or a single occurrence. Supply exactly one ID." + DELETE_GUIDANCE + SERIES_GUIDANCE, Ids(), destructive: true);
        yield return Define("restore_event", "Restore a series (keeping individually cancelled occurrences) or restore only one occurrence. Supply exactly one ID.", Ids());
        yield return Define("create_todos", "Create 1–50 todos atomically in the local database. Sync is reported per item." + RELATIVE_DATES,
            Object(new() { ["todos"] = Batch(Object(TodoFields(false), "title")) }, "todos"));
        var todoPatch = TodoFields(true);
        todoPatch["todo_id"] = String();
        yield return Define("update_todo", "Update a todo. Omitted fields are unchanged; null clears due_date or memo.", Object(todoPatch, "todo_id"), idempotent: true);
        yield return Define("delete_todo", "Soft-delete a todo." + DELETE_GUIDANCE, Object(new() { ["todo_id"] = String() }, "todo_id"), destructive: true);
        yield return Define("restore_todo", "Restore a soft-deleted todo.", Object(new() { ["todo_id"] = String() }, "todo_id"));

        McpServerTool Define(string name, string description, JsonObject schema, bool readOnly = false, bool idempotent = false, bool destructive = false) =>
            new CalendarTool(new Tool
            {
                Name = name, Description = description, InputSchema = JsonSerializer.SerializeToElement(schema),
                Annotations = new() { ReadOnlyHint = readOnly, IdempotentHint = idempotent, DestructiveHint = destructive, OpenWorldHint = false }
            }, service);
    }

    private static Dictionary<string, JsonNode?> EventFields(bool patch)
    {
        var result = new Dictionary<string, JsonNode?>
        {
            ["title"] = String(), ["start_date"] = Date(), ["end_date"] = Date(!patch),
            ["start_time"] = Time(true), ["end_time"] = Time(true), ["memo"] = String(true),
            ["category"] = Category(), ["is_dday"] = Boolean(), ["repeat"] = Repeat()
        };
        if (patch) result["is_done"] = Boolean();
        return result;
    }

    private static Dictionary<string, JsonNode?> TodoFields(bool patch)
    {
        var result = new Dictionary<string, JsonNode?> { ["title"] = String(), ["due_date"] = Date(true), ["memo"] = String(true) };
        if (patch) result["is_done"] = Boolean();
        return result;
    }

    private static JsonObject Repeat()
    {
        var value = Object(new()
        {
            ["frequency"] = Enum("daily", "weekly", "monthly", "yearly"), ["interval"] = Integer(),
            ["weekdays"] = new JsonObject { ["type"] = "array", ["items"] = Enum("sunday", "monday", "tuesday", "wednesday", "thursday", "friday", "saturday") },
            ["until"] = Date(true), ["count"] = Integer(true)
        }, "frequency");
        value["type"] = new JsonArray("object", "null");
        value["description"] = "Weekly weekdays must include the start weekday. Use until or count, never both. Null removes recurrence.";
        return value;
    }

    private static JsonObject Ids() => WithIds(Object(new() { ["event_id"] = String(), ["occurrence_id"] = String() }));
    private static JsonObject WithIds(JsonObject schema)
    {
        schema["oneOf"] = new JsonArray(new JsonObject { ["required"] = new JsonArray("event_id") }, new JsonObject { ["required"] = new JsonArray("occurrence_id") });
        return schema;
    }
    private static JsonObject Object(Dictionary<string, JsonNode?> properties, params string[] required) => new()
    {
        ["type"] = "object", ["properties"] = new JsonObject(properties), ["additionalProperties"] = false,
        ["required"] = new JsonArray(required.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray())
    };
    private static JsonObject String(bool nullable = false) => new() { ["type"] = nullable ? new JsonArray("string", "null") : JsonValue.Create("string") };
    private static JsonObject Date(bool nullable = false) { var value = String(nullable); value["format"] = "date"; value["description"] = "Local YYYY-MM-DD date."; return value; }
    private static JsonObject Time(bool nullable = false) { var value = String(nullable); value["pattern"] = "^([01][0-9]|2[0-3]):[0-5][0-9]$"; return value; }
    private static JsonObject Category() { var value = String(true); value["description"] = "Existing category color_id or exact name; null clears it."; return value; }
    private static JsonObject Boolean() => new() { ["type"] = "boolean" };
    private static JsonObject Integer(bool nullable = false) => new() { ["type"] = nullable ? new JsonArray("integer", "null") : JsonValue.Create("integer"), ["minimum"] = 1 };
    private static JsonObject Enum(params string[] values) => new() { ["type"] = "string", ["enum"] = new JsonArray(values.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray()) };
    private static JsonObject Batch(JsonObject item) => new() { ["type"] = "array", ["minItems"] = 1, ["maxItems"] = 50, ["items"] = item };

    private sealed class CalendarTool(Tool definition, CalendarTools service) : McpServerTool
    {
        public override Tool ProtocolTool => definition;
        public override IReadOnlyList<object> Metadata => [];
        public override ValueTask<CallToolResult> InvokeAsync(RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var arguments = JsonSerializer.SerializeToElement(request.Params?.Arguments ?? new Dictionary<string, JsonElement>());
            return ValueTask.FromResult(service.Invoke(definition.Name, arguments));
        }
    }
}
