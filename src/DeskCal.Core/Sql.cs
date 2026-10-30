using Microsoft.Data.Sqlite;

namespace DeskCal.Core;

internal static class Sql
{
    public static SqliteCommand Command(SqliteConnection connection, SqliteTransaction? transaction, string sql, params (string Name, object? Value)[] values)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in values) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }

    public static long Insert(SqliteConnection connection, SqliteTransaction transaction, string table, Dictionary<string, object?> values)
    {
        using var command = Command(connection, transaction,
            $"INSERT INTO {table} ({string.Join(',', values.Keys)}) VALUES ({string.Join(',', values.Keys.Select(k => "$" + k))}) RETURNING {table}_id;",
            values.Select(pair => ("$" + pair.Key, pair.Value)).ToArray());
        return (long)command.ExecuteScalar()!;
    }

    public static void Update(SqliteConnection connection, SqliteTransaction transaction, string table, long id, Dictionary<string, object?> values)
    {
        using var command = Command(connection, transaction,
            $"UPDATE {table} SET {string.Join(',', values.Keys.Select(k => k + "=$" + k))} WHERE {table}_id=$id;",
            values.Select(pair => ("$" + pair.Key, pair.Value)).Append(("$id", (object?)id)).ToArray());
        command.ExecuteNonQuery();
    }

    public static string? Text(SqliteDataReader reader, string name) => reader[name] is DBNull ? null : (string)reader[name];
    public static long Number(SqliteDataReader reader, string name) => (long)reader[name];
}
