using System.Globalization;

namespace DeskCal.Core;

/// <summary>
/// 공휴일 캐시(§5.6). 동기화가 받아 온 목록을 통째로 바꾸고, 화면은 날짜 범위로 읽는다. 오프라인이면 마지막 목록을 그대로 쓴다.
/// </summary>
public sealed class HolidayRepository(Database database)
{
    private const string SyncKey = "holidays";

    // 날짜별 이름. 같은 날 둘 이상이면 " · "로 잇는다.
    public IReadOnlyDictionary<DateOnly, string> List(DateOnly from, DateOnly to)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT holiday_date, name FROM holiday WHERE holiday_date BETWEEN $from AND $to ORDER BY holiday_date, name;";
        command.Parameters.AddWithValue("$from", Format(from));
        command.Parameters.AddWithValue("$to", Format(to));
        using var reader = command.ExecuteReader();
        var result = new Dictionary<DateOnly, string>();
        while (reader.Read())
        {
            var date = DateOnly.ParseExact(reader.GetString(0), "yyyy-MM-dd", CultureInfo.InvariantCulture);
            result[date] = result.TryGetValue(date, out var names) ? names + " · " + reader.GetString(1) : reader.GetString(1);
        }
        return result;
    }

    // 받아 온 목록 전체로 바꾸고 동기화 시각을 남긴다. 모든 페이지를 받은 뒤에만 부른다.
    public void Replace(IEnumerable<(DateOnly Date, string Name)> holidays, DateTimeOffset syncedAt)
    {
        using var connection = database.OpenConnection();
        using var transaction = connection.BeginTransaction(deferred: false);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM holiday;";
        command.ExecuteNonQuery();
        command.CommandText = "INSERT OR IGNORE INTO holiday(holiday_date, name, is_day_off) VALUES ($date, $name, 1);";
        var date = command.Parameters.Add("$date", Microsoft.Data.Sqlite.SqliteType.Text);
        var name = command.Parameters.Add("$name", Microsoft.Data.Sqlite.SqliteType.Text);
        foreach (var holiday in holidays)
        {
            date.Value = Format(holiday.Date);
            name.Value = holiday.Name;
            command.ExecuteNonQuery();
        }
        command.Parameters.Clear();
        command.CommandText = "INSERT INTO sync_state(sync_key, last_synced_at) VALUES ($key, $at) " +
            "ON CONFLICT(sync_key) DO UPDATE SET last_synced_at = excluded.last_synced_at;";
        command.Parameters.AddWithValue("$key", SyncKey);
        command.Parameters.AddWithValue("$at", syncedAt.ToString("O", CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    public DateTimeOffset? LastSyncedAt()
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT last_synced_at FROM sync_state WHERE sync_key = $key;";
        command.Parameters.AddWithValue("$key", SyncKey);
        return command.ExecuteScalar() is string value ? DateTimeOffset.Parse(value, CultureInfo.InvariantCulture) : null;
    }

    private static string Format(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
