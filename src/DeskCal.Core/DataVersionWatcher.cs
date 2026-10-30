using Microsoft.Data.Sqlite;

namespace DeskCal.Core;

public sealed class DataVersionWatcher(Database database) : IDisposable
{
    private readonly SqliteConnection _connection = database.OpenConnection();
    private long? _lastVersion;

    public bool HasChanged()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "PRAGMA data_version;";
        var version = Convert.ToInt64(command.ExecuteScalar());
        var changed = _lastVersion.HasValue && _lastVersion.Value != version;
        _lastVersion = version;
        return changed;
    }

    public void Dispose() => _connection.Dispose();
}
