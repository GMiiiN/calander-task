using Google.Apis.Calendar.v3.Data;
using Xunit.Abstractions;

namespace DeskCal.Core.Tests;

public class BulkSyncTests(ITestOutputHelper output)
{
    [Fact]
    public async Task CrashedSenderReleasesDatabaseLease()
    {
        using var db = new TestDatabase();
        var start = new System.Diagnostics.ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-Command");
        start.ArgumentList.Add("$f=[IO.File]::Open($env:DESKCAL_TEST_LOCK,'OpenOrCreate','ReadWrite','None'); [Console]::WriteLine('ready'); Start-Sleep 60");
        start.Environment["DESKCAL_TEST_LOCK"] = db.Path + ".sync-lock";
        using var child = System.Diagnostics.Process.Start(start)!;
        try
        {
            Assert.Equal("ready", await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.False(await SyncCoordinator.WithSenderAsync(db.Database, () => Task.FromResult(true), default));
        }
        finally { if (!child.HasExited) child.Kill(); await child.WaitForExitAsync(); }
        // Windows가 종료된 다른 프로세스의 파일 핸들을 실제로 해제할 때까지 기다린다.
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!await SyncCoordinator.WithSenderAsync(db.Database, () => Task.FromResult(true), deadline.Token))
            await Task.Delay(25, deadline.Token);
    }

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
