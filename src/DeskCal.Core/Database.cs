using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace DeskCal.Core;

public sealed class Database
{
    public const int LatestVersion = 2;
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

    public void Initialize()
    {
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction(deferred: false);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "PRAGMA user_version;";
        var version = Convert.ToInt32(command.ExecuteScalar());
        if (version > LatestVersion)
            throw new InvalidOperationException($"Database version {version} is newer than supported version {LatestVersion}.");
        Action<SqliteCommand>[] migrations = [MigrateToV1, MigrateToV2];
        while (version < LatestVersion)
        {
            migrations[version](command);
            command.CommandText = $"PRAGMA user_version={++version};";
            command.ExecuteNonQuery();
        }
        command.CommandText = "SELECT COUNT(*) FROM category;";
        if ((long)command.ExecuteScalar()! == 0)
        {
            command.CommandText = """
                INSERT INTO category(color_id, name, color_hex) VALUES
                ('1', '라벤더', '#7986CB'), ('2', '세이지', '#33B679'),
                ('3', '포도', '#8E24AA'), ('4', '플라밍고', '#E67C73'),
                ('5', '바나나', '#F6BF26'), ('6', '귤', '#F4511E'),
                ('7', '공작', '#039BE5'), ('8', '흑연', '#616161'),
                ('9', '블루베리', '#3F51B5'), ('10', '바질', '#0B8043'),
                ('11', '토마토', '#D50000');
                """;
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    private static void MigrateToV1(SqliteCommand command)
    {
        using var stream = typeof(Database).Assembly.GetManifestResourceStream("DeskCal.Core.SchemaV1.sql")!;
        using var reader = new StreamReader(stream);
        command.CommandText = reader.ReadToEnd();
        command.ExecuteNonQuery();
    }

    private static void MigrateToV2(SqliteCommand command)
    {
        command.CommandText = "ALTER TABLE event ADD COLUMN checklist TEXT NOT NULL DEFAULT '[]';";
        command.ExecuteNonQuery();
    }
}
