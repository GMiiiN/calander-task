using System.Net;
using Google;
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

    [Fact]
    public async Task UnknownInsertResultIsNotSentAgain()
    {
        using var db = new TestDatabase();
        var todos = new TodoRepository(db.Database);
        var created = todos.Create([new TodoInput("응답이 사라진 할 일")])[0];
        var remote = new FakeTasks { FailInsertWith = new HttpRequestException("connection reset") };

        await new TaskSync(db.Database, remote).SyncAsync(CancellationToken.None);
        remote.FailInsertWith = null;
        await new TaskSync(db.Database, remote).SyncAsync(CancellationToken.None);

        Assert.Equal(1, remote.InsertCalls);
        Assert.Equal("unknown", todos.Get(created.TodoUid).PushState);
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

        public Task<IReadOnlyList<GoogleTask>> ListAllAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<GoogleTask>>(Items.Select(Copy).ToList());

        public Task<GoogleTask> InsertAsync(GoogleTask task, CancellationToken cancellationToken)
        {
            InsertCalls++;
            if (FailInsertWith is not null) throw FailInsertWith;
            BeforeInsertReturns?.Invoke();
            var stored = Copy(task);
            stored.Id = "g" + ++_next;
            Add(stored);
            return Task.FromResult(Copy(stored));
        }

        public Task<GoogleTask> UpdateAsync(GoogleTask task, CancellationToken cancellationToken)
        {
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
