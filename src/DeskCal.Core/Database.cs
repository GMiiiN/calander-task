using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace DeskCal.Core;

public sealed class Database
{
    private static readonly Action<SqliteCommand>[] Migrations = [MigrateToV1, MigrateToV2, MigrateToV3, MigrateToV4, MigrateToV5, MigrateToV6];
    public const int LatestVersion = 6;
    private readonly string _connectionString;

    private readonly Action<string>? _trace;
    internal string FilePath { get; }
    public string PathFingerprint { get; }
    public string DiagnosticIdentity => $"db={PathFingerprint} version={typeof(Database).Assembly.GetName().Version} pid={Environment.ProcessId}";

    public Database(string path, Action<string>? trace = null)
    {
        _trace = trace;
        FilePath = path = Path.GetFullPath(path);
        var normalized = OperatingSystem.IsWindows() ? path.ToUpperInvariant() : path;
        PathFingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))[..24];
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path, DefaultTimeout = 5, Pooling = false
        }.ToString();
    }

    // Only fixed operation/stage names and SQLite codes enter diagnostics, never values or paths.
    public T Measure<T>(string stage, Func<T> action)
    {
        var started = Stopwatch.GetTimestamp();
        SqliteException? failure = null;
        try { return action(); }
        catch (SqliteException error)
        {
            failure = error;
            if (!error.Data.Contains("db_stage"))
            {
                error.Data["db_stage"] = stage;
                error.Data["db_elapsed_ms"] = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            }
            throw;
        }
        finally
        {
            if (_trace is not null)
            {
                var elapsed = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                var codes = failure is null ? "" : $" code={failure.SqliteErrorCode} extended={failure.SqliteExtendedErrorCode}";
                try { _trace($"{DateTimeOffset.UtcNow:O} {DiagnosticIdentity} op={Activity.Current?.OperationName ?? "initialize"} stage={stage} ms={elapsed}{codes}"); }
                catch (IOException) { } // A closed diagnostic stream cannot invalidate a commit.
            }
        }
    }

    internal void Measure(string stage, Action action) => Measure(stage, () => { action(); return true; });

    public SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        try
        {
            Measure("open", connection.Open);
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA busy_timeout=5000; PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON; PRAGMA synchronous=NORMAL;";
            Measure("pragma", command.ExecuteNonQuery);
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    public void Initialize() => Initialize(Migrations, path => OpenSnapshot(path, SqliteOpenMode.ReadWriteCreate));

    internal void Initialize(IReadOnlyList<Action<SqliteCommand>> migrations, Func<string, SqliteConnection> openDestination)
    {
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction(deferred: false);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "PRAGMA user_version;";
        var version = Convert.ToInt32(command.ExecuteScalar());
        if (version > migrations.Count)
            throw new InvalidOperationException($"Database version {version} is newer than supported version {migrations.Count}.");
        // BEGIN IMMEDIATE holds competing initializers and writers until the snapshot and migration finish.
        if (version > 0 && version < migrations.Count)
            BackupBeforeMigration(version, openDestination);
        while (version < migrations.Count)
        {
            migrations[version](command);
            command.CommandText = $"PRAGMA user_version={++version};";
            command.ExecuteNonQuery();
        }
        command.CommandText = "SELECT COUNT(*) FROM category;";
        if ((long)command.ExecuteScalar()! == 0)
        {
            command.CommandText = "INSERT INTO category(color_id, name, color_hex) VALUES " +
                string.Join(", ", CategoryRepository.Defaults.Select(d => $"('{d.ColorId}', '{d.Name}', '{d.ColorHex}')")) + ";";
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    private void BackupBeforeMigration(int version, Func<string, SqliteConnection> openDestination)
    {
        var directory = Directory.CreateDirectory(FilePath + ".backups").FullName;
        var staging = Path.Combine(directory, "snapshot.partial");
        var final = Path.Combine(directory, $"v{version}-{DateTime.UtcNow:yyyyMMddTHHmmssfffZ}-{Guid.NewGuid():N}.db");
        // Only the holder of the source write lock accesses this staging name, including after a crash.
        void ClearStaging()
        {
            foreach (var suffix in new[] { "", "-journal", "-wal", "-shm" })
                File.Delete(staging + suffix);
        }

        ClearStaging();
        try
        {
            using (var reader = OpenSnapshot(FilePath, SqliteOpenMode.ReadOnly))
            using (var destination = openDestination(staging))
            {
                reader.BackupDatabase(destination);
                using var journal = destination.CreateCommand();
                journal.CommandText = "PRAGMA journal_mode=DELETE";
                if (!string.Equals(journal.ExecuteScalar() as string, "delete", StringComparison.OrdinalIgnoreCase))
                    throw new IOException("독립적인 백업 파일을 만들 수 없습니다.");
            }
            using (var check = OpenSnapshot(staging, SqliteOpenMode.ReadOnly))
            using (var command = check.CreateCommand())
            {
                command.CommandText = "PRAGMA integrity_check";
                if (!Equals(command.ExecuteScalar(), "ok"))
                    throw new InvalidDataException("데이터베이스 백업의 무결성 검사에 실패했습니다.");
                command.CommandText = "PRAGMA user_version";
                if (Convert.ToInt32(command.ExecuteScalar()) != version)
                    throw new InvalidDataException("백업의 스키마 버전이 원본과 다릅니다.");
            }
            File.Move(staging, final);
        }
        finally { ClearStaging(); }
    }

    private static SqliteConnection OpenSnapshot(string path, SqliteOpenMode mode)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = path, Mode = mode, Pooling = false }.ToString());
        try { connection.Open(); return connection; }
        catch { connection.Dispose(); throw; }
    }

    private static void MigrateToV1(SqliteCommand command)
    {
        using var stream = typeof(Database).Assembly.GetManifestResourceStream("DeskCal.Core.SchemaV1.sql")!;
        using var reader = new StreamReader(stream);
        command.CommandText = reader.ReadToEnd();
        command.ExecuteNonQuery();
    }

    // 412 필드 병합(S4). sync_base는 마지막으로 Google과 맞춘 값(필드 묶음별 JSON)이다. 이것과 이 PC·Google 값을 비교해
    // 서로 다른 필드는 둘 다 살리고, 같은 필드가 둘 다 바뀌었으면 Google 값을 남기고 이 PC 값은 sync_conflict.detail_json에 둔다.
    // S4 이전에 동기화한 행은 sync_base가 없어 예전처럼 원격 우선이다.
    private static void MigrateToV6(SqliteCommand command)
    {
        command.CommandText = """
            ALTER TABLE event ADD COLUMN sync_base TEXT;
            ALTER TABLE todo ADD COLUMN sync_base TEXT;
            ALTER TABLE sync_conflict ADD COLUMN detail_json TEXT;
            """;
        command.ExecuteNonQuery();
    }

    // 기기 사이에 맞추는 설정(S6). 키마다 값과 버전(rev, 쓴 기기)을 둔다. 이 PC에서 바꾸고 아직 올리지 않은 값은 is_dirty이고,
    // base_*는 바꿀 때 알던 버전(그보다 새 값이 먼저 올라갔으면 원격 우선), edit_seq는 올리는 사이 또 바꿨는지 판단에 쓴다.
    private static void MigrateToV5(SqliteCommand command)
    {
        command.CommandText = """
            CREATE TABLE shared_entry (
                entry_key TEXT PRIMARY KEY, value_json TEXT NOT NULL, rev INTEGER NOT NULL, writer TEXT NOT NULL,
                written_at TEXT NOT NULL, is_dirty INTEGER NOT NULL DEFAULT 0,
                base_rev INTEGER NOT NULL DEFAULT 0, base_writer TEXT NOT NULL DEFAULT '', edit_seq INTEGER NOT NULL DEFAULT 0
            );
            ALTER TABLE sync_state ADD COLUMN last_error TEXT;
            """;
        command.ExecuteNonQuery();
    }

    private static void MigrateToV4(SqliteCommand command)
    {
        command.CommandText = """
            CREATE TABLE task_attempt (
                todo_id INTEGER PRIMARY KEY REFERENCES todo(todo_id) ON DELETE CASCADE,
                marker TEXT NOT NULL, sent_revision INTEGER, ambiguous INTEGER NOT NULL DEFAULT 0
            );
            """;
        command.ExecuteNonQuery();
    }

    private static void MigrateToV3(SqliteCommand command)
    {
        command.CommandText = """
            CREATE TABLE day_event_order (
                day TEXT NOT NULL, event_key TEXT NOT NULL, position INTEGER NOT NULL,
                revision INTEGER NOT NULL, PRIMARY KEY(day,event_key), UNIQUE(day,position)
            );
            """;
        command.ExecuteNonQuery();
    }

    private static void MigrateToV2(SqliteCommand command)
    {
        command.CommandText = "ALTER TABLE event ADD COLUMN checklist TEXT NOT NULL DEFAULT '[]';";
        command.ExecuteNonQuery();
    }
}
