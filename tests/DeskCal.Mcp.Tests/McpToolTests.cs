using System.Globalization;
using System.Text.Json;
using DeskCal.Core;
using DeskCal.Mcp;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace DeskCal.Mcp.Tests;

public sealed class McpToolTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "deskcal-mcp-" + Guid.NewGuid().ToString("N"));
    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromSeconds(60));
    private McpClient _client = null!;
    private Database _database = null!;
    private EventRepository Events => new(_database);
    private TodoRepository Todos => new(_database);

    public async Task InitializeAsync()
    {
        var path = Path.Combine(_directory, "nested", "deskcal.db");
        _client = await McpClient.CreateAsync(new StdioClientTransport(new()
        {
            Command = "dotnet", Arguments = [typeof(CalendarTools).Assembly.Location],
            EnvironmentVariables = new Dictionary<string, string?> { ["DESKCAL_DB"] = path }
        }), cancellationToken: _timeout.Token);
        Assert.True(File.Exists(path)); // The executable creates the directory and initializes the DB.
        _database = new Database(path);
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO category(color_id,name,color_hex) VALUES ('1','개인','#123456'),('2','업무','#654321');";
        command.ExecuteNonQuery();
    }

    public async Task DisposeAsync()
    {
        if (_client is not null) await _client.DisposeAsync();
        _timeout.Dispose();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }

    private async Task<JsonElement> Call(string name, object? args = null, bool error = false)
    {
        var json = args is string raw ? raw : JsonSerializer.Serialize(args ?? new { });
        var arguments = JsonSerializer.Deserialize<Dictionary<string, object?>>(json)!;
        var result = await _client.CallToolAsync(name, arguments, cancellationToken: _timeout.Token);
        Assert.Equal(error, result.IsError == true);
        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
        var body = JsonDocument.Parse(text).RootElement.Clone();
        Assert.Equal(body.GetRawText(), result.StructuredContent!.Value.GetRawText());
        var today = DateOnly.ParseExact(body.GetProperty("today").GetString()!, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        Assert.Equal(today.DayOfWeek.ToString().ToLowerInvariant(), body.GetProperty("weekday").GetString());
        Assert.InRange(today.DayNumber, DateOnly.FromDateTime(DateTime.Now).DayNumber - 1, DateOnly.FromDateTime(DateTime.Now).DayNumber);
        AssertSnakeCase(body);
        Assert.DoesNotContain("google_", text);
        if (error)
        {
            Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("error").GetString()));
            Assert.DoesNotContain("Exception", text);
            Assert.DoesNotContain(" at DeskCal", text);
        }
        return body;
    }

    private static void AssertSnakeCase(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
            foreach (var property in value.EnumerateObject())
            {
                Assert.Matches("^[a-z][a-z0-9_]*$", property.Name);
                AssertSnakeCase(property.Value);
            }
        if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) AssertSnakeCase(item);
    }
    private static void Pending(JsonElement value)
    {
        Assert.Equal("pending", value.GetProperty("sync").GetString());
        Assert.Equal("not_signed_in", value.GetProperty("reason").GetString());
    }
    private static string Id(JsonElement value, string field = "event_id") => value.GetProperty(field).GetString()!;

    [Fact]
    public async Task OnlySuccessfulWritesSignalTheApp()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var signal = new ChangeSignal();
        while (signal.Handle.WaitOne(0)) { }
        ChangeSignal.Notify();
        Assert.True(signal.Handle.WaitOne(TimeSpan.FromSeconds(1)));
        Assert.False(signal.Handle.WaitOne(0));

        async Task<JsonElement> Check(string name, object args, bool error = false, bool writes = true)
        {
            var result = await Call(name, args, error);
            Assert.Equal(writes && !error, signal.Handle.WaitOne(0));
            return result;
        }

        var createdEvent = await Check("create_events", new { events = new[] { new { title = "signal", start_date = "2026-11-02" } } });
        var eventId = Id(createdEvent.GetProperty("events")[0]);
        await Check("update_event", new { event_id = eventId, title = "updated" });
        await Check("delete_event", new { event_id = eventId });
        await Check("restore_event", new { event_id = eventId });
        var createdTodo = await Check("create_todos", new { todos = new[] { new { title = "signal" } } });
        var todoId = Id(createdTodo.GetProperty("todos")[0], "todo_id");
        await Check("update_todo", new { todo_id = todoId, is_done = true });
        await Check("delete_todo", new { todo_id = todoId });
        await Check("restore_todo", new { todo_id = todoId });

        await Check("create_events", new { events = new[] { new { title = "bad", start_date = "2026-02-30" } } }, error: true);
        await Check("create_todos", new { todos = new[] { new { title = "bad", due_date = "2026-02-30" } } }, error: true);
        foreach (var name in new[] { "update_event", "delete_event", "restore_event" })
            await Check(name, new { event_id = "00000000000000000000000000000000" }, error: true);
        foreach (var name in new[] { "update_todo", "delete_todo", "restore_todo" })
            await Check(name, new { todo_id = "00000000000000000000000000000000" }, error: true);
        await Check("list_events", new { start_date = "2026-11-02", end_date = "2026-11-02" }, writes: false);
        await Check("list_todos", new { }, writes: false);
    }

    [Fact]
    public async Task DiscoversExactlyFourteenToolsWithAnnotationsAndEnums()
    {
        var tools = await _client.ListToolsAsync(cancellationToken: _timeout.Token);
        Assert.Equal(14, tools.Count);
        var reads = new[] { "get_agenda", "list_events", "get_event", "find_free_time", "list_todos", "list_categories" };
        foreach (var tool in tools)
        {
            Assert.Equal(reads.Contains(tool.Name), tool.ProtocolTool.Annotations!.ReadOnlyHint);
            Assert.Equal(tool.Name.StartsWith("update_"), tool.ProtocolTool.Annotations.IdempotentHint);
            Assert.Equal(tool.Name.StartsWith("delete_"), tool.ProtocolTool.Annotations.DestructiveHint);
        }
        var schema = tools.Single(t => t.Name == "create_events").ProtocolTool.InputSchema;
        var repeat = schema.GetProperty("properties").GetProperty("events").GetProperty("items").GetProperty("properties").GetProperty("repeat").GetProperty("properties");
        Assert.Equal(new[] { "daily", "weekly", "monthly", "yearly" }, repeat.GetProperty("frequency").GetProperty("enum").EnumerateArray().Select(x => x.GetString()));
        Assert.Contains("monday", repeat.GetProperty("weekdays").GetProperty("items").GetProperty("enum").EnumerateArray().Select(x => x.GetString()));
        Assert.Contains("transparent", tools.Single(t => t.Name == "find_free_time").Description);
        Assert.Contains("get_agenda", tools.Single(t => t.Name == "create_events").Description);
        Assert.Contains("entire series", tools.Single(t => t.Name == "delete_event").Description);
    }

    [Fact]
    public async Task MilestoneUsesAllFourteenToolsThroughStdio()
    {
        var briefing = await Call("get_agenda");
        var today = DateOnly.Parse(briefing.GetProperty("today").GetString()!, CultureInfo.InvariantCulture);
        var tomorrow = today.AddDays(1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var categories = await Call("list_categories");
        Assert.Equal(2, categories.GetProperty("categories").GetArrayLength());
        var created = await Call("create_events", new { events = new[] { new { title = "치과", start_date = tomorrow, start_time = "15:00", category = "개인" } } });
        var dental = created.GetProperty("events")[0];
        Pending(dental);
        Assert.Equal("16:00", dental.GetProperty("end_time").GetString());
        Assert.Equal("claude", dental.GetProperty("created_by").GetString());
        Assert.Equal("개인", dental.GetProperty("category").GetString());
        var id = Id(dental);
        var stored = Events.Get(id);
        Assert.True(stored.IsDirty);
        Assert.Equal(1, stored.LocalRevision);
        var listed = await Call("list_events");
        Assert.Equal(id, Id(Assert.Single(listed.GetProperty("events").EnumerateArray())));
        Assert.Equal(id, Id((await Call("get_event", new { event_id = id })).GetProperty("event")));
        Pending((await Call("update_event", new { event_id = id, memo = "예약 확인" })).GetProperty("event"));
        var agenda = await Call("get_agenda");
        Assert.Contains(agenda.GetProperty("groups").EnumerateArray(), g => g.GetProperty("events").EnumerateArray().Any(e => Id(e) == id));
        var daysUntilMonday = ((int)DayOfWeek.Monday - (int)today.DayOfWeek + 7) % 7;
        var nextMonday = today.AddDays(daysUntilMonday == 0 ? 7 : daysUntilMonday);
        var free = await Call("find_free_time", new { start_date = nextMonday.ToString("yyyy-MM-dd"), end_date = nextMonday.AddDays(6).ToString("yyyy-MM-dd"), duration_minutes = 120 });
        Assert.NotEmpty(free.GetProperty("slots").EnumerateArray());
        var batch = await Call("create_todos", new { todos = new[] { new { title = "준비" }, new { title = "예약" }, new { title = "확인" } } });
        Assert.Equal(3, batch.GetProperty("todos").GetArrayLength());
        foreach (var todo in batch.GetProperty("todos").EnumerateArray()) { Pending(todo); Assert.Equal("claude", todo.GetProperty("created_by").GetString()); }
        var todoId = Id(batch.GetProperty("todos")[0], "todo_id");
        Pending((await Call("update_todo", new { todo_id = todoId, is_done = true })).GetProperty("todo"));
        Assert.Equal(2, (await Call("list_todos")).GetProperty("todos").GetArrayLength());
        Pending((await Call("delete_todo", new { todo_id = todoId })).GetProperty("todo"));
        Assert.True(Todos.Get(todoId).DeletedAt is not null);
        Pending((await Call("restore_todo", new { todo_id = todoId })).GetProperty("todo"));
        Assert.Null(Todos.Get(todoId).DeletedAt);
        Pending((await Call("delete_event", new { event_id = id })).GetProperty("event"));
        Assert.Empty((await Call("list_events")).GetProperty("events").EnumerateArray());
        Assert.True((await Call("get_event", new { event_id = id })).GetProperty("event").GetProperty("is_deleted").GetBoolean());
        Pending((await Call("restore_event", new { event_id = id })).GetProperty("event"));
        Assert.Single((await Call("list_events")).GetProperty("events").EnumerateArray());
    }

    [Fact]
    public async Task OmissionPreservesValuesAndExplicitNullClearsThem()
    {
        var value = (await Call("create_events", """{"events":[{"title":"keep","start_date":"2026-11-02","start_time":"15:00","memo":"memo","category":"1","repeat":{"frequency":"daily","count":3}}]}""")).GetProperty("events")[0];
        var id = Id(value);
        await Call("update_event", new { event_id = id, title = "new" });
        var kept = Events.Get(id);
        Assert.Equal("memo", kept.Memo);
        Assert.Equal("1", kept.ColorId);
        Assert.Equal("15:00", kept.StartTime);
        Assert.NotNull(kept.RecurrenceRule);
        await Call("update_event", new { event_id = id, memo = (string?)null, category = (string?)null, repeat = (object?)null, start_time = (string?)null, end_time = (string?)null });
        var cleared = Events.Get(id);
        Assert.Equal("", cleared.Memo);
        Assert.Null(cleared.ColorId);
        Assert.Null(cleared.RecurrenceRule);
        Assert.Null(cleared.StartTime);
        Assert.Null(cleared.EndTime);
        await Call("update_event", new { event_id = id, title = (string?)null }, true);
        await Call("update_event", new { event_id = id, is_done = (bool?)null }, true);
        var todo = Todos.Create([new("todo", "2026-11-03", "memo")])[0];
        await Call("update_todo", new { todo_id = todo.TodoUid, title = "new" });
        Assert.Equal("2026-11-03", Todos.Get(todo.TodoUid).DueDate);
        Assert.Equal("memo", Todos.Get(todo.TodoUid).Memo);
        await Call("update_todo", new { todo_id = todo.TodoUid, due_date = (string?)null, memo = (string?)null });
        Assert.Null(Todos.Get(todo.TodoUid).DueDate);
        Assert.Equal("", Todos.Get(todo.TodoUid).Memo);
    }

    [Fact]
    public async Task RecurrenceCompletionDeletionAndRestoreKeepStablePublicIds()
    {
        var created = await Call("create_events", """{"events":[{"title":"series","start_date":"2026-11-02","start_time":"15:00","repeat":{"frequency":"weekly","weekdays":["monday","wednesday"],"count":3}}]}""");
        var id = Id(created.GetProperty("events")[0]);
        var list = await Call("list_events", new { start_date = "2026-11-02", end_date = "2026-11-10" });
        Assert.Equal(3, list.GetProperty("events").GetArrayLength());
        var occurrenceId = list.GetProperty("events")[0].GetProperty("occurrence_id").GetString()!;
        var original = (await Call("get_event", new { occurrence_id = occurrenceId })).GetProperty("event");
        Assert.Equal(id, Id(original));
        Assert.True(original.GetProperty("is_recurring").GetBoolean());
        Assert.False(original.GetProperty("is_done").GetBoolean());
        var changed = (await Call("update_event", new { occurrence_id = occurrenceId, is_done = true })).GetProperty("event");
        Pending(changed);
        Assert.Equal(id, Id(changed));
        Assert.True(changed.GetProperty("is_recurring").GetBoolean());
        Assert.True(changed.GetProperty("is_done").GetBoolean());
        await Call("update_event", new { occurrence_id = occurrenceId, title = "forbidden" }, true);
        await Call("update_event", new { event_id = id, repeat = (object?)null }, true);
        await Call("delete_event", new { occurrence_id = occurrenceId });
        Assert.True((await Call("get_event", new { occurrence_id = occurrenceId })).GetProperty("event").GetProperty("is_deleted").GetBoolean());
        Assert.Equal(2, (await Call("list_events", new { start_date = "2026-11-02", end_date = "2026-11-10" })).GetProperty("events").GetArrayLength());
        var deleted = await Call("list_events", new { start_date = "2026-11-02", end_date = "2026-11-10", include_deleted = true });
        Assert.Equal(3, deleted.GetProperty("events").GetArrayLength());
        Assert.Equal(occurrenceId, deleted.GetProperty("events")[0].GetProperty("occurrence_id").GetString());
        await Call("delete_event", new { event_id = id });
        Assert.True((await Call("get_event", new { occurrence_id = occurrenceId })).GetProperty("event").GetProperty("is_deleted").GetBoolean());
        await Call("restore_event", new { event_id = id });
        Assert.Equal(2, (await Call("list_events", new { start_date = "2026-11-02", end_date = "2026-11-10" })).GetProperty("events").GetArrayLength());
        Pending((await Call("restore_event", new { occurrence_id = occurrenceId })).GetProperty("event"));
        var restored = (await Call("get_event", new { occurrence_id = occurrenceId })).GetProperty("event");
        Assert.False(restored.GetProperty("is_deleted").GetBoolean());
        Assert.True(restored.GetProperty("is_done").GetBoolean());
        Assert.Equal(id, Id(restored));
        var missingOccurrence = id + "~2026-12-01T00:00:00Z";
        await Call("get_event", new { occurrence_id = missingOccurrence }, true);
        await Call("delete_event", new { occurrence_id = missingOccurrence }, true);
    }

    [Fact]
    public async Task PagesMoreThanTwoHundredEventsOnOneDayWithoutOmissions()
    {
        for (var batch = 0; batch < 5; batch++)
            await Call("create_events", new { events = Enumerable.Range(batch * 50, 50).Select(i => new { title = "event " + i, start_date = "2026-11-02" }) });
        var first = await Call("list_events", new { start_date = "2026-11-02", end_date = "2026-11-02" });
        Assert.Equal(200, first.GetProperty("events").GetArrayLength());
        var cursor = first.GetProperty("cursor").GetString()!;
        var second = await Call("list_events", new { start_date = "2026-11-02", end_date = "2026-11-02", cursor });
        Assert.Equal(50, second.GetProperty("events").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, second.GetProperty("cursor").ValueKind);
        Assert.Equal(250, first.GetProperty("events").EnumerateArray().Concat(second.GetProperty("events").EnumerateArray()).Select(e => Id(e)).Distinct().Count());
        await Call("list_events", new { start_date = "2026-11-02", end_date = "2026-11-02", keyword = "different", cursor }, true);
        await Call("update_event", new { event_id = Id(first.GetProperty("events")[0]), title = "changed" });
        await Call("list_events", new { start_date = "2026-11-02", end_date = "2026-11-02", cursor }, true);
    }

    [Theory]
    [InlineData("create_events", "{\"events\":[{\"title\":\"valid\",\"start_date\":\"2026-11-02\"},{\"title\":\"bad\",\"start_date\":\"2026-02-30\"}]}")]
    [InlineData("create_todos", "{\"todos\":[{\"title\":\"valid\"},{\"title\":\"bad\",\"due_date\":\"2026-02-30\"}]}")]
    public async Task InvalidSecondItemRollsBackTheWholeBatch(string tool, string args)
    {
        await Call(tool, args, true);
        Assert.Empty(Events.GetRecords());
        Assert.Empty(Todos.List("all", includeDeleted: true));
    }

    [Fact]
    public async Task OversizedBatchesAndConflictingIdsCannotWrite()
    {
        await Call("create_events", new { events = Enumerable.Range(0, 51).Select(i => new { title = "event", start_date = "2026-11-02" }) }, true);
        await Call("create_todos", new { todos = Enumerable.Range(0, 51).Select(i => new { title = "todo" }) }, true);
        Assert.Empty(Events.GetRecords());
        Assert.Empty(Todos.List());
        var parent = Events.Create([new("series", "2026-11-02") { Repeat = new("daily", Count: 2) }])[0];
        await Call("delete_event", new { event_id = parent.EventUid, occurrence_id = parent.EventUid + "~2026-11-02" }, true);
        Assert.Null(Events.Get(parent.EventUid).DeletedAt);
        Assert.Single(Events.GetRecords());
        var failure = await Call("update_event", new { occurrence_id = parent.EventUid + "~2026-11-02", title = "forbidden" }, true);
        Assert.Contains("only is_done", failure.GetProperty("error").GetString());
        Assert.Single(Events.GetRecords());
    }

    [Theory]
    [InlineData("get_agenda", "{\"start_date\":\"2026-02-30\"}")]
    [InlineData("list_events", "{\"start_date\":\"2026-01-01\",\"end_date\":\"2027-01-02\"}")]
    [InlineData("list_events", "{\"cursor\":\"garbage\"}")]
    [InlineData("list_events", "{\"category\":\"missing\"}")]
    [InlineData("get_event", "{}")]
    [InlineData("get_event", "{\"event_id\":\"bad\"}")]
    [InlineData("get_event", "{\"event_id\":\"00000000000000000000000000000000\"}")]
    [InlineData("update_event", "{\"event_id\":\"00000000000000000000000000000000\",\"title\":\"x\"}")]
    [InlineData("delete_event", "{\"event_id\":\"00000000000000000000000000000000\"}")]
    [InlineData("restore_event", "{\"event_id\":\"00000000000000000000000000000000\"}")]
    [InlineData("update_todo", "{\"todo_id\":\"00000000000000000000000000000000\"}")]
    [InlineData("delete_todo", "{\"todo_id\":\"00000000000000000000000000000000\"}")]
    [InlineData("restore_todo", "{\"todo_id\":\"00000000000000000000000000000000\"}")]
    [InlineData("find_free_time", "{\"start_date\":\"2026-11-01\",\"end_date\":\"2026-12-02\",\"duration_minutes\":120}")]
    [InlineData("find_free_time", "{\"start_date\":\"2026-11-01\",\"end_date\":\"2026-11-01\",\"duration_minutes\":0}")]
    [InlineData("find_free_time", "{\"start_date\":\"2026-11-01\",\"end_date\":\"2026-11-01\",\"duration_minutes\":120,\"day_start\":\"25:00\"}")]
    [InlineData("list_todos", "{\"status\":\"OPEN\"}")]
    [InlineData("list_categories", "{\"unexpected\":true}")]
    [InlineData("create_events", "{\"events\":[]}")]
    [InlineData("create_todos", "{\"todos\":[]}")]
    [InlineData("create_events", "{\"events\":[{\"title\":\"x\",\"start_date\":\"2026-11-02\",\"repeat\":{\"frequency\":\"WEEKLY\"}}]}")]
    [InlineData("create_events", "{\"events\":[{\"title\":\"x\",\"start_date\":\"2026-11-02\",\"repeat\":{\"frequency\":\"weekly\",\"weekdays\":[\"Monday\"]}}]}")]
    public async Task InvalidInputsReturnReadableErrorsAndDateContext(string tool, string args) => await Call(tool, args, true);

    [Fact]
    public async Task FreeTimeUsesAllEventsAndExcludesAllDayAndTransparent()
    {
        Events.Create([new("all-day", "2026-11-02"), new("transparent", "2026-11-02") { StartTime = "09:00", EndTime = "22:00", IsBusy = false }]);
        for (var batch = 0; batch < 4; batch++)
            Events.Create(Enumerable.Range(0, 50).Select(i => new EventInput("busy", "2026-11-02") { StartTime = "09:00", EndTime = "10:00" }).ToArray());
        Events.Create([new("late busy", "2026-11-02") { StartTime = "12:00", EndTime = "13:00" }]);
        var result = await Call("find_free_time", new { start_date = "2026-11-02", end_date = "2026-11-02", duration_minutes = 120 });
        var slots = result.GetProperty("slots");
        Assert.Equal(2, slots.GetArrayLength());
        Assert.Equal("10:00", slots[0].GetProperty("start_time").GetString());
        Assert.Equal("12:00", slots[0].GetProperty("end_time").GetString());
        Assert.Equal("13:00", slots[1].GetProperty("start_time").GetString());
        Assert.Equal("22:00", slots[1].GetProperty("end_time").GetString());
    }

    [Fact]
    public async Task DefaultHourRollsOverMidnightAndCategoryFilteringUsesNamesOrIds()
    {
        var result = await Call("create_events", """{"events":[{"title":"night","start_date":"2026-11-02","start_time":"23:30","category":"1"}]}""");
        var value = result.GetProperty("events")[0];
        Assert.Equal("2026-11-03", value.GetProperty("end_date").GetString());
        Assert.Equal("00:30", value.GetProperty("end_time").GetString());
        var listed = await Call("list_events", new { start_date = "2026-11-03", end_date = "2026-11-03", category = "개인", keyword = "NIGHT" });
        Assert.Single(listed.GetProperty("events").EnumerateArray());
        await Call("create_events", """{"events":[{"title":"bad","start_date":"2026-11-02","end_date":"2026-11-03","start_time":"15:00"}]}""", true);
    }
}
