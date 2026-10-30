using Microsoft.Data.Sqlite;

namespace DeskCal.Core.Tests;

public sealed class TestDatabase : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "deskcal-" + Guid.NewGuid().ToString("N") + ".db");
    public Database Database { get; }
    public EventRepository Events { get; }
    public TodoRepository Todos { get; }
    public static TimeZoneInfo Seoul => TimeZoneInfo.FindSystemTimeZoneById("Asia/Seoul");
    public FixedClock Clock { get; } = new();

    public TestDatabase()
    {
        Database = new Database(Path);
        Database.Initialize();
        Events = new EventRepository(Database, Seoul, Clock);
        Todos = new TodoRepository(Database, Clock);
    }

    public void Execute(string sql, params (string Name, object? Value)[] values)
    {
        using var connection = Database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in values) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    public long Scalar(string sql)
    {
        using var connection = Database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }

    public void Dispose()
    {
        foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(Path + suffix);
    }
}

public sealed class FixedClock : TimeProvider
{
    public DateTimeOffset Now { get; set; } = DateTimeOffset.Parse("2026-10-30T03:00:00Z");
    public override DateTimeOffset GetUtcNow() => Now;
}
