using System.Diagnostics;
using Microsoft.Data.Sqlite;

namespace DeskCal.Core.Tests;

public sealed class DatabaseTests
{
    [Fact]
    public void ConnectionAndSchemaMatchContract()
    {
        using var db = new TestDatabase();
        using var connection = db.Database.OpenConnection();
        Assert.Equal(5, connection.DefaultTimeout);
        Assert.Equal(5000, db.Scalar("PRAGMA busy_timeout"));
        Assert.Equal(1, db.Scalar("PRAGMA foreign_keys"));
        Assert.Equal(1, db.Scalar("PRAGMA synchronous"));
        Assert.Equal(1, db.Scalar("PRAGMA user_version"));
        Assert.Equal(7, db.Scalar("SELECT COUNT(*) FROM sqlite_master WHERE type='table'"));
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode";
        Assert.Equal("wal", command.ExecuteScalar());
        db.Database.Initialize();
        Assert.Equal(7, db.Scalar("SELECT COUNT(*) FROM sqlite_master WHERE type='table'"));
    }

    [Fact]
    public async Task ConcurrentFirstRunMigratesExactlyOnce()
    {
        var path = Path.Combine(Path.GetTempPath(), "deskcal-migration-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            using var gate = new ManualResetEventSlim();
            var tasks = Enumerable.Range(0, 8).Select(_ => Task.Run(() => { gate.Wait(); new Database(path).Initialize(); })).ToArray();
            gate.Set();
            await Task.WhenAll(tasks);
            using var connection = new Database(path).OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table'";
            Assert.Equal(7L, command.ExecuteScalar());
            command.CommandText = "PRAGMA user_version";
            Assert.Equal(1L, command.ExecuteScalar());
        }
        finally
        {
            foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(path + suffix);
        }
    }

    [Fact]
    public async Task OverlappingWritesSerializeWithoutLostRevisions()
    {
        using var db = new TestDatabase();
        var value = db.Events.Create([new("shared", "2026-10-30")])[0];
        var todo = db.Todos.Create([new("shared")])[0];
        using var gate = new ManualResetEventSlim();
        var tasks = Enumerable.Range(0, 12).Select(i => Task.Run(() =>
        {
            gate.Wait();
            new EventRepository(db.Database, TestDatabase.Seoul).Update(value.EventUid, new() { Title = "writer " + i });
            new TodoRepository(db.Database).Update(todo.TodoUid, new() { Title = "writer " + i });
        })).ToArray();
        gate.Set();
        await Task.WhenAll(tasks);
        Assert.Equal(13, db.Events.Get(value.EventUid).LocalRevision);
        Assert.Equal(13, db.Todos.Get(todo.TodoUid).LocalRevision);
        Assert.True(db.Events.Get(value.EventUid).IsDirty);
        Assert.Equal("pending", db.Todos.Get(todo.TodoUid).PushState);
        var reopened = new EventRepository(new Database(db.Path), TestDatabase.Seoul);
        Assert.Equal(13, reopened.Get(value.EventUid).LocalRevision);
    }

    [Fact]
    public void NewerSchemaIsRejectedWithoutChangingDatabase()
    {
        using var db = new TestDatabase();
        db.Execute("PRAGMA user_version=2");
        Assert.Contains("newer", Assert.Throws<InvalidOperationException>(db.Database.Initialize).Message);
        Assert.Equal(2, db.Scalar("PRAGMA user_version"));
    }

    [Fact]
    public async Task WriteLockTimesOutAndPreservesData()
    {
        using var db = new TestDatabase();
        var value = db.Todos.Create([new("locked")])[0];
        using var connection = db.Database.OpenConnection();
        using var transaction = connection.BeginTransaction(deferred: false);
        var timer = Stopwatch.StartNew();
        var error = await Task.Run(() => Assert.Throws<SqliteException>(() => db.Todos.Update(value.TodoUid, new() { Title = "blocked" })));
        Assert.Equal(5, error.SqliteErrorCode);
        Assert.True(timer.Elapsed >= TimeSpan.FromSeconds(4));
        transaction.Rollback();
        Assert.Equal("locked", db.Todos.Get(value.TodoUid).Title);
        Assert.Equal(1, db.Todos.Get(value.TodoUid).LocalRevision);
    }
}
