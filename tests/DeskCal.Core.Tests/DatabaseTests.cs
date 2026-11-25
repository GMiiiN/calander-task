using System.Diagnostics;
using Microsoft.Data.Sqlite;

namespace DeskCal.Core.Tests;

public sealed class DatabaseTests
{
    [Fact]
    public void FingerprintUsesTheResolvedPathWithoutRevealingIt()
    {
        var folder = Path.GetTempPath();
        var name = "deskcal-" + Guid.NewGuid().ToString("N") + ".db";
        var database = new Database(Path.Combine(folder, name));
        Assert.Equal(database.PathFingerprint, new Database(Path.Combine(folder, ".", name)).PathFingerprint);
        Assert.NotEqual(database.PathFingerprint, new Database(Path.Combine(folder, "other.db")).PathFingerprint);
        Assert.DoesNotContain(name, database.DiagnosticIdentity);
    }

    [Fact]
    public void InitializeSeedsDefaultCategoriesOnlyOnce()
    {
        using var db = new TestDatabase();
        Assert.Equal(11, db.Scalar("SELECT COUNT(*) FROM category"));
        Assert.Equal(1, db.Scalar("SELECT COUNT(*) FROM category WHERE color_id='1' AND name='라벤더' AND color_hex='#7986CB'"));
        Assert.Equal(1, db.Scalar("SELECT COUNT(*) FROM category WHERE color_id='11' AND name='토마토' AND color_hex='#D50000'"));
        db.Database.Initialize();
        Assert.Equal(11, db.Scalar("SELECT COUNT(*) FROM category"));
        Assert.Equal(Database.LatestVersion, db.Scalar("PRAGMA user_version"));
    }

    [Fact]
    public void InitializePreservesExistingCategoryRowsWithoutFillingDefaults()
    {
        using var db = new TestDatabase();
        db.Execute("DELETE FROM category; INSERT INTO category VALUES ('1','개인','#123456');");
        db.Database.Initialize();
        Assert.Equal(1, db.Scalar("SELECT COUNT(*) FROM category"));
        Assert.Equal(1, db.Scalar("SELECT COUNT(*) FROM category WHERE color_id='1' AND name='개인' AND color_hex='#123456'"));
    }

    [Fact]
    public void ConnectionAndSchemaMatchContract()
    {
        using var db = new TestDatabase();
        using var connection = db.Database.OpenConnection();
        Assert.Equal(5, connection.DefaultTimeout);
        Assert.Equal(5000, db.Scalar("PRAGMA busy_timeout"));
        Assert.Equal(1, db.Scalar("PRAGMA foreign_keys"));
        Assert.Equal(1, db.Scalar("PRAGMA synchronous"));
        Assert.Equal(Database.LatestVersion, db.Scalar("PRAGMA user_version"));
        Assert.Equal(8, db.Scalar("SELECT COUNT(*) FROM sqlite_master WHERE type='table'"));
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode";
        Assert.Equal("wal", command.ExecuteScalar());
        db.Database.Initialize();
        Assert.Equal(8, db.Scalar("SELECT COUNT(*) FROM sqlite_master WHERE type='table'"));
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
            Assert.Equal(8L, command.ExecuteScalar());
            command.CommandText = "PRAGMA user_version";
            Assert.Equal((long)Database.LatestVersion, command.ExecuteScalar());
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
    public void VersionOneDatabaseKeepsItsEventsWhenChecklistColumnIsAdded()
    {
        using var db = new TestDatabase();
        var created = db.Events.Create([new EventInput("기존 일정", "2026-11-02")])[0];
        db.Execute("DROP TABLE day_event_order; ALTER TABLE event DROP COLUMN checklist");
        db.Execute("PRAGMA user_version=1");

        db.Database.Initialize();

        Assert.Equal(Database.LatestVersion, db.Scalar("PRAGMA user_version"));
        var value = db.Events.Get(created.EventUid);
        Assert.Equal("기존 일정", value.Title);
        Assert.Empty(value.Checklist);
        var backup = Assert.Single(Directory.GetFiles(db.Path + ".backups"));
        using var old = new Database(backup).OpenConnection();
        using var read = old.CreateCommand();
        read.CommandText = "PRAGMA user_version";
        Assert.Equal(1L, read.ExecuteScalar());
        read.CommandText = "SELECT title FROM event";
        Assert.Equal("기존 일정", read.ExecuteScalar());
        db.Database.Initialize();
        Assert.Single(Directory.GetFiles(db.Path + ".backups", "*.db"));
    }

    [Fact]
    public void DayOrderUpgradeBacksUpVersionTwoBeforeCreatingTheOrderTable()
    {
        using var db = new TestDatabase();
        var ev = db.Events.Create([new("이전 일정", "2026-11-02")]).Single();
        db.Execute("DROP TABLE day_event_order; PRAGMA user_version=2;");
        db.Database.Initialize();
        var path = Assert.Single(Directory.GetFiles(db.Path + ".backups", "v2-*.db"));
        using var old = new Database(path).OpenConnection();
        using var sql = old.CreateCommand();
        sql.CommandText = "PRAGMA integrity_check";
        Assert.Equal("ok", sql.ExecuteScalar());
        sql.CommandText = "PRAGMA user_version";
        Assert.Equal(2L, sql.ExecuteScalar());
        sql.CommandText = "SELECT count(*) FROM sqlite_master WHERE name='day_event_order'";
        Assert.Equal(0L, sql.ExecuteScalar());
        Assert.Equal(ev.EventUid, new EventRepository(new Database(path)).Get(ev.EventUid).EventUid);
        Assert.Equal(0, db.Scalar("SELECT COUNT(*) FROM day_event_order"));
    }

    private static SqliteConnection BackupDestination(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        connection.Open();
        return connection;
    }

    private static void UnusedMigration(SqliteCommand _) => throw new InvalidOperationException("이미 적용한 마이그레이션");

    [Fact]
    public void FutureUpgradeBacksUpCommittedWalAndRecoversAfterInterruptedStaging()
    {
        using var db = new TestDatabase();
        Assert.False(Directory.Exists(db.Path + ".backups"));
        using var writer = db.Database.OpenConnection(); // keep WAL pages uncheckpointed
        using var sql = writer.CreateCommand();
        sql.CommandText = "PRAGMA wal_autocheckpoint=0; PRAGMA wal_checkpoint(TRUNCATE);";
        sql.ExecuteNonQuery();
        var todo = db.Todos.Create([new("WAL 할 일")])[0];
        var ev = db.Events.Create([new("체크리스트", "2026-11-02")])[0];
        db.Events.AddChecklistItems(ev.EventUid, false, ["복구 확인"]);
        Assert.True(new FileInfo(db.Path + "-wal").Length > 32);
        var folder = Directory.CreateDirectory(db.Path + ".backups").FullName;
        File.WriteAllText(Path.Combine(folder, "snapshot.partial"), "interrupted");
        db.Database.Initialize([.. Enumerable.Repeat<Action<SqliteCommand>>(UnusedMigration, Database.LatestVersion), cmd =>
        {
            cmd.CommandText = "DELETE FROM todo; DELETE FROM event;";
            cmd.ExecuteNonQuery();
        }], BackupDestination);
        Assert.Equal(Database.LatestVersion + 1, db.Scalar("PRAGMA user_version"));
        Assert.Empty(db.Todos.List("all"));
        var restorePath = Path.Combine(folder, "restored.db");
        File.Copy(Assert.Single(Directory.GetFiles(folder)), restorePath);
        var backup = new Database(restorePath);
        backup.Initialize(); // restore the copy with the previous executable
        Assert.Equal("WAL 할 일", new TodoRepository(backup).Get(todo.TodoUid).Title);
        Assert.Contains(new EventRepository(backup).Get(ev.EventUid).Checklist, i => i.Text == "복구 확인");
    }

    [Fact]
    public void InterruptedMigrationRollsBackAfterBackupPublication()
    {
        using var db = new TestDatabase();
        var todo = db.Todos.Create([new("중단 전")])[0];
        Assert.Throws<OperationCanceledException>(() => db.Database.Initialize([.. Enumerable.Repeat<Action<SqliteCommand>>(UnusedMigration, Database.LatestVersion), cmd =>
        {
            cmd.CommandText = "DELETE FROM todo";
            cmd.ExecuteNonQuery();
            throw new OperationCanceledException();
        }], BackupDestination));
        Assert.Equal(Database.LatestVersion, db.Scalar("PRAGMA user_version"));
        Assert.Equal("중단 전", db.Todos.Get(todo.TodoUid).Title);
        var backup = new Database(Assert.Single(Directory.GetFiles(db.Path + ".backups")));
        Assert.Equal(todo.TodoUid, Assert.Single(new TodoRepository(backup).List()).TodoUid);
    }

    [Fact]
    public void FullBackupDestinationPreservesVersionAndDataAndCanRetry()
    {
        using var db = new TestDatabase();
        var todo = db.Todos.Create([new("보존")])[0];
        Action<SqliteCommand>[] migrations = [.. Enumerable.Repeat<Action<SqliteCommand>>(UnusedMigration, Database.LatestVersion), _ => { }];
        var error = Assert.Throws<SqliteException>(() => db.Database.Initialize(migrations, path =>
        {
            var destination = BackupDestination(path);
            using var limit = destination.CreateCommand();
            limit.CommandText = "PRAGMA max_page_count=1";
            limit.ExecuteNonQuery();
            return destination;
        }));
        Assert.Equal(13, error.SqliteErrorCode); // SQLITE_FULL during the actual sqlite3_backup
        Assert.Equal(Database.LatestVersion, db.Scalar("PRAGMA user_version"));
        Assert.Equal("보존", db.Todos.Get(todo.TodoUid).Title);
        Assert.Empty(Directory.GetFiles(db.Path + ".backups"));
        db.Database.Initialize(migrations, BackupDestination);
        Assert.Single(Directory.GetFiles(db.Path + ".backups"));
    }

    [Fact]
    public void NewerSchemaIsRejectedWithoutChangingDatabase()
    {
        using var db = new TestDatabase();
        db.Execute("PRAGMA user_version=99");
        Assert.Contains("newer", Assert.Throws<InvalidOperationException>(db.Database.Initialize).Message);
        Assert.Equal(99, db.Scalar("PRAGMA user_version"));
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
