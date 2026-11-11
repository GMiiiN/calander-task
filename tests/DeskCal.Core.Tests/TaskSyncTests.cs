using System.Net;
using Google;
using Google.Apis.Auth.OAuth2.Responses;
using GoogleTask = Google.Apis.Tasks.v1.Data.Task;

namespace DeskCal.Core.Tests;

public sealed class TaskSyncTests
{
    [Fact]
    public async Task PushesNewTodoOnceAndPullDoesNotDuplicateIt()
    {
        using var db = new TestDatabase();
        var todos = new TodoRepository(db.Database);
        var created = todos.Create([new TodoInput("보고서", "2026-11-05")])[0];
        var remote = new FakeTasks();

        await new TaskSync(db.Database, remote).SyncAsync(CancellationToken.None);
        await new TaskSync(db.Database, remote).SyncAsync(CancellationToken.None);

        var item = Assert.Single(remote.Items);
        Assert.Equal("보고서", item.Title);
        Assert.Equal("2026-11-05T00:00:00.000Z", item.Due);
        var local = Assert.Single(todos.List("all"));
        Assert.Equal(created.TodoUid, local.TodoUid);
        Assert.False(local.IsDirty);
        Assert.Equal("synced", local.PushState);
    }

    [Fact]
    public async Task PullsRemoteTodoAndRemovesOneDeletedElsewhere()
    {
        using var db = new TestDatabase();
        var remote = new FakeTasks();
        remote.Add(new GoogleTask { Id = "r1", Title = "휴대폰에서 만든 할 일", Due = "2026-11-09T00:00:00.000Z", Status = "needsAction" });
        var todos = new TodoRepository(db.Database);

        await new TaskSync(db.Database, remote).SyncAsync(CancellationToken.None);
        var pulled = Assert.Single(todos.List("all"));
        Assert.Equal("휴대폰에서 만든 할 일", pulled.Title);
        Assert.Equal("2026-11-09", pulled.DueDate);
        Assert.Equal("google", pulled.CreatedBy);

        remote.Items.Clear();
        await new TaskSync(db.Database, remote).SyncAsync(CancellationToken.None);
        Assert.Empty(todos.List("all"));
    }

    [Fact]
    public async Task RemoteWinsOnConflictAndLocalChangeIsRecorded()
    {
        using var db = new TestDatabase();
        var remote = new FakeTasks();
        remote.Add(new GoogleTask { Id = "r1", Title = "원래 제목", Status = "needsAction" });
        var todos = new TodoRepository(db.Database);
        await new TaskSync(db.Database, remote).SyncAsync(CancellationToken.None);
        var local = Assert.Single(todos.List("all"));

        todos.Update(local.TodoUid, new TodoPatch { Title = "이 PC에서 바꾼 제목" });
        remote.Edit("r1", t => t.Title = "다른 곳에서 바꾼 제목");
        await new TaskSync(db.Database, remote).SyncAsync(CancellationToken.None);

        Assert.Equal("다른 곳에서 바꾼 제목", todos.Get(local.TodoUid).Title);
        Assert.Equal("다른 곳에서 바꾼 제목", remote.Items.Single().Title);
        using var connection = db.Database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT discarded_json FROM sync_conflict WHERE item_kind='todo';";
        Assert.Contains("이 PC에서 바꾼 제목", (string)command.ExecuteScalar()!);
    }

    [Theory]
    [InlineData("invalid_grant")]
    [InlineData("invalid_client")]
    public async Task InsertAuthenticationFailureStaysPendingAndCanBeRetried(string error)
    {
        using var db = new TestDatabase();
        var todos = new TodoRepository(db.Database);
        var created = todos.Create([new TodoInput("다시 연결한 뒤 보낼 할 일")])[0];
        var failure = new TokenResponseException(new TokenErrorResponse { Error = error });
        var remote = new FakeTasks { FailInsertWith = failure };

        var thrown = await Assert.ThrowsAsync<TokenResponseException>(() =>
            new TaskSync(db.Database, remote).SyncAsync(CancellationToken.None));

        Assert.Same(failure, thrown);
        var pending = todos.Get(created.TodoUid);
        Assert.True(pending.IsDirty);
        Assert.Equal("pending", pending.PushState);
        Assert.Empty(remote.Items);

        remote.FailInsertWith = null;
        await new TaskSync(db.Database, remote).SyncAsync(CancellationToken.None);

        Assert.Equal(2, remote.InsertCalls);
        Assert.Equal(created.Title, Assert.Single(remote.Items).Title);
        Assert.False(todos.Get(created.TodoUid).IsDirty);
        Assert.Equal("synced", todos.Get(created.TodoUid).PushState);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnknownInsertResultIsNotSentAgain(bool timeout)
    {
        using var db = new TestDatabase();
        var todos = new TodoRepository(db.Database);
        var created = todos.Create([new TodoInput("응답이 사라진 할 일")])[0];
        var remote = new FakeTasks { FailInsertWith = timeout ? new TaskCanceledException("timeout") : new HttpRequestException("connection reset") };

        await new TaskSync(db.Database, remote).SyncAsync(CancellationToken.None);
        remote.FailInsertWith = null;
        await new TaskSync(db.Database, remote).SyncAsync(CancellationToken.None);

        Assert.Equal(1, remote.InsertCalls);
        Assert.Equal("unknown", todos.Get(created.TodoUid).PushState);
    }

    public static TheoryData<Exception> NotSentFailures => new()
    {
        new HttpRequestException(HttpRequestError.NameResolutionError, "No such host is known."),
        new HttpRequestException(HttpRequestError.ConnectionError, "connection refused"),
        new HttpRequestException("wrapped", new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.ConnectionRefused)),
    };

    // 응답을 받다가 끊긴 경우는 요청이 닿았을 수 있다. 다시 보내면 중복이 생기므로 결과 불명으로 남긴다.
    [Theory]
    [InlineData(HttpRequestError.ResponseEnded, null)]
    [InlineData(HttpRequestError.Unknown, System.Net.Sockets.SocketError.NetworkDown)]
    [InlineData(HttpRequestError.Unknown, System.Net.Sockets.SocketError.ConnectionReset)]
    public async Task FailureAfterConnectingStaysUnknown(HttpRequestError error, System.Net.Sockets.SocketError? socket)
    {
        using var db = new TestDatabase();
        var todos = new TodoRepository(db.Database);
        var created = todos.Create([new TodoInput("응답 도중 끊김")])[0];
        Exception? inner = socket is { } code ? new System.Net.Sockets.SocketException((int)code) : null;
        var remote = new FakeTasks { FailInsertWith = new HttpRequestException(error, "failed", inner) };

        await new TaskSync(db.Database, remote).SyncAsync(CancellationToken.None);
        remote.FailInsertWith = null;
        await new TaskSync(db.Database, remote).SyncAsync(CancellationToken.None);

        Assert.Equal(1, remote.InsertCalls);
        Assert.Equal("unknown", todos.Get(created.TodoUid).PushState);
    }

    // feedback 2-1: 절전 직후처럼 연결조차 못 한 실패를 결과 불명으로 묶으면 그 할 일과 이후 수정이 영영 올라가지 않았다.
    [Theory]
    [MemberData(nameof(NotSentFailures))]
    public async Task InsertThatNeverReachedGoogleIsSentAgain(Exception failure)
    {
        using var db = new TestDatabase();
        var todos = new TodoRepository(db.Database);
        var created = todos.Create([new TodoInput("오프라인에서 만든 할 일")])[0];
        var remote = new FakeTasks { FailInsertWith = failure };

        await new TaskSync(db.Database, remote).SyncAsync(CancellationToken.None);
        var waiting = todos.Get(created.TodoUid);
        Assert.Equal("pending", waiting.PushState);
        Assert.True(waiting.IsDirty);

        todos.Update(created.TodoUid, new TodoPatch { DueDate = "2026-11-20" });
        remote.FailInsertWith = null;
        await new TaskSync(db.Database, remote).SyncAsync(CancellationToken.None);

        Assert.Equal(2, remote.InsertCalls);
        var item = Assert.Single(remote.Items);
        Assert.Equal("2026-11-20T00:00:00.000Z", item.Due);
        Assert.Equal("synced", todos.Get(created.TodoUid).PushState);
    }

    // feedback 1: 방금 올린 할 일이 바로 이은 목록 조회에 아직 없으면 지워진 것으로 처리돼 잠시 사라졌다.
    [Fact]
    public async Task JustInsertedTodoMissingFromImmediateListIsKept()
    {
        using var db = new TestDatabase();
        var todos = new TodoRepository(db.Database);
        var created = todos.Create([new TodoInput("방금 만든 할 일")])[0];
        var remote = new FakeTasks { HideNewlyInsertedFromList = true };

        await new TaskSync(db.Database, remote).SyncAsync(CancellationToken.None);

        var kept = Assert.Single(todos.List("all"));
        Assert.Equal(created.TodoUid, kept.TodoUid);
        Assert.Null(kept.DeletedAt);

        await new TaskSync(db.Database, remote).SyncAsync(CancellationToken.None);
        Assert.Single(todos.List("all"));
        Assert.Single(remote.Items);
    }

    [Fact]
    public async Task UntitledRemoteTodoCanStillBeCompleted()
    {
        using var db = new TestDatabase();
        var remote = new FakeTasks();
        remote.Add(new GoogleTask { Id = "r1", Title = "", Status = "needsAction" });
        var todos = new TodoRepository(db.Database);
        await new TaskSync(db.Database, remote).SyncAsync(CancellationToken.None);
        var untitled = Assert.Single(todos.List("all"));

        var done = todos.Update(untitled.TodoUid, new TodoPatch { IsDone = true });

        Assert.True(done.IsDone);
        Assert.Throws<ArgumentException>(() => todos.Update(untitled.TodoUid, new TodoPatch { Title = " " }));
    }

    [Fact]
    public async Task EditingUnknownTodoDoesNotCreateItAgain()
    {
        using var db = new TestDatabase();
        var todos = new TodoRepository(db.Database);
        var created = todos.Create([new TodoInput("응답이 사라진 할 일")])[0];
        var remote = new FakeTasks { FailInsertWith = new HttpRequestException("connection reset") };
        await new TaskSync(db.Database, remote).SyncAsync(CancellationToken.None);

        todos.Update(created.TodoUid, new TodoPatch { Title = "나중에 고침" });
        remote.FailInsertWith = null;
        await new TaskSync(db.Database, remote).SyncAsync(CancellationToken.None);

        Assert.Equal(1, remote.InsertCalls);
        Assert.Equal("unknown", todos.Get(created.TodoUid).PushState);
    }

    [Fact]
    public async Task InsertIsMarkedUnknownBeforeItIsSent()
    {
        using var db = new TestDatabase();
        var todos = new TodoRepository(db.Database);
        var created = todos.Create([new TodoInput("보내는 중")])[0];
        string? stateWhileSending = null;
        var remote = new FakeTasks { BeforeInsertReturns = () => stateWhileSending = todos.Get(created.TodoUid).PushState };

        await new TaskSync(db.Database, remote).SyncAsync(CancellationToken.None);

        // 응답을 저장하기 전에 앱이 끝나면 이 상태로 남아 다시 보내지 않는다.
        Assert.Equal("unknown", stateWhileSending);
        Assert.Equal("synced", todos.Get(created.TodoUid).PushState);
    }

    [Fact]
    public async Task ConflictRecordsEditMadeDuringPush()
    {
        using var db = new TestDatabase();
        var remote = new FakeTasks();
        remote.Add(new GoogleTask { Id = "r1", Title = "원래 제목", Status = "needsAction" });
        var todos = new TodoRepository(db.Database);
        await new TaskSync(db.Database, remote).SyncAsync(CancellationToken.None);
        var local = Assert.Single(todos.List("all"));

        todos.Update(local.TodoUid, new TodoPatch { Title = "첫 수정" });
        remote.Edit("r1", t => t.Title = "다른 곳에서 바꾼 제목");
        remote.BeforeUpdateReturns = () => todos.Update(local.TodoUid, new TodoPatch { Title = "보내는 중에 또 고침" });
        await new TaskSync(db.Database, remote).SyncAsync(CancellationToken.None);

        Assert.Equal("다른 곳에서 바꾼 제목", todos.Get(local.TodoUid).Title);
        using var connection = db.Database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT discarded_json FROM sync_conflict WHERE item_kind='todo';";
        Assert.Contains("보내는 중에 또 고침", (string)command.ExecuteScalar()!);
    }

    [Fact]
    public async Task EditDuringPushStaysDirtyForNextRound()
    {
        using var db = new TestDatabase();
        var todos = new TodoRepository(db.Database);
        var created = todos.Create([new TodoInput("처음")])[0];
        var remote = new FakeTasks { BeforeInsertReturns = () => todos.Update(created.TodoUid, new TodoPatch { Title = "보내는 중에 바꿈" }) };

        await new TaskSync(db.Database, remote).SyncAsync(CancellationToken.None);
        Assert.True(todos.Get(created.TodoUid).IsDirty);

        remote.BeforeInsertReturns = null;
        await new TaskSync(db.Database, remote).SyncAsync(CancellationToken.None);
        Assert.Equal("보내는 중에 바꿈", Assert.Single(remote.Items).Title);
        Assert.False(todos.Get(created.TodoUid).IsDirty);
    }

    [Fact]
    public async Task LocalDeleteRemovesRemoteTask()
    {
        using var db = new TestDatabase();
        var remote = new FakeTasks();
        remote.Add(new GoogleTask { Id = "r1", Title = "지울 것", Status = "needsAction" });
        var todos = new TodoRepository(db.Database);
        await new TaskSync(db.Database, remote).SyncAsync(CancellationToken.None);

        todos.Delete(Assert.Single(todos.List("all")).TodoUid);
        await new TaskSync(db.Database, remote).SyncAsync(CancellationToken.None);

        Assert.Empty(remote.Items);
        Assert.Empty(todos.List("all"));
    }

    [Fact]
    public async Task SendsCompletionTimeInGoogleFormat()
    {
        using var db = new TestDatabase();
        var todos = new TodoRepository(db.Database);
        var created = todos.Create([new TodoInput("끝낸 일")])[0];
        todos.Update(created.TodoUid, new TodoPatch { IsDone = true });
        var remote = new FakeTasks();

        await new TaskSync(db.Database, remote).SyncAsync(CancellationToken.None);

        var item = Assert.Single(remote.Items);
        Assert.Equal("completed", item.Status);
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z$", item.Completed);
    }

    private sealed class FakeTasks : ITasksRemote
    {
        private int _next;
        public List<GoogleTask> Items { get; } = [];
        public Exception? FailInsertWith { get; set; }
        public Action? BeforeInsertReturns { get; set; }
        public Action? BeforeUpdateReturns { get; set; }
        public int InsertCalls { get; private set; }

        public void Add(GoogleTask task)
        {
            task.ETag = "e" + ++_next;
            Items.Add(task);
        }

        public void Edit(string id, Action<GoogleTask> change)
        {
            var task = Items.Single(t => t.Id == id);
            change(task);
            task.ETag = "e" + ++_next;
        }

        // 쓰기 직후 목록에 아직 안 보이는 항목을 흉내 낸다.
        public bool HideNewlyInsertedFromList { get; set; }
        private readonly HashSet<string> _insertedSinceList = [];

        public Task<IReadOnlyList<GoogleTask>> ListAllAsync(CancellationToken cancellationToken)
        {
            var visible = Items.Where(t => !HideNewlyInsertedFromList || !_insertedSinceList.Contains(t.Id)).Select(Copy).ToList();
            _insertedSinceList.Clear();
            return Task.FromResult<IReadOnlyList<GoogleTask>>(visible);
        }

        public Task<GoogleTask> InsertAsync(GoogleTask task, CancellationToken cancellationToken)
        {
            InsertCalls++;
            if (FailInsertWith is not null) throw FailInsertWith;
            BeforeInsertReturns?.Invoke();
            var stored = Copy(task);
            stored.Id = "g" + ++_next;
            Add(stored);
            _insertedSinceList.Add(stored.Id);
            return Task.FromResult(Copy(stored));
        }

        public Task<GoogleTask> UpdateAsync(GoogleTask task, CancellationToken cancellationToken)
        {
            BeforeUpdateReturns?.Invoke();
            var index = Items.FindIndex(t => t.Id == task.Id);
            if (index < 0) throw new GoogleApiException("tasks", "not found") { HttpStatusCode = HttpStatusCode.NotFound };
            if (task.ETag != Items[index].ETag) throw new GoogleApiException("tasks", "precondition") { HttpStatusCode = HttpStatusCode.PreconditionFailed };
            var stored = Copy(task);
            stored.ETag = "e" + ++_next;
            Items[index] = stored;
            return Task.FromResult(Copy(stored));
        }

        public Task DeleteAsync(string taskId, string? etag, CancellationToken cancellationToken)
        {
            var task = Items.SingleOrDefault(t => t.Id == taskId)
                ?? throw new GoogleApiException("tasks", "not found") { HttpStatusCode = HttpStatusCode.NotFound };
            if (etag is not null && etag != task.ETag) throw new GoogleApiException("tasks", "precondition") { HttpStatusCode = HttpStatusCode.PreconditionFailed };
            Items.Remove(task);
            return Task.CompletedTask;
        }

        private static GoogleTask Copy(GoogleTask t) => new()
        {
            Id = t.Id, ETag = t.ETag, Title = t.Title, Notes = t.Notes, Due = t.Due, Status = t.Status, Completed = t.Completed,
            Position = t.Position, Deleted = t.Deleted,
        };
    }
}
