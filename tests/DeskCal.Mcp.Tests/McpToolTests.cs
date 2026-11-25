using System.Diagnostics;
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
        _client = await StartServer(path);
        Assert.True(File.Exists(path)); // The executable creates the directory and initializes the DB.
        _database = new Database(path);
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT OR REPLACE INTO category(color_id,name,color_hex) VALUES ('1','개인','#123456'),('2','업무','#654321');";
        command.ExecuteNonQuery();
    }

    private Task<McpClient> StartServer(string path) => McpClient.CreateAsync(new StdioClientTransport(new()
    {
        Command = "dotnet", Arguments = [typeof(CalendarTools).Assembly.Location],
        // 클라이언트 파일은 DB 옆에서 찾는다. 임시 폴더에는 없으므로 서버는 Google에 연결하지 않는다.
        EnvironmentVariables = new Dictionary<string, string?> { ["DESKCAL_DB"] = path }
    }), cancellationToken: _timeout.Token);

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
        var write = name is not ("get_agenda" or "list_events" or "get_event" or "find_free_time" or "list_todos" or "list_categories");
        Assert.Equal(write && !error, body.TryGetProperty("saved_locally", out var saved));
        Assert.Equal(write && !error, body.TryGetProperty("app_running", out _));
        Assert.Equal(write && !error, body.TryGetProperty("last_verified_sync", out _));
        if (write && !error) Assert.True(saved.GetBoolean());
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
    public async Task TwoServerProcessesPublishOneVerifiedBackupOnUpgrade()
    {
        using var connection = _database.OpenConnection();
        using var schema = connection.CreateCommand();
        schema.CommandText = "ALTER TABLE event DROP COLUMN sync_base; ALTER TABLE todo DROP COLUMN sync_base; ALTER TABLE sync_conflict DROP COLUMN detail_json; " +
            "DROP TABLE shared_entry; ALTER TABLE sync_state DROP COLUMN last_error; " +
            "DROP TABLE task_attempt; DROP TABLE day_event_order; ALTER TABLE event DROP COLUMN checklist; PRAGMA user_version=1;";
        schema.ExecuteNonQuery();
        var path = Path.Combine(_directory, "nested", "deskcal.db");
        var clients = await Task.WhenAll(StartServer(path), StartServer(path));
        try
        {
            Assert.Single(Directory.GetFiles(path + ".backups"));
            schema.CommandText = "PRAGMA user_version";
            Assert.Equal((long)Database.LatestVersion, schema.ExecuteScalar());
        }
        finally
        {
            foreach (var client in clients) await client.DisposeAsync();
        }
    }

    [Fact]
    public async Task LockedDatabaseReturnsSqliteCodesAndRetryGuidance()
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "BEGIN EXCLUSIVE;";
        command.ExecuteNonQuery();
        try
        {
            Assert.Empty((await Call("list_todos")).GetProperty("todos").EnumerateArray());
            var result = await Call("create_todos", new { todos = new[] { new { title = "locked" } } }, error: true);
            Assert.Equal(_database.PathFingerprint, result.GetProperty("db_fingerprint").GetString());
            Assert.Equal("begin", result.GetProperty("db_stage").GetString());
            Assert.InRange(result.GetProperty("db_elapsed_ms").GetInt64(), 4000, 15000);
            var error = result.GetProperty("error").GetString();
            Assert.Contains("SQLITE_BUSY(5)", error);
            Assert.Contains("SqliteExtendedErrorCode=5", error);
            Assert.Contains("another program", error);
            Assert.Contains("DeskCal", error);
            Assert.Contains("Try again shortly", error);
        }
        finally
        {
            command.CommandText = "ROLLBACK;";
            command.ExecuteNonQuery();
        }
        Assert.Empty(Todos.List());
        var created = await Call("create_todos", new { todos = new[] { new { title = "저장 확인" } } });
        var uid = Id(created.GetProperty("todos")[0], "todo_id");
        Assert.Equal(uid, Assert.Single(Todos.List()).TodoUid);
        await _client.DisposeAsync();
        _client = await StartServer(Path.Combine(_directory, "nested", "deskcal.db"));
        var reopened = await Call("list_todos", new { status = "all", include_deleted = true });
        Assert.Equal(uid, Id(Assert.Single(reopened.GetProperty("todos").EnumerateArray()), "todo_id"));
    }

    [Fact]
    public async Task ConstraintFailureReturnsExtendedCodeWithoutExceptionDetails()
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TRIGGER reject_todo BEFORE INSERT ON todo
            BEGIN SELECT RAISE(ABORT, 'Exception at DeskCal'); END;
            """;
        command.ExecuteNonQuery();
        var result = await Call("create_todos", new { todos = new[] { new { title = "rejected" } } }, error: true);
        var error = result.GetProperty("error").GetString();
        Assert.Equal("insert", result.GetProperty("db_stage").GetString());
        Assert.Contains("SQLITE_CONSTRAINT(19)", error);
        Assert.Contains("SqliteExtendedErrorCode=1811", error);
        Assert.Contains("Try again.", error);
        Assert.Empty(Todos.List());
    }

    [Fact]
    public async Task WriteReceiptSeparatesVerifiedCalendarTimeAppStateAndRowState()
    {
        var args = new { todos = new[] { new { title = "첫째" }, new { title = "둘째" } } };
        var off = await Call("create_todos", args);
        Assert.False(off.GetProperty("app_running").GetBoolean());
        Assert.Equal("app_not_running", off.GetProperty("app_reason").GetString());
        Assert.Equal(JsonValueKind.Null, off.GetProperty("last_verified_sync").GetProperty("calendar").ValueKind);
        Assert.Equal(JsonValueKind.Null, off.GetProperty("last_verified_sync").GetProperty("tasks").ValueKind);
        var uid = Id(off.GetProperty("todos")[0], "todo_id");
        Assert.Equal(uid, Todos.Get(uid).TodoUid);

        using var connection = _database.OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO sync_state(sync_key,last_synced_at) VALUES ('calendar_primary','2026-11-24T10:00:00Z');
            INSERT INTO setting VALUES ('google_token','x');
            """;
        cmd.ExecuteNonQuery();
        var update = new { todo_id = uid, title = "변경" };
        var waiting = await Call("update_todo", update);
        // 로그인(토큰)은 있는데 DB 옆 OAuth 클라이언트 파일이 없어 보낼 수 없다(S4: 사용 중·미로그인과 구분).
        Assert.Equal("client_file_missing", waiting.GetProperty("todo").GetProperty("reason").GetString());
        using (AppPresence.Register(_database))
        {
            var on = await Call("update_todo", update);
            Assert.True(on.GetProperty("app_running").GetBoolean());
            Assert.False(on.TryGetProperty("app_reason", out _));
            Assert.False(on.GetProperty("todo").TryGetProperty("reason", out _));
            Assert.Equal(DateTimeOffset.Parse("2026-11-24T10:00:00Z"),
                on.GetProperty("last_verified_sync").GetProperty("calendar").GetDateTimeOffset());
            Assert.Equal(JsonValueKind.Null, on.GetProperty("last_verified_sync").GetProperty("tasks").ValueKind);
        }
        cmd.CommandText = "DELETE FROM setting WHERE setting_key='google_token'; UPDATE todo SET push_state='unknown';";
        cmd.ExecuteNonQuery();
        var unknown = await Call("update_todo", update);
        Assert.Equal("needs_review", unknown.GetProperty("todo").GetProperty("reason").GetString());
        Assert.Equal("app_not_running", unknown.GetProperty("app_reason").GetString());
        await Call("create_todos", new { todos = new[] { new { title = "셋째" }, new { title = " " } } }, error: true);
        Assert.Equal(2, Todos.List().Count);
    }

    [Fact]
    public async Task ClosedAppWriteReportsOnlyVerifiedRowsAndPreservesReceiptOnTimeout()
    {
        using var connection = _database.OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "INSERT INTO setting VALUES ('google_token','offline')";
        cmd.ExecuteNonQuery();
        var tools = new CalendarTools(_database, () =>
        {
            cmd.CommandText = "UPDATE todo SET push_state='synced',is_dirty=0 WHERE title='확인됨';";
            cmd.ExecuteNonQuery();
            return Task.FromResult(SyncOutcome.Completed);
        });
        var args = JsonSerializer.SerializeToElement(new { todos = new[] { new { title = "확인됨" }, new { title = "대기" } } });
        var response = (await tools.InvokeAsync("create_todos", args, default)).StructuredContent!.Value;
        Assert.True(response.GetProperty("saved_locally").GetBoolean());
        Assert.Equal("synced", response.GetProperty("todos")[0].GetProperty("sync").GetString());
        Assert.Equal("pending", response.GetProperty("todos")[1].GetProperty("sync").GetString());

        // S4: 다른 DeskCal(앱·다른 MCP)이 보내는 중이면 그쪽이 보낸다. "로그인 없음"과 구분한다.
        var busy = await new CalendarTools(_database, () => Task.FromResult(SyncOutcome.Busy)).InvokeAsync("update_todo",
            JsonSerializer.SerializeToElement(new { todo_id = Id(response.GetProperty("todos")[1], "todo_id"), memo = "사용 중" }), default);
        Assert.Equal("sync_busy", busy.StructuredContent!.Value.GetProperty("todo").GetProperty("reason").GetString());

        var never = new TaskCompletionSource<SyncOutcome>();
        tools = new CalendarTools(_database, () => never.Task);
        using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        response = (await tools.InvokeAsync("create_todos", args, deadline.Token)).StructuredContent!.Value;
        Assert.True(response.GetProperty("saved_locally").GetBoolean());
        Assert.Equal("sync_timeout", response.GetProperty("todos")[0].GetProperty("reason").GetString());
        Assert.Equal(4, Todos.List().Count);
        never.SetResult(SyncOutcome.Busy);
    }

    // 리뷰 A-1: 영수증의 5초 마감이 실제 Google 전송까지 취소해, 보내기 전에 결과 불명으로 표시한 생성이 needs_review로 남았다.
    // 마감은 기다림만 끊고, 전송은 끝까지 가서 행이 synced가 되어야 한다.
    [Fact]
    public async Task SlowSendOutlivesTheReceiptWaitAndStillSyncsTheTodo()
    {
        using (var connection = _database.OpenConnection())
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "INSERT INTO setting VALUES ('google_token','offline')";
            cmd.ExecuteNonQuery();
        }
        var remote = new GatedTasks();
        using var lifetime = new CancellationTokenSource();
        Task<SyncOutcome>? sending = null;
        var tools = new CalendarTools(_database, () => sending = SendAsync());
        async Task<SyncOutcome> SendAsync()
        {
            await new TaskSync(_database, remote).SyncAsync(lifetime.Token);
            return SyncOutcome.Completed;
        }

        var waited = Stopwatch.StartNew();
        var args = JsonSerializer.SerializeToElement(new { todos = new[] { new { title = "느린 망에서 만든 할 일" } } });
        var response = (await tools.InvokeAsync("create_todos", args, default)).StructuredContent!.Value;
        waited.Stop();

        var todo = response.GetProperty("todos")[0];
        Assert.Equal("pending", todo.GetProperty("sync").GetString());
        Assert.Equal("sync_timeout", todo.GetProperty("reason").GetString());
        Assert.True(waited.Elapsed >= ToolOutput.ReceiptWait - TimeSpan.FromMilliseconds(100), waited.Elapsed.ToString());
        Assert.NotNull(sending);
        Assert.False(sending.IsCompleted); // 영수증을 돌려준 뒤에도 전송은 계속된다.
        Assert.False(remote.InsertCancelled);

        remote.Release();
        Assert.Equal(SyncOutcome.Completed, await sending.WaitAsync(_timeout.Token));
        var stored = Todos.Get(Id(todo, "todo_id"));
        Assert.Equal("synced", stored.PushState);
        Assert.False(stored.IsDirty);
        Assert.Equal(1, remote.InsertCalls);
        Assert.False(remote.InsertCancelled);
    }

    // 리뷰 B-L1: get_agenda는 list_events처럼 시작 시각순이다. 앱에서 저장한 날짜별 순서나 완료 여부로 바뀌지 않는다.
    [Fact]
    public async Task AgendaListsEventsOfADayInStartTimeOrder()
    {
        var day = DateOnly.FromDateTime(DateTime.Now).AddDays(1);
        var date = day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var afternoon = Events.Create([new("오후", date) { StartTime = "15:00" }]).Single();
        var morning = Events.Create([new("오전", date) { StartTime = "09:00" }]).Single();
        Events.Update(morning.EventUid, new() { IsDone = true });
        var noon = Events.Create([new("정오", date) { StartTime = "12:00" }]).Single();
        Assert.True(DayEventOrder.Move(Events, day, noon.EventUid, afternoon.EventUid)); // 앱에서 정오를 맨 위로 옮겼다

        var args = JsonSerializer.SerializeToElement(new { start_date = date, end_date = date });
        var body = (await new CalendarTools(_database).InvokeAsync("get_agenda", args, default)).StructuredContent!.Value;
        var group = body.GetProperty("groups").EnumerateArray().Single(g => g.GetProperty("date").GetString() == date);
        Assert.Equal(new[] { "오전", "정오", "오후" }, group.GetProperty("events").EnumerateArray().Select(e => e.GetProperty("title").GetString()));
    }

    // 생성 요청이 문을 열어 줄 때까지 응답하지 않는 가짜 Google Tasks. 받은 토큰이 취소되면 요청도 취소된다.
    private sealed class GatedTasks : ITasksRemote
    {
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly List<Google.Apis.Tasks.v1.Data.Task> _items = [];
        public int InsertCalls { get; private set; }
        public bool InsertCancelled { get; private set; }
        public void Release() => _gate.TrySetResult();

        public async Task<Google.Apis.Tasks.v1.Data.Task> InsertAsync(Google.Apis.Tasks.v1.Data.Task task, CancellationToken cancellationToken)
        {
            InsertCalls++;
            try { await _gate.Task.WaitAsync(cancellationToken); }
            catch (OperationCanceledException) { InsertCancelled = true; throw; }
            task.Id = "g" + InsertCalls;
            task.ETag = "e" + InsertCalls;
            _items.Add(task);
            return task;
        }

        public Task<IReadOnlyList<Google.Apis.Tasks.v1.Data.Task>> ListAllAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Google.Apis.Tasks.v1.Data.Task>>(_items.ToList());
        public Task<Google.Apis.Tasks.v1.Data.Task?> GetAsync(string id, CancellationToken cancellationToken) =>
            Task.FromResult(_items.SingleOrDefault(t => t.Id == id));
        public Task<Google.Apis.Tasks.v1.Data.Task> UpdateAsync(Google.Apis.Tasks.v1.Data.Task task, CancellationToken cancellationToken)
        {
            task.ETag += "+";
            _items[_items.FindIndex(t => t.Id == task.Id)] = task;
            return Task.FromResult(task);
        }
        public Task DeleteAsync(string taskId, string? etag, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    [Fact]
    public async Task ReceiptRecoversAfterMarkerOwnerProcessCrashes()
    {
        if (!OperatingSystem.IsWindows()) return;
        var start = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
            ArgumentList = { "-NoProfile", "-NonInteractive", "-Command",
                "$f=[IO.File]::Open($env:DESKCAL_TEST_MARKER,'OpenOrCreate','ReadWrite','None'); [Console]::WriteLine('ready'); Start-Sleep 60" }
        };
        start.Environment["DESKCAL_TEST_MARKER"] = Path.Combine(_directory, "nested", "deskcal.db.app-running");
        using var child = Process.Start(start)!;
        try
        {
            Assert.Equal("ready", await child.StandardOutput.ReadLineAsync(_timeout.Token));
            Assert.False(AppPresence.IsRunning(new Database(Path.Combine(_directory, "other.db"))));
            var receipt = await Call("create_todos", new { todos = new[] { new { title = "앱 실행 중" } } });
            Assert.True(receipt.GetProperty("app_running").GetBoolean());
        }
        finally
        {
            if (!child.HasExited) child.Kill();
            await child.WaitForExitAsync(_timeout.Token);
        }
        // Wait for Windows to finish releasing the terminated process's file handles.
        Assert.True(SpinWait.SpinUntil(() => !AppPresence.IsRunning(_database), TimeSpan.FromSeconds(5)));
        var next = await Call("create_todos", new { todos = new[] { new { title = "앱 종료 후" } } });
        Assert.False(next.GetProperty("app_running").GetBoolean());
    }

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
    public async Task DiscoversToolsWithAnnotationsAndEnums()
    {
        var tools = await _client.ListToolsAsync(cancellationToken: _timeout.Token);
        Assert.Equal(20, tools.Count);
        var rename = tools.Single(t => t.Name == "rename_category").ProtocolTool.InputSchema;
        Assert.Equal(new[] { "color_id", "name" }, rename.GetProperty("required").EnumerateArray().Select(p => p.GetString()));
        Assert.Contains("other DeskCal devices", tools.Single(t => t.Name == "rename_category").Description);
        var reads = new[] { "get_agenda", "list_events", "get_event", "find_free_time", "list_todos", "list_categories" };
        foreach (var tool in tools)
        {
            Assert.Equal(reads.Contains(tool.Name), tool.ProtocolTool.Annotations!.ReadOnlyHint);
            Assert.Equal(tool.Name.StartsWith("update_") || tool.Name is "rename_category" or "reorder_events", tool.ProtocolTool.Annotations.IdempotentHint);
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

    // S8: Claude가 get_agenda의 day_order를 보고 그날 순서를 통째로 다시 보낸다. 일정 자체는 바뀌지 않는다.
    [Fact]
    public async Task ReorderEventsUsesTheAgendaDayOrderAndRejectsStaleLists()
    {
        await Call("create_events", new { events = new[] { new { title = "첫째", start_date = "2026-12-01" }, new { title = "둘째", start_date = "2026-12-01" } } });
        await Call("create_events", new { events = new[] { new { title = "셋째", start_date = "2026-12-01" } } });
        await Call("create_events", new { events = new[] { new { title = "기간", start_date = "2026-11-30", end_date = "2026-12-02" } } });
        string[] DayOrder(JsonElement agenda) => agenda.GetProperty("groups").EnumerateArray().Single(g => g.GetProperty("kind").GetString() == "date")
            .GetProperty("day_order").EnumerateArray().Select(x => x.GetString()!).ToArray();
        var order = DayOrder(await Call("get_agenda", new { start_date = "2026-12-01", end_date = "2026-12-01" }));
        Assert.Equal(3, order.Length); // 여러 날 일정은 빠진다
        var reversed = order.Reverse().ToArray();

        var result = await Call("reorder_events", new { date = "2026-12-01", event_ids = reversed });
        Assert.Equal("2026-12-01", result.GetProperty("date").GetString());
        Assert.Equal(reversed, result.GetProperty("events").EnumerateArray().Select(e => Id(e)));
        Pending(result);
        Assert.Equal(reversed, DayOrder(await Call("get_agenda", new { start_date = "2026-12-01", end_date = "2026-12-01" })));

        var stale = await Call("reorder_events", new { date = "2026-12-01", event_ids = reversed.Take(2).ToArray() }, error: true);
        Assert.Contains("Current order", stale.GetProperty("error").GetString());
        await Call("reorder_events", new { date = "2026-12-01", event_ids = new[] { reversed[0], reversed[0], reversed[1] } }, error: true);
        var period = Events.GetRecords().Single(e => e.Title == "기간").EventUid;
        Assert.Contains("multi-day", (await Call("reorder_events", new { date = "2026-12-01", event_ids = reversed.Prepend(period).ToArray() }, error: true))
            .GetProperty("error").GetString());
        Assert.All(Events.GetRecords(), e => Assert.Equal(1, e.LocalRevision));
    }

    [Fact]
    public async Task RenameCategoryReturnsNewAliasAndPreservesExistingColor()
    {
        var existing = Events.Create([new("기존", "2026-11-03") { ColorId = "1" }]).Single();
        var changed = await Call("rename_category", new { color_id = "1", name = " 수업 " });
        Assert.Equal("수업", changed.GetProperty("category").GetProperty("name").GetString());
        Assert.Equal("#123456", changed.GetProperty("category").GetProperty("color_hex").GetString());
        // S6: 별칭은 다른 기기와 맞출 변경으로 남는다(로그인 전이라 대기).
        Pending(changed.GetProperty("category"));
        using (var connection = _database.OpenConnection())
        using (var shared = connection.CreateCommand())
        {
            shared.CommandText = "SELECT value_json || ':' || is_dirty FROM shared_entry WHERE entry_key='category_name:1'";
            Assert.Equal("\"수업\":1", shared.ExecuteScalar());
        }
        var result = await Call("list_events", new { start_date = "2026-11-03", category = "수업" });
        Assert.Equal(existing.EventUid, Id(result.GetProperty("events")[0]));
        var created = await Call("create_events", new { events = new[] { new { title = "새 수업", start_date = "2026-11-03", category = "수업" } } });
        Assert.Equal("1", Events.Get(Id(created.GetProperty("events")[0])).ColorId);
        foreach (var bad in new[] { "", "업무", "2" }) await Call("rename_category", new { color_id = "1", name = bad }, error: true);
        await Call("rename_category", new { color_id = "99", name = "없음" }, error: true);
    }

    [Fact]
    public async Task MilestoneUsesAllFourteenToolsThroughStdio()
    {
        var briefing = await Call("get_agenda");
        var today = DateOnly.Parse(briefing.GetProperty("today").GetString()!, CultureInfo.InvariantCulture);
        var tomorrow = today.AddDays(1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var categories = await Call("list_categories");
        Assert.Equal(11, categories.GetProperty("categories").GetArrayLength());
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

    // sync/reason come from the stored push_state and whether a Google token is stored, not a fixed value.
    [Theory]
    [InlineData(false, "synced", "synced", null)]
    [InlineData(true, "synced", "synced", null)]
    [InlineData(false, "pending", "pending", "not_signed_in")]
    [InlineData(true, "pending", "pending", null)]
    [InlineData(true, "failed", "pending", "push_failed")]
    [InlineData(true, "unknown", "pending", "needs_review")]
    [InlineData(false, "unknown", "pending", "needs_review")] // 다시 로그인해도 자동 전송되지 않으므로 확인 필요가 우선
    [InlineData(false, "failed", "pending", "not_signed_in")]
    public async Task SyncStatusReflectsStoredPushState(bool signedIn, string pushState, string sync, string? reason)
    {
        var todoId = Id((await Call("create_todos", new { todos = new[] { new { title = "상태" } } })).GetProperty("todos")[0], "todo_id");
        using (var connection = _database.OpenConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE todo SET push_state=$state WHERE todo_uid=$uid;" +
                (signedIn ? "INSERT INTO setting(setting_key, setting_value) VALUES ('google_token', 'x');" : "");
            command.Parameters.AddWithValue("$state", pushState);
            command.Parameters.AddWithValue("$uid", todoId);
            command.ExecuteNonQuery();
        }
        var todo = (await Call("list_todos")).GetProperty("todos")[0];
        Assert.Equal(sync, todo.GetProperty("sync").GetString());
        if (reason is null) Assert.False(todo.TryGetProperty("reason", out _));
        else Assert.Equal(reason, todo.GetProperty("reason").GetString());
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
    public async Task SeriesStartTimeCanChangeUntilAnOccurrenceHasItsOwnState()
    {
        var created = await Call("create_events", """{"events":[{"title":"주간","start_date":"2026-11-02","start_time":"09:00","repeat":{"frequency":"weekly","weekdays":["monday"]}}]}""");
        var uid = Id(created.GetProperty("events")[0]);
        await Call("update_event", new { event_id = uid, start_time = "11:00", end_time = "12:00" });
        var date = new { start_date = "2026-11-09", end_date = "2026-11-09" };
        var id = Id((await Call("list_events", date)).GetProperty("events")[0], "occurrence_id");
        await Call("add_checklist_items", new { occurrence_id = id, items = new[] { "준비" } });
        var result = await Call("update_event", new { event_id = uid, start_time = "12:00", end_time = "13:00" }, error: true);
        Assert.Contains("occurrence", result.GetProperty("error").GetString());
        var same = Assert.Single((await Call("list_events", date)).GetProperty("events").EnumerateArray());
        Assert.Equal(id, Id(same, "occurrence_id"));
        Assert.Equal("준비", same.GetProperty("checklist")[0].GetProperty("text").GetString());
    }

    [Fact]
    public async Task ChecklistIsPerOccurrenceAndSupportsEditDeleteRestore()
    {
        var created = await Call("create_events", """{"events":[{"title":"카공","start_date":"2026-11-08","start_time":"15:30","repeat":{"frequency":"weekly","weekdays":["sunday"],"count":3}}]}""");
        var series = Id(created.GetProperty("events")[0]);
        var list = await Call("list_events", new { start_date = "2026-11-08", end_date = "2026-11-15" });
        var first = list.GetProperty("events")[0].GetProperty("occurrence_id").GetString()!;
        var second = list.GetProperty("events")[1].GetProperty("occurrence_id").GetString()!;

        await Call("add_checklist_items", new { event_id = series, items = new[] { "시리즈에는 못 붙임" } }, true);
        var added = (await Call("add_checklist_items", new { occurrence_id = second, items = new[] { "자료구조 3장", "설계패턴 과제" } })).GetProperty("event").GetProperty("checklist");
        var itemId = added[0].GetProperty("item_id").GetString()!;
        Assert.Equal(2, added.GetArrayLength());
        Assert.False((await Call("get_event", new { occurrence_id = first })).GetProperty("event").TryGetProperty("checklist", out _));

        var checkedOff = (await Call("update_checklist_item", new { occurrence_id = second, item_id = itemId, is_done = true })).GetProperty("event").GetProperty("checklist");
        Assert.True(checkedOff[0].GetProperty("is_done").GetBoolean());
        await Call("update_checklist_item", new { occurrence_id = second, item_id = itemId }, true);
        await Call("update_checklist_item", new { occurrence_id = second, item_id = itemId, text = (string?)null }, true);
        await Call("update_checklist_item", new { occurrence_id = second, item_id = "missing", is_done = true }, true);

        var deleted = (await Call("delete_checklist_item", new { occurrence_id = second, item_id = itemId })).GetProperty("event").GetProperty("checklist");
        Assert.Equal("설계패턴 과제", Assert.Single(deleted.EnumerateArray()).GetProperty("text").GetString());
        var restored = (await Call("restore_checklist_item", new { occurrence_id = second, item_id = itemId })).GetProperty("event").GetProperty("checklist");
        Assert.Equal(2, restored.GetArrayLength());

        var agenda = (await Call("list_events", new { start_date = "2026-11-15", end_date = "2026-11-15" })).GetProperty("events")[0];
        Assert.Equal(2, agenda.GetProperty("checklist").GetArrayLength());
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
