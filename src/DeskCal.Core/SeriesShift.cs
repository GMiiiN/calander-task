using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace DeskCal.Core;

/// <summary>
/// 반복 일정의 시각·반복이 바뀌어 회차 키(원래 시작 시각)가 달라졌을 때(S5), 옛 회차의 기록(완료·체크리스트·회차 삭제)을 새 회차로 옮긴다.
/// 대응은 시리즈 ID와 원래 회차의 날짜로 정한다. 시리즈 시간대로 같은 날에 새 회차가 정확히 하나이고 다른 옛 회차와 겹치지 않을 때만 유일하다.
/// 날짜가 비슷하다는 이유만으로 다른 날 회차에 붙이지 않는다(계획 §3). 유일하지 않으면 옮기지 않고 예외를 그대로 둔 채 충돌 기록에 남긴다.
/// Google은 시리즈 시각이 바뀌면 예외를 모두 버리므로, 옮긴 회차는 새 인스턴스로 다시 올린다.
/// </summary>
internal static class SeriesShift
{
    // 옛 회차 키 → 새 회차 키(유일하지 않으면 null)
    public static Dictionary<string, string?> Map(EventRecord series, IEnumerable<string> oldKeys, TimeZoneInfo localZone)
    {
        var expander = new RecurrenceExpander(localZone);
        var zone = Zone(series, localZone);
        var visible = series with { DeletedAt = null, Status = "confirmed" };
        var map = new Dictionary<string, string?>();
        foreach (var key in oldKeys.Distinct())
        {
            var day = DateOf(key, zone);
            var candidates = expander.Expand(visible, [], day.AddDays(-1), day.AddDays(1))
                .Select(o => OccurrenceId.Parse(o.OccurrenceId!).OriginalStartKey).Where(k => DateOf(k, zone) == day).Distinct().ToList();
            map[key] = candidates.Count == 1 ? candidates[0] : null;
        }
        foreach (var crowded in map.Where(pair => pair.Value is not null).GroupBy(pair => pair.Value).Where(g => g.Count() > 1).SelectMany(g => g).ToList())
            map[crowded.Key] = null;
        return map;
    }

    // 지금 시리즈가 만드는 회차의 키인지(아니면 시각이 바뀌어 남은 옛 회차다).
    public static bool IsCurrent(EventRecord series, string key, TimeZoneInfo localZone) =>
        new RecurrenceExpander(localZone).GetOriginal(series with { DeletedAt = null, Status = "confirmed" }, key) is not null;

    /// <summary>
    /// 예외 행을 새 회차 키로 옮긴다. 완료·체크리스트·D-day·회차 삭제는 그대로이고 시각은 새 회차를 따른다(개별 이동도 Google이 버렸으므로).
    /// Google ID·etag를 비워 새 인스턴스에 다시 올리고, 병합 기준값은 Google이 새로 만든 평범한 인스턴스 값으로 둔다(S4 병합이 이 PC 값을 남긴다).
    /// </summary>
    public static void Rekey(SqliteConnection connection, SqliteTransaction transaction, EventRecord series, EventRecord exception, string newKey,
        TimeZoneInfo localZone, string now)
    {
        var occurrence = new RecurrenceExpander(localZone).GetOriginal(series with { DeletedAt = null, Status = "confirmed" }, newKey)
            ?? throw new InvalidOperationException("Occurrence not found in series.");
        var plain = occurrence.Event with { RecurrenceRule = null, Status = "confirmed" };
        using var command = Sql.Command(connection, transaction, """
            UPDATE event SET original_start_key=$key, google_event_id=NULL, google_etag=NULL, google_recurring_id=$series,
                google_start_raw=$start, google_end_raw=$end, time_zone=$zone, start_date=$startDate, end_date=$endDate,
                start_time=$startTime, end_time=$endTime, sync_base=$base, is_dirty=1, push_state='pending',
                local_revision=local_revision+1, attempt_count=0, last_error=NULL, updated_at=$now
            WHERE event_id=$id;
            """, ("$key", newKey), ("$series", series.GoogleEventId), ("$start", plain.GoogleStartRaw), ("$end", plain.GoogleEndRaw),
            ("$zone", plain.TimeZone), ("$startDate", plain.StartDate), ("$endDate", plain.EndDate), ("$startTime", plain.StartTime),
            ("$endTime", plain.EndTime), ("$base", EventFields.Of(plain).ToJsonString()), ("$now", now), ("$id", exception.EventId));
        command.ExecuteNonQuery();
    }

    // 옮기지 못한 옛 회차를 한 번만 충돌 기록에 남긴다. 행은 그대로 둔다(기록 보존).
    public static void Review(SqliteConnection connection, SqliteTransaction transaction, EventRecord exception, string now)
    {
        using var seen = Sql.Command(connection, transaction,
            "SELECT EXISTS(SELECT 1 FROM sync_conflict WHERE item_uid=$uid AND detail_json LIKE '%\"orphan\"%');", ("$uid", exception.EventUid));
        if ((long)seen.ExecuteScalar()! != 0) return;
        using var insert = Sql.Command(connection, transaction,
            "INSERT INTO sync_conflict(item_kind, item_uid, discarded_json, occurred_at, detail_json) VALUES ('event', $uid, $json, $at, $detail);",
            ("$uid", exception.EventUid), ("$json", JsonSerializer.Serialize(exception, TaskSync.ConflictJson)), ("$at", now),
            ("$detail", "{\"fields\":[],\"orphan\":\"series_changed\"}"));
        insert.ExecuteNonQuery();
    }

    private static DateOnly DateOf(string key, TimeZoneInfo zone) => key.Length == 10
        ? DateOnly.ParseExact(key, "yyyy-MM-dd", CultureInfo.InvariantCulture)
        : DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.Parse(key, CultureInfo.InvariantCulture), zone).DateTime);

    // 같은 날은 시리즈의 시간대로 본다(사용자가 정한 요일·날짜). 종일 일정은 날짜 그대로다.
    private static TimeZoneInfo Zone(EventRecord series, TimeZoneInfo localZone)
    {
        if (series.TimeZone is not { } id) return localZone;
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch (Exception error) when (error is TimeZoneNotFoundException or InvalidTimeZoneException) { return localZone; }
    }
}
