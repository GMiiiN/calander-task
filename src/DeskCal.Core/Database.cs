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
}
