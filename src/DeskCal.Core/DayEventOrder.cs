using System.Globalization;
using Microsoft.Data.Sqlite;

namespace DeskCal.Core;

public sealed class DayEventOrder
{
    private readonly Dictionary<(DateOnly, string), int> _positions = [];

    public static string Key(EventOccurrence o) => o.OccurrenceId ?? o.Event.EventUid;
    public static bool IsPeriod(EventOccurrence o) => o.Start.Date < o.End.AddTicks(-1).Date;

    public static DayEventOrder Read(Database database, DateOnly start, DateOnly end)
    {
        using var connection = database.OpenConnection();
        return Read(connection, null, start, end);
    }

    private static DayEventOrder Read(SqliteConnection connection, SqliteTransaction? tx, DateOnly start, DateOnly end)
    {
        var result = new DayEventOrder();
        using var command = Sql.Command(connection, tx, """
            SELECT day, event_key, position FROM day_event_order WHERE day BETWEEN $start AND $end;
            """, ("$start", Format(start)), ("$end", Format(end)));
        using var reader = command.ExecuteReader();
        while (reader.Read())
            result._positions[(DateOnly.ParseExact(reader.GetString(0), "yyyy-MM-dd", CultureInfo.InvariantCulture), reader.GetString(1))] = reader.GetInt32(2);
        return result;
    }

    // 여러 날 일정이 먼저다. 시작일순이고 같은 날 시작하면 긴 것이 먼저다(달력 막대 레인과 같은 순서). 사용자 순서는 단일 일정에만 쓴다.
    public IOrderedEnumerable<EventOccurrence> Sort(DateOnly date, IEnumerable<EventOccurrence> items) =>
        items.OrderBy(o => IsPeriod(o) ? 0 : 1)
            .ThenBy(o => IsPeriod(o) ? o.Start.Date : DateTime.MinValue)
            .ThenByDescending(o => IsPeriod(o) ? o.End.AddTicks(-1).Date : DateTime.MinValue)
            .ThenBy(o => IsPeriod(o) ? 0 : _positions.GetValueOrDefault((date, Key(o)), int.MaxValue))
            .ThenBy(o => o.Event.CreatedAt, StringComparer.Ordinal)
            .ThenBy(Key, StringComparer.Ordinal);

    // 현재 보이는 두 항목을 다시 검사한다. 중간에 지워진 일정이나 다른 날짜의 회차는 옮길 수 없다.
    public static bool Move(EventRepository events, DateOnly date, string source, string target)
    {
        if (source == target) return false;
        using var connection = events.Database.OpenConnection();
        using var tx = connection.BeginTransaction(deferred: false);
        var order = Read(connection, tx, date, date);
        var keys = order.Sort(date, events.List(date, date).Where(o => !IsPeriod(o))).Select(Key).ToList();
        var from = keys.IndexOf(source);
        var to = keys.IndexOf(target);
        if (from < 0 || to < 0) return false;
        keys.RemoveAt(from);
        keys.Insert(to, source);
        var day = Format(date);
        using var version = Sql.Command(connection, tx,
            "SELECT COALESCE(MAX(revision),0)+1 FROM day_event_order WHERE day=$day", ("$day", day));
        var revision = (long)version.ExecuteScalar()!;
        using var clear = Sql.Command(connection, tx, "DELETE FROM day_event_order WHERE day=$day", ("$day", day));
        clear.ExecuteNonQuery();
        for (var i = 0; i < keys.Count; i++)
        {
            using var insert = Sql.Command(connection, tx,
                "INSERT INTO day_event_order(day,event_key,position,revision) VALUES ($day,$key,$pos,$rev)",
                ("$day", day), ("$key", keys[i]), ("$pos", i), ("$rev", revision));
            insert.ExecuteNonQuery();
        }
        tx.Commit();
        return true;
    }

    private static string Format(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
