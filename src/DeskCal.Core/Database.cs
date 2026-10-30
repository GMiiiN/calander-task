using Microsoft.Data.Sqlite;

namespace DeskCal.Core;

public sealed class Database
{
    public const int LatestVersion = 1;
    private readonly string _connectionString;

    public Database(string path)
    {
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path, DefaultTimeout = 5, Pooling = false
        }.ToString();
    }

    public SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        try
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA busy_timeout=5000; PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON; PRAGMA synchronous=NORMAL;";
            command.ExecuteNonQuery();
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
        Action<SqliteCommand>[] migrations = [MigrateToV1];
        while (version < LatestVersion)
        {
            migrations[version](command);
            command.CommandText = $"PRAGMA user_version={++version};";
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
}
