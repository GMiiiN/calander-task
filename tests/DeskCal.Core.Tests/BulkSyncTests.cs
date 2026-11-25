using Google.Apis.Calendar.v3.Data;
using Xunit.Abstractions;

namespace DeskCal.Core.Tests;

public class BulkSyncTests(ITestOutputHelper output)
{
    [Fact]
    public async Task InterruptedPullKeepsPreviousTokenAndRetries()
    {
        using var db = new TestDatabase();
        var remote = new EventSyncTests.FakeCalendar();
        var sync = new EventSync(db.Database, remote);
        await sync.SyncAsync(default);
        using var cancel = new CancellationTokenSource();
        remote.Put(new Event { Id = "a", Start = new() { Date = "2026-11-10" }, End = new() { Date = "2026-11-11" } });
        remote.BeforeListReturns = cancel.Cancel;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sync.SyncAsync(cancel.Token));
        Assert.Empty(db.Events.GetRecords());
        Assert.Equal(0, db.Scalar("SELECT sync_token FROM sync_state WHERE sync_key='calendar_primary'"));
        remote.BeforeListReturns = null;
        await sync.SyncAsync(default);
        Assert.Single(db.Events.GetRecords());
        Assert.Equal(1, db.Scalar("SELECT sync_token FROM sync_state WHERE sync_key='calendar_primary'"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BulkPullRetainsConcurrentWritesAndOriginalGoogleId(bool expired)
    {
        using var db = new TestDatabase();
        var remote = new EventSyncTests.FakeCalendar();
        var sync = new EventSync(db.Database, remote, TestDatabase.Seoul);
        for (var i = 0; i < 1500; i++)
            remote.Put(new Event { Id = $"event{i}", Summary = "이전", Start = new() { Date = "2026-11-10" }, End = new() { Date = "2026-11-11" } });
        await sync.SyncAsync(default);
        var edited = db.Events.GetRecords().First();
        db.Events.Update(edited.EventUid, new() { Title = "새 이름" });
        for (var i = 1; i < 750; i++) remote.Edit($"event{i}", e => e.Summary = "다른 PC");
        remote.ExpireTokens = expired;

        var traces = new List<string>();
        var writer = new TodoRepository(new Database(db.Path, traces.Add));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        remote.BeforeListReturns = () => started.TrySetResult();
        var pulling = Task.Run(() => sync.SyncAsync(default));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var local = writer.Create([new("동시 생성")]).Single();
        for (var i = 0; i < 10; i++)
        {
            await Task.Delay(5);
            writer.Update(local.TodoUid, new() { Title = $"수정 {i}" });
        }
        await pulling;
        var waits = traces.Where(t => t.Contains("stage=begin ")).Select(t => long.Parse(t.Split(" ms=")[1])).ToArray();
        output.WriteLine($"D1 begin max_ms={waits.Max()} total_ms={waits.Sum()} samples={waits.Length}");
        Assert.Equal(11, db.Todos.Get(local.TodoUid).LocalRevision);
        Assert.Equal(1500, remote.Items.Count);
        Assert.Equal("새 이름", remote.Items[edited.GoogleEventId!].Summary);
        Assert.Equal(1500, db.Events.GetRecords().Count);
    }
}
