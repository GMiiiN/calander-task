using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using DeskCal.Core;
using Microsoft.Data.Sqlite;
using ModelContextProtocol.Protocol;

namespace DeskCal.Mcp;

public sealed class CalendarTools(Database database)
{
    private readonly EventRepository _events = new(database);
    private readonly TodoRepository _todos = new(database);

    internal CallToolResult Invoke(string name, JsonElement arguments)
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        JsonObject body;
        var isError = false;
        try
        {
            var input = new ToolInput(arguments);
            var output = new ToolOutput(database, _events);
            body = name switch
            {
                "get_agenda" => GetAgenda(input, output),
                "list_events" => ListEvents(input, output, today),
                "get_event" => GetEvent(input, output),
                "find_free_time" => FindFreeTime(input),
                "list_todos" => ListTodos(input, output),
                "list_categories" => ListCategories(input, output),
                "create_events" => CreateEvents(input, output),
                "update_event" => UpdateEvent(input, output),
                "delete_event" => SetEventDeleted(input, output, true),
                "restore_event" => SetEventDeleted(input, output, false),
                "create_todos" => CreateTodos(input, output),
                "update_todo" => UpdateTodo(input, output),
                "delete_todo" => SetTodoDeleted(input, output, true),
                "restore_todo" => SetTodoDeleted(input, output, false),
                _ => throw new ArgumentException("Unknown tool.")
            };
        }
        catch (Exception error) when (error is ArgumentException or KeyNotFoundException or InvalidOperationException or FormatException or JsonException or SqliteException)
        {
            isError = true;
            body = new JsonObject { ["error"] = error is SqliteException ? "The local database could not complete the operation. Try again." : error.Message };
        }
        body["today"] = today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        body["weekday"] = today.DayOfWeek.ToString().ToLowerInvariant();
        return new()
        {
            IsError = isError, StructuredContent = JsonSerializer.SerializeToElement(body),
            Content = [new TextContentBlock { Text = body.ToJsonString() }]
        };
    }

    private JsonObject GetAgenda(ToolInput input, ToolOutput output)
    {
        input.Only("start_date", "end_date");
        var agenda = new AgendaService(_events, _todos).GetAgenda(input.Date("start_date"), input.Date("end_date"));
        return ToolOutput.Object(new
        {
            Ddays = agenda.Ddays.Select(d => new { Event = output.Occurrence(d.Occurrence), d.DaysRemaining }),
            Groups = agenda.Groups.Select(g => new { g.Kind, Date = g.Date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), Events = g.Events.Select(output.Occurrence), Todos = g.Todos.Select(output.Todo) })
        });
    }

    private JsonObject ListEvents(ToolInput input, ToolOutput output, DateOnly today)
    {
        input.Only("start_date", "end_date", "keyword", "category", "include_deleted", "cursor");
        var start = input.Date("start_date") ?? today;
        var end = input.Date("end_date") ?? start.AddDays(30);
        var keyword = input.Text("keyword");
        var colorId = output.ResolveCategory(input.Text("category"));
        var includeDeleted = input.Boolean("include_deleted") ?? false;
        var events = _events.List(start, end, keyword, colorId, includeDeleted);
        // Bind the opaque offset to both its query and result revisions, so mutations never silently skip items.
        var fingerprint = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            start, end, keyword, colorId, includeDeleted,
            Items = events.Select(e => new { e.Event.EventUid, e.OccurrenceId, e.Start, e.End, e.Event.LocalRevision, e.Event.DeletedAt, e.Event.Status })
        })));
        var offset = 0;
        if (input.Text("cursor") is { } cursor)
        {
            try
            {
                var page = JsonSerializer.Deserialize<PageCursor>(Convert.FromBase64String(cursor), ToolOutput.JsonOptions);
                if (page is null || page.Fingerprint != fingerprint || page.Offset <= 0 || page.Offset % 200 != 0 || page.Offset >= events.Count)
                    throw new ArgumentException("Invalid or stale cursor. Repeat list_events without cursor and with the desired filters.");
                offset = page.Offset;
            }
            catch (Exception error) when (error is FormatException or JsonException)
            {
                throw new ArgumentException("Invalid cursor. Use the cursor returned by list_events.");
            }
        }
        var next = offset + 200 < events.Count
            ? Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new PageCursor(offset + 200, fingerprint), ToolOutput.JsonOptions)) : null;
        return ToolOutput.Object(new { Events = events.Skip(offset).Take(200).Select(output.Occurrence), Cursor = next });
    }

    private sealed record PageCursor(int Offset, string Fingerprint);

    private JsonObject GetEvent(ToolInput input, ToolOutput output)
    {
        input.Only("event_id", "occurrence_id");
        var (id, isOccurrence) = input.EventId();
        if (!isOccurrence)
        {
            var value = _events.Get(id);
            if (value.ParentEventId is not null) throw new ArgumentException("Use occurrence_id for an occurrence.");
            return ToolOutput.Object(new { Event = output.Event(new EventTime().Derive(value)) });
        }
        var (uid, key) = OccurrenceId.Parse(id);
        var parent = _events.Get(uid);
        if (parent.RecurrenceRule is null || parent.ParentEventId is not null) throw new ArgumentException("Occurrence requires a series.");
        var exception = _events.GetRecords().SingleOrDefault(e => e.ParentEventId == parent.EventId && e.OriginalStartKey == key);
        var occurrence = exception is null
            ? new RecurrenceExpander().GetOriginal(parent, key) ?? throw new KeyNotFoundException("Occurrence not found in series.")
            : new EventTime().ToOccurrence(exception, id);
        return ToolOutput.Object(new { Event = output.Occurrence(occurrence) });
    }

    private JsonObject FindFreeTime(ToolInput input)
    {
        input.Only("start_date", "end_date", "duration_minutes", "day_start", "day_end");
        var slots = new FreeTimeFinder(_events).Find(ToolInput.ParseDate(input.Required("start_date")), ToolInput.ParseDate(input.Required("end_date")),
            input.Integer("duration_minutes") ?? throw new ArgumentException("duration_minutes is required."), input.Time("day_start"), input.Time("day_end"));
        return ToolOutput.Object(new
        {
            Slots = slots.Select(s => new
            {
                StartDate = s.Start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), EndDate = s.End.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                StartTime = s.Start.ToString("HH:mm", CultureInfo.InvariantCulture), EndTime = s.End.ToString("HH:mm", CultureInfo.InvariantCulture)
            })
        });
    }

    private JsonObject ListTodos(ToolInput input, ToolOutput output)
    {
        input.Only("status", "keyword", "include_deleted");
        return ToolOutput.Object(new { Todos = _todos.List(input.Text("status") ?? "open", input.Text("keyword"), input.Boolean("include_deleted") ?? false).Select(output.Todo) });
    }
    private static JsonObject ListCategories(ToolInput input, ToolOutput output)
    {
        input.Only();
        return ToolOutput.Object(new { output.Categories });
    }

    private JsonObject CreateEvents(ToolInput input, ToolOutput output)
    {
        input.Only("events");
        var items = input.Batch("events").Select(item =>
        {
            item.Only("title", "start_date", "end_date", "start_time", "end_time", "memo", "category", "is_dday", "repeat");
            return new EventInput(item.Required("title"), item.Required("start_date"))
            {
                EndDate = item.Text("end_date"), StartTime = item.Text("start_time"), EndTime = item.Text("end_time"), Memo = item.Text("memo"),
                ColorId = output.ResolveCategory(item.Text("category")), IsDday = item.Boolean("is_dday") ?? false, Repeat = item.Repeat(), CreatedBy = "claude"
            };
        }).ToArray();
        return ToolOutput.Object(new { Events = _events.Create(items).Select(e => output.Event(e)) });
    }

    private JsonObject UpdateEvent(ToolInput input, ToolOutput output)
    {
        input.Only("event_id", "occurrence_id", "title", "start_date", "end_date", "start_time", "end_time", "memo", "category", "is_dday", "repeat", "is_done");
        var (id, isOccurrence) = input.EventId();
        EventRecord value;
        if (isOccurrence)
        {
            try { input.Only("occurrence_id", "is_done"); }
            catch (ArgumentException) { throw new ArgumentException("Occurrence updates support only is_done. Use event_id to edit the entire series."); }
            value = _events.SetOccurrenceDone(id, input.Boolean("is_done") ?? throw new ArgumentException("Occurrence updates only support is_done. Edit the series for other fields."));
        }
        else
        {
            value = _events.Update(id, new EventPatch
            {
                Title = input.TextPatch("title"), StartDate = input.TextPatch("start_date"), EndDate = input.TextPatch("end_date"),
                StartTime = input.TextPatch("start_time"), EndTime = input.TextPatch("end_time"), Memo = input.TextPatch("memo"),
                ColorId = input.Has("category") ? new(true, output.ResolveCategory(input.Text("category"))) : default,
                IsDday = input.BooleanPatch("is_dday"), IsDone = input.BooleanPatch("is_done"),
                Repeat = input.Has("repeat") ? new(true, input.Repeat()) : default
            });
        }
        return ToolOutput.Object(new { Event = output.Event(value, isOccurrence ? id : null) });
    }

    private JsonObject SetEventDeleted(ToolInput input, ToolOutput output, bool deleted)
    {
        input.Only("event_id", "occurrence_id");
        var (id, isOccurrence) = input.EventId();
        var value = isOccurrence
            ? deleted ? _events.DeleteOccurrence(id) : _events.RestoreOccurrence(id)
            : deleted ? _events.Delete(id) : _events.Restore(id);
        return ToolOutput.Object(new { Event = output.Event(value, isOccurrence ? id : null) });
    }

    private JsonObject CreateTodos(ToolInput input, ToolOutput output)
    {
        input.Only("todos");
        var items = input.Batch("todos").Select(item =>
        {
            item.Only("title", "due_date", "memo");
            return new TodoInput(item.Required("title"), item.Text("due_date"), item.Text("memo"), "claude");
        }).ToArray();
        return ToolOutput.Object(new { Todos = _todos.Create(items).Select(output.Todo) });
    }
    private JsonObject UpdateTodo(ToolInput input, ToolOutput output)
    {
        input.Only("todo_id", "title", "due_date", "memo", "is_done");
        var value = _todos.Update(input.Uid("todo_id"), new TodoPatch
        {
            Title = input.TextPatch("title"), DueDate = input.TextPatch("due_date"), Memo = input.TextPatch("memo"), IsDone = input.BooleanPatch("is_done")
        });
        return ToolOutput.Object(new { Todo = output.Todo(value) });
    }
    private JsonObject SetTodoDeleted(ToolInput input, ToolOutput output, bool deleted)
    {
        input.Only("todo_id");
        var id = input.Uid("todo_id");
        return ToolOutput.Object(new { Todo = output.Todo(deleted ? _todos.Delete(id) : _todos.Restore(id)) });
    }
}
