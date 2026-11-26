using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace DeskCal.Core;

/// <summary>
/// 하루 안 일정 순서(U6, 기기 사이 공유는 S8). 여러 날 일정은 위에 고정이고, 단일 일정(반복은 그 회차)만 순서를 바꾼다.
/// 순서는 공유 설정(SharedSettings)의 day_order:&lt;날짜&gt; 항목에 두 PC가 같은 일정을 가리키는 키(RemoteKey) 목록으로 둔다.
/// 그래서 Google에서 직접 만든 일정처럼 PC마다 로컬 UID가 달라도 같은 순서가 된다. 목록에 없는 일정(새 일정)은 그 뒤에 생성순이다.
/// S8 이전에 이 PC에만 저장한 순서(day_event_order 표)는 공유 항목이 없는 날짜에만 쓰고, Google에 연결하면 공유 항목으로 옮긴다.
/// </summary>
public sealed class DayEventOrder
{
    public const string Prefix = "day_order:";
    private readonly Dictionary<DateOnly, Dictionary<string, int>> _shared = [];
    private readonly Dictionary<(DateOnly, string), int> _legacy = [];

    // 이 PC의 공개 ID(MCP event_id 또는 occurrence_id)
    public static string Key(EventOccurrence o) => o.OccurrenceId ?? o.Event.EventUid;

    // 두 PC에서 같은 일정·회차를 가리키는 키. 단일 일정은 Google 일정 ID(이 PC에서 만든 일정은 올리기 전에 미리 정한 ID),
    // 반복 회차는 시리즈의 Google ID와 원래 회차 키다. 로컬 행 번호나 PC마다 다른 로컬 UID는 쓰지 않는다.
    public static string RemoteKey(EventOccurrence o)
    {
        if (o.OccurrenceId is not { } id) return o.Event.GoogleEventId ?? o.Event.EventUid;
        var (uid, key) = OccurrenceId.Parse(id);
        return $"{o.Event.GoogleRecurringId ?? o.Event.GoogleEventId ?? uid}~{key}";
    }

    public static bool IsPeriod(EventOccurrence o) => o.Start.Date < o.End.AddTicks(-1).Date;

    public static DayEventOrder Read(Database database, DateOnly start, DateOnly end)
    {
        using var connection = database.OpenConnection();
        return Read(connection, null, start, end);
    }

    private static DayEventOrder Read(SqliteConnection connection, SqliteTransaction? tx, DateOnly start, DateOnly end)
    {
        var result = new DayEventOrder();
        using (var shared = Sql.Command(connection, tx, "SELECT entry_key, value_json FROM shared_entry WHERE entry_key BETWEEN $start AND $end;",
            ("$start", Prefix + Format(start)), ("$end", Prefix + Format(end))))
        using (var reader = shared.ExecuteReader())
            while (reader.Read())
                if (DateOnly.TryParseExact(reader.GetString(0)[Prefix.Length..], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
                {
                    var positions = new Dictionary<string, int>();
                    foreach (var key in Keys(reader.GetString(1))) positions.TryAdd(key, positions.Count);
                    result._shared[day] = positions;
                }
        using var legacy = Sql.Command(connection, tx, """
            SELECT day, event_key, position FROM day_event_order WHERE day BETWEEN $start AND $end;
            """, ("$start", Format(start)), ("$end", Format(end)));
        using var rows = legacy.ExecuteReader();
        while (rows.Read())
            result._legacy[(DateOnly.ParseExact(rows.GetString(0), "yyyy-MM-dd", CultureInfo.InvariantCulture), rows.GetString(1))] = rows.GetInt32(2);
        return result;
    }

    // 공유 항목 값: RemoteKey 문자열 배열. 다른 형식이면 빈 목록(생성순)으로 본다.
    private static IReadOnlyList<string> Keys(string json)
    {
        try
        {
            return JsonNode.Parse(json) is JsonArray array
                ? array.Select(node => node is JsonValue value && value.TryGetValue<string>(out var key) ? key : null).OfType<string>().ToArray()
                : [];
        }
        catch (JsonException) { return []; }
    }

    private int Position(DateOnly date, EventOccurrence o) => _shared.TryGetValue(date, out var order)
        ? order.GetValueOrDefault(RemoteKey(o), int.MaxValue)
        : _legacy.GetValueOrDefault((date, Key(o)), int.MaxValue);

    // 여러 날 일정이 먼저다. 시작일순이고 같은 날 시작하면 긴 것이 먼저다(달력 막대 레인과 같은 순서). 사용자 순서는 단일 일정에만 쓴다.
    // 단일 일정은 완료한 것을 아래로 보낸다(v0.8.2와 같다). 그 안에서 사용자 순서 → 생성순 → 두 PC에 같은 키 순이다.
    public IOrderedEnumerable<EventOccurrence> Sort(DateOnly date, IEnumerable<EventOccurrence> items) =>
        items.OrderBy(o => IsPeriod(o) ? 0 : 1)
            .ThenBy(o => IsPeriod(o) ? o.Start.Date : DateTime.MinValue)
            .ThenByDescending(o => IsPeriod(o) ? o.End.AddTicks(-1).Date : DateTime.MinValue)
            .ThenBy(o => !IsPeriod(o) && o.Event.IsDone)
            .ThenBy(o => IsPeriod(o) ? 0 : Position(date, o))
            .ThenBy(o => o.Event.CreatedAt, StringComparer.Ordinal)
            .ThenBy(RemoteKey, StringComparer.Ordinal)
            .ThenBy(Key, StringComparer.Ordinal);

    // 현재 보이는 두 항목을 다시 검사한다. 중간에 지워진 일정이나 다른 날짜의 회차는 옮길 수 없다.
    public static bool Move(EventRepository events, DateOnly date, string source, string target)
    {
        if (source == target) return false;
        using var connection = events.Database.OpenConnection();
        using var tx = connection.BeginTransaction(deferred: false);
        var items = Singles(connection, tx, events, date);
        var from = items.FindIndex(o => Key(o) == source);
        var to = items.FindIndex(o => Key(o) == target);
        if (from < 0 || to < 0) return false;
        // 완료한 일정은 늘 미완료 아래에 있다. 완료 상태가 다른 자리로 옮기면 보이는 순서가 바뀌지 않으므로 받지 않는다.
        if (items[from].Event.IsDone != items[to].Event.IsDone) return false;
        var moved = items[from];
        items.RemoveAt(from);
        items.Insert(to, moved);
        Save(connection, tx, date, items);
        tx.Commit();
        return true;
    }

    /// <summary>
    /// 그날 단일 일정 전체의 새 순서(Claude의 reorder_events). 그날 순서를 바꿀 수 있는 일정을 모두 한 번씩 받아야 한다.
    /// 여러 날 일정, 다른 날의 일정, 중복, 빠지거나 낡은 목록, 미완료보다 앞선 완료 일정은 받지 않는다. 바뀐 순서를 돌려준다.
    /// </summary>
    public static IReadOnlyList<EventOccurrence> Reorder(EventRepository events, DateOnly date, IReadOnlyList<string> ids)
    {
        using var connection = events.Database.OpenConnection();
        using var tx = connection.BeginTransaction(deferred: false);
        var items = Singles(connection, tx, events, date);
        string Current() => string.Join(", ", items.Select(o => $"{Key(o)} ({o.Event.Title}{(o.Event.IsDone ? ", done" : "")})"));
        if (ids.Distinct(StringComparer.Ordinal).Count() != ids.Count) throw new ArgumentException("event_ids must not repeat an ID.");
        var day = Format(date);
        foreach (var id in ids.Where(id => items.All(o => Key(o) != id)))
        {
            if (events.List(date, date).Any(o => IsPeriod(o) && Key(o) == id))
                throw new ArgumentException($"{id} is a multi-day event. Multi-day events stay fixed above single-day events on {day}; leave them out.");
            throw new ArgumentException($"{id} is not a single-day event on {day}. Current order on {day}: {Current()}.");
        }
        if (ids.Count != items.Count)
            throw new ArgumentException($"event_ids must list every single-day event on {day} exactly once; the list is incomplete or stale. Current order: {Current()}.");
        var ordered = ids.Select(id => items.Single(o => Key(o) == id)).ToList();
        if (ordered.SkipWhile(o => !o.Event.IsDone).FirstOrDefault(o => !o.Event.IsDone) is { } open)
            throw new ArgumentException($"Completed events always stay below open ones. Put {Key(open)} ({open.Event.Title}) before every completed event.");
        Save(connection, tx, date, ordered);
        tx.Commit();
        return ordered;
    }

    // 그날 단일 일정을 지금 보이는 순서로(트랜잭션 안에서 다시 읽는다).
    private static List<EventOccurrence> Singles(SqliteConnection connection, SqliteTransaction tx, EventRepository events, DateOnly date) =>
        Read(connection, tx, date, date).Sort(date, events.List(date, date).Where(o => !IsPeriod(o))).ToList();

    // 한 날짜의 순서를 공유 항목 하나로 원자적으로 저장한다. 그날 이 PC에만 있던 예전 순서는 지운다.
    private static void Save(SqliteConnection connection, SqliteTransaction tx, DateOnly date, IEnumerable<EventOccurrence> ordered)
    {
        SharedSettings.Edit(connection, tx, Prefix + Format(date), new JsonArray(ordered.Select(o => (JsonNode)JsonValue.Create(RemoteKey(o))).ToArray()),
            Validation.Timestamp(TimeProvider.System));
        using var clear = Sql.Command(connection, tx, "DELETE FROM day_event_order WHERE day=$day", ("$day", Format(date)));
        clear.ExecuteNonQuery();
    }

    /// <summary>
    /// S8 이전에 이 PC에만 저장한 날짜별 순서를 공유 항목으로 옮긴다(Google 공유 설정 동기화 때). 다른 기기가 이미 그날 순서를
    /// 올렸으면 합칠 때 원격 우선이다. 옮긴 날짜의 예전 행은 지운다.
    /// </summary>
    internal static void SeedLegacy(SqliteConnection connection, SqliteTransaction tx, EventRepository events)
    {
        var days = new List<string>();
        using (var command = Sql.Command(connection, tx, """
            SELECT DISTINCT day FROM day_event_order
            WHERE NOT EXISTS (SELECT 1 FROM shared_entry WHERE entry_key = 'day_order:' || day) ORDER BY day;
            """))
        using (var reader = command.ExecuteReader())
            while (reader.Read()) days.Add(reader.GetString(0));
        foreach (var text in days)
        {
            if (!DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) continue;
            var order = Read(connection, tx, date, date);
            Save(connection, tx, date, order.Sort(date, events.List(date, date).Where(o => !IsPeriod(o))).Where(o => order._legacy.ContainsKey((date, Key(o)))));
        }
    }

    private static string Format(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
