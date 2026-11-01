using System.Globalization;
using System.Net;
using System.Text.Json;
using Google;
using Google.Apis.Calendar.v3;
using Google.Apis.Calendar.v3.Data;
using Microsoft.Data.Sqlite;

namespace DeskCal.Core;

/// <summary>Google Calendar 기본 캘린더 호출. 테스트에서는 가짜로 바꾼다.</summary>
public interface ICalendarRemote
{
    // syncToken이 없으면 전체(삭제 포함), 있으면 그 뒤 바뀐 것만. 모든 페이지를 받은 뒤 다음 토큰과 함께 돌려준다. 토큰이 만료되면 410.
    Task<(IReadOnlyList<Event> Items, string? NextSyncToken)> ListAsync(string? syncToken, CancellationToken cancellationToken);
    Task<Event> GetAsync(string eventId, CancellationToken cancellationToken);
    Task<Event> InsertAsync(Event value, CancellationToken cancellationToken);
    Task<Event> PatchAsync(string eventId, Event value, string? etag, CancellationToken cancellationToken);
    Task DeleteAsync(string eventId, string? etag, CancellationToken cancellationToken);
}

public sealed class GoogleCalendarRemote(CalendarService service) : ICalendarRemote
{
    private const string PRIMARY = "primary";

    public async Task<(IReadOnlyList<Event> Items, string? NextSyncToken)> ListAsync(string? syncToken, CancellationToken cancellationToken)
    {
        var items = new List<Event>();
        string? page = null;
        string? next = null;
        do
        {
            var request = service.Events.List(PRIMARY);
            request.SingleEvents = false;
            request.ShowDeleted = true;
            request.MaxResults = 250;
            request.SyncToken = syncToken;
            request.PageToken = page;
            var result = await request.ExecuteAsync(cancellationToken);
            items.AddRange(result.Items ?? []);
            page = result.NextPageToken;
            next = result.NextSyncToken;
        } while (page is not null);
        return (items, next);
    }

    public Task<Event> GetAsync(string eventId, CancellationToken cancellationToken) => service.Events.Get(PRIMARY, eventId).ExecuteAsync(cancellationToken);

    public Task<Event> InsertAsync(Event value, CancellationToken cancellationToken) => service.Events.Insert(value, PRIMARY).ExecuteAsync(cancellationToken);

    public Task<Event> PatchAsync(string eventId, Event value, string? etag, CancellationToken cancellationToken)
    {
        var request = service.Events.Patch(value, PRIMARY, eventId);
        if (etag is not null) request.ModifyRequest = message => message.Headers.TryAddWithoutValidation("If-Match", etag);
        return request.ExecuteAsync(cancellationToken);
    }

    public Task DeleteAsync(string eventId, string? etag, CancellationToken cancellationToken)
    {
        var request = service.Events.Delete(PRIMARY, eventId);
        if (etag is not null) request.ModifyRequest = message => message.Headers.TryAddWithoutValidation("If-Match", etag);
        return request.ExecuteAsync(cancellationToken);
    }
}

/// <summary>
/// 일정 동기화(§5.3). 매번 push → pull. pull은 syncToken으로 바뀐 것만 받고, 토큰이 없거나 만료(410)되면 전체를 받아 대조한다.
/// DeskCal 전용 값(완료, D-day, 로컬 UID)은 extendedProperties.private에 싣는다.
/// 충돌(412)은 원격 우선: 로컬 변경을 sync_conflict에 남기고 원격 값을 받는다.
/// </summary>
public sealed class EventSync(Database database, ICalendarRemote remote, TimeZoneInfo? localTimeZone = null, TimeProvider? clock = null)
{
    private const string SYNC_KEY = "calendar_primary";
    private readonly EventTime _time = new(localTimeZone);
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    // 화면 아래 동기화 상태 표시용. 일정을 마지막으로 받은 때.
    public static DateTimeOffset? LastSyncedAt(Database database)
    {
        using var connection = database.OpenConnection();
        using var command = Sql.Command(connection, null, "SELECT last_synced_at FROM sync_state WHERE sync_key=$key;", ("$key", SYNC_KEY));
        return command.ExecuteScalar() is string text ? DateTimeOffset.Parse(text, CultureInfo.InvariantCulture) : null;
    }

    public async Task SyncAsync(CancellationToken cancellationToken)
    {
        await PushAsync(cancellationToken);
        var token = ReadSyncToken();
        try
        {
            var (items, next) = await remote.ListAsync(token, cancellationToken);
            Pull(items, next, fullList: token is null);
        }
        catch (GoogleApiException exception) when (exception.HttpStatusCode == HttpStatusCode.Gone && token is not null)
        {
            // 토큰이 만료됐다. 전체를 다시 받아 대조한다.
            var (items, next) = await remote.ListAsync(null, cancellationToken);
            Pull(items, next, fullList: true);
        }
    }

    // ---- push ----

    private async Task PushAsync(CancellationToken cancellationToken)
    {
        var records = ReadAll();
        foreach (var snapshot in records.Where(r => r.Record.IsDirty).ToList())
        {
            var value = snapshot.Record;
            try
            {
                if (value.ParentEventId is { } parentId)
                {
                    // 예외 회차: 시리즈가 Google에 올라간 뒤에만 회차 인스턴스를 고친다.
                    var parent = records.Single(r => r.Record.EventId == parentId);
                    if (parent.Etag is null || parent.Record.GoogleEventId is null) continue;
                    var instanceId = value.GoogleEventId ?? parent.Record.GoogleEventId + "_" + InstanceSuffix(value.OriginalStartKey!);
                    var patched = await remote.PatchAsync(instanceId, ToRemote(value, isInstance: true), snapshot.Etag, cancellationToken);
                    Apply(snapshot, patched.Id, patched.ETag);
                }
                else if (value.DeletedAt is not null)
                {
                    if (snapshot.Etag is not null) await DeleteRemoteAsync(value.GoogleEventId!, snapshot.Etag, cancellationToken);
                    Apply(snapshot, value.GoogleEventId, null);
                }
                else if (snapshot.Etag is null)
                {
                    await InsertRemoteAsync(snapshot, cancellationToken);
                }
                else
                {
                    var patched = await remote.PatchAsync(value.GoogleEventId!, ToRemote(value, isInstance: false), snapshot.Etag, cancellationToken);
                    Apply(snapshot, patched.Id, patched.ETag);
                }
            }
            catch (GoogleApiException exception) when (exception.HttpStatusCode is HttpStatusCode.PreconditionFailed or HttpStatusCode.NotFound or HttpStatusCode.Gone)
            {
                Discard(snapshot);
            }
            catch (GoogleApiException exception)
            {
                MarkFailed(snapshot, exception.Message);
            }
        }
    }

    // ID를 미리 정해 두었으므로(§5.3) 응답을 못 받고 다시 보내도 409가 나서 중복이 생기지 않는다.
    private async Task InsertRemoteAsync(Stored snapshot, CancellationToken cancellationToken)
    {
        var value = snapshot.Record;
        try
        {
            var created = await remote.InsertAsync(ToRemote(value, isInstance: false), cancellationToken);
            Apply(snapshot, created.Id, created.ETag);
        }
        catch (GoogleApiException exception) when (exception.HttpStatusCode == HttpStatusCode.Conflict)
        {
            var existing = await remote.GetAsync(value.GoogleEventId!, cancellationToken);
            var isOurs = existing.ExtendedProperties?.Private__ is { } p && p.TryGetValue("deskcalUid", out var uid) && uid == value.EventUid;
            if (!isOurs)
            {
                MarkFailed(snapshot, "Event ID is already used on Google.");
                return;
            }
            if (existing.Status == "cancelled")
            {
                // 지웠던 일정을 되살렸다. 지운 ID는 다시 만들 수 없으므로 상태를 되돌리는 patch로 복구한다(M0.5 확인).
                var restored = await remote.PatchAsync(value.GoogleEventId!, ToRemote(value, isInstance: false), existing.ETag, cancellationToken);
                Apply(snapshot, restored.Id, restored.ETag);
                return;
            }
            // 지난번 생성이 실제로는 성공했다. 연결하고 dirty를 남겨 다음 회차에 최신 내용을 patch한다.
            LinkOnly(snapshot, existing.ETag);
        }
    }

    private async Task DeleteRemoteAsync(string eventId, string etag, CancellationToken cancellationToken)
    {
        try
        {
            await remote.DeleteAsync(eventId, etag, cancellationToken);
        }
        catch (GoogleApiException exception) when (exception.HttpStatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
        {
            // 이미 지워졌다.
        }
    }

    private static Event ToRemote(EventRecord value, bool isInstance)
    {
        var result = new Event
        {
            Id = isInstance ? null : value.GoogleEventId, Summary = value.Title, Description = value.Memo, Status = value.Status,
            Start = ToRemoteTime(value.GoogleStartRaw!, value.TimeZone), End = ToRemoteTime(value.GoogleEndRaw!, value.TimeZone),
            ColorId = value.ColorId, Transparency = value.IsBusy ? "opaque" : "transparent",
            ExtendedProperties = new Event.ExtendedPropertiesData
            {
                Private__ = new Dictionary<string, string>
                {
                    ["deskcalUid"] = value.EventUid, ["deskcalDone"] = value.IsDone ? "1" : "0", ["deskcalDday"] = value.IsDday ? "1" : "0",
                },
            },
        };
        // 반복을 없앤 경우에도 반영되도록 시리즈 본체는 빈 목록까지 보낸다.
        if (!isInstance) result.Recurrence = value.RecurrenceRule?.Split('\n', StringSplitOptions.RemoveEmptyEntries) ?? [];
        return result;
    }

    private static EventDateTime ToRemoteTime(string raw, string? timeZone) =>
        raw.Length == 10 ? new EventDateTime { Date = raw } : new EventDateTime { DateTimeRaw = raw, TimeZone = timeZone };

    // 회차 키(종일 'YYYY-MM-DD', 시간 'YYYY-MM-DDTHH:MM:SSZ')를 Google 인스턴스 ID 꼬리로 바꾼다.
    internal static string InstanceSuffix(string key) => key.Length == 10 ? key.Replace("-", "") : key.Replace("-", "").Replace(":", "");

    private void Apply(Stored snapshot, string? googleId, string? etag)
    {
        using var connection = database.OpenConnection();
        using var transaction = connection.BeginTransaction(deferred: false);
        var same = CurrentRevision(connection, transaction, snapshot.Record.EventId) == snapshot.Record.LocalRevision;
        using var command = Sql.Command(connection, transaction,
            "UPDATE event SET google_event_id=$id, google_etag=$etag, attempt_count=0, last_error=NULL" +
            (same ? ", is_dirty=0, push_state='synced'" : "") + " WHERE event_id=$event;",
            ("$id", googleId), ("$etag", etag), ("$event", snapshot.Record.EventId));
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    private void LinkOnly(Stored snapshot, string? etag)
    {
        using var connection = database.OpenConnection();
        using var command = Sql.Command(connection, null, "UPDATE event SET google_etag=$etag WHERE event_id=$event;",
            ("$etag", etag), ("$event", snapshot.Record.EventId));
        command.ExecuteNonQuery();
    }

    private void Discard(Stored snapshot)
    {
        using var connection = database.OpenConnection();
        using var transaction = connection.BeginTransaction(deferred: false);
        using (var conflict = Sql.Command(connection, transaction,
            "INSERT INTO sync_conflict(item_kind, item_uid, discarded_json, occurred_at) VALUES ('event', $uid, $json, $at);",
            ("$uid", snapshot.Record.EventUid), ("$json", JsonSerializer.Serialize(snapshot.Record, TaskSync.ConflictJson)), ("$at", Validation.Timestamp(_clock))))
            conflict.ExecuteNonQuery();
        // etag를 비워 다음 pull이 원격 값을 덮게 한다. 증분 조회에 안 올 수 있으므로 토큰도 지워 전체 대조를 한 번 한다.
        using var command = Sql.Command(connection, transaction,
            "UPDATE event SET is_dirty=0, push_state='synced', google_etag=NULL, attempt_count=0, last_error=NULL WHERE event_id=$event;" +
            "UPDATE sync_state SET sync_token=NULL WHERE sync_key=$key;",
            ("$event", snapshot.Record.EventId), ("$key", SYNC_KEY));
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    private void MarkFailed(Stored snapshot, string error)
    {
        using var connection = database.OpenConnection();
        using var command = Sql.Command(connection, null,
            "UPDATE event SET push_state='failed', attempt_count=attempt_count+1, last_error=$error WHERE event_id=$event;",
            ("$error", error), ("$event", snapshot.Record.EventId));
        command.ExecuteNonQuery();
    }

    // ---- pull ----

    private void Pull(IReadOnlyList<Event> items, string? nextSyncToken, bool fullList)
    {
        var now = Validation.Timestamp(_clock);
        using var connection = database.OpenConnection();
        using var transaction = connection.BeginTransaction(deferred: false);
        var byGoogleId = new Dictionary<string, Stored>();
        foreach (var stored in ReadAll(connection, transaction))
            if (stored.Record.GoogleEventId is { } id) byGoogleId[id] = stored;
        var usedUids = new HashSet<string>(ReadAll(connection, transaction).Select(s => s.Record.EventUid));

        // 예외는 부모보다 먼저 올 수 있으므로 시리즈 본체를 먼저 적용한다.
        foreach (var item in items.OrderBy(i => i.RecurringEventId is null ? 0 : 1))
        {
            if (item.Id is null) continue;
            byGoogleId.TryGetValue(item.Id, out var local);
            // 이 PC에서 만든 예외 회차는 올리기 전까지 Google ID가 없다. 시리즈와 회차 키로 찾는다.
            if (local is null && item.RecurringEventId is not null && item.OriginalStartTime is not null
                && byGoogleId.TryGetValue(item.RecurringEventId, out var series))
            {
                var key = OriginalKey(item.OriginalStartTime);
                local = byGoogleId.Values.Concat(ReadAll(connection, transaction))
                    .FirstOrDefault(s => s.Record.ParentEventId == series.Record.EventId && s.Record.OriginalStartKey == key);
            }
            if (local is not null && (local.Record.IsDirty || local.Record.PushState != "synced")) continue;
            if (item.Status == "cancelled")
            {
                ApplyCancelled(connection, transaction, item, local, byGoogleId, now);
                continue;
            }
            var value = FromRemote(item, local?.Record, byGoogleId, usedUids, now);
            if (local is null)
            {
                var id = Sql.Insert(connection, transaction, "event", Values(value, item.ETag));
                byGoogleId[item.Id] = new Stored(value with { EventId = id }, item.ETag);
            }
            else if (local.Etag != item.ETag || local.Record.DeletedAt is not null)
            {
                Sql.Update(connection, transaction, "event", local.Record.EventId, Values(value, item.ETag));
                byGoogleId[item.Id] = new Stored(value, item.ETag);
            }
        }

        // 시리즈보다 먼저(또는 다른 회차에) 받은 예외 회차를 시리즈에 잇는다.
        using (var command = Sql.Command(connection, transaction,
            "UPDATE event SET parent_event_id=(SELECT p.event_id FROM event p WHERE p.google_event_id=event.google_recurring_id) " +
            "WHERE parent_event_id IS NULL AND google_recurring_id IS NOT NULL;"))
            command.ExecuteNonQuery();

        if (fullList)
        {
            // 전체 대조: 한 번 올라갔던(etag가 있던) 동기화된 행 중 원격에 없는 것은 다른 곳에서 지운 것이다.
            var remoteIds = items.Where(i => i.Id is not null).Select(i => i.Id!).ToHashSet();
            foreach (var stored in byGoogleId.Values)
            {
                var record = stored.Record;
                if (remoteIds.Contains(record.GoogleEventId!) || record.IsDirty || record.PushState != "synced" || stored.Etag is null || record.DeletedAt is not null) continue;
                using var command = Sql.Command(connection, transaction, "UPDATE event SET deleted_at=$now, updated_at=$now WHERE event_id=$id;",
                    ("$now", now), ("$id", record.EventId));
                command.ExecuteNonQuery();
            }
        }

        using (var command = Sql.Command(connection, transaction,
            "INSERT INTO sync_state(sync_key, sync_token, last_synced_at) VALUES ($key, $token, $at) " +
            "ON CONFLICT(sync_key) DO UPDATE SET sync_token=excluded.sync_token, last_synced_at=excluded.last_synced_at;",
            ("$key", SYNC_KEY), ("$token", nextSyncToken), ("$at", now)))
            command.ExecuteNonQuery();
        transaction.Commit();
    }

    // 삭제 항목은 ID(취소 회차는 recurringEventId·originalStartTime)만 보장된다. 기존 값은 두고 상태만 바꾼다.
    private void ApplyCancelled(SqliteConnection connection, SqliteTransaction transaction, Event item, Stored? local,
        Dictionary<string, Stored> byGoogleId, string now)
    {
        if (local is not null)
        {
            var isOccurrence = local.Record.ParentEventId is not null;
            using var command = Sql.Command(connection, transaction,
                (isOccurrence ? "UPDATE event SET status='cancelled'" : "UPDATE event SET deleted_at=COALESCE(deleted_at, $now)") +
                ", google_etag=$etag, updated_at=$now WHERE event_id=$id;",
                ("$now", now), ("$etag", item.ETag), ("$id", local.Record.EventId));
            command.ExecuteNonQuery();
            return;
        }
        // 로컬에 없는 취소 회차: 시리즈를 알고 있으면 그 회차를 숨기는 예외 행을 만든다. 모르는 삭제는 무시한다.
        if (item.RecurringEventId is null || item.OriginalStartTime is null || !byGoogleId.TryGetValue(item.RecurringEventId, out var parent)) return;
        var key = OriginalKey(item.OriginalStartTime);
        var original = new RecurrenceExpander(_time.LocalTimeZone).GetOriginal(parent.Record, key);
        if (original is null) return;
        var value = original.Event with
        {
            EventId = 0, EventUid = Guid.NewGuid().ToString("N"), GoogleEventId = item.Id, ParentEventId = parent.Record.EventId,
            GoogleRecurringId = item.RecurringEventId, OriginalStartKey = key, RecurrenceRule = null, Status = "cancelled",
            CreatedBy = "google", CreatedAt = now, UpdatedAt = now, DeletedAt = null, LocalRevision = 0, IsDirty = false, PushState = "synced",
        };
        var id = Sql.Insert(connection, transaction, "event", Values(value, item.ETag));
        byGoogleId[item.Id!] = new Stored(value with { EventId = id }, item.ETag);
    }

    private EventRecord FromRemote(Event item, EventRecord? local, Dictionary<string, Stored> byGoogleId, HashSet<string> usedUids, string now)
    {
        var properties = item.ExtendedProperties?.Private__;
        string? Property(string name) => properties is not null && properties.TryGetValue(name, out var text) ? text : null;
        var uid = local?.EventUid;
        if (uid is null)
        {
            // 다른 PC의 DeskCal이 만든 일정은 같은 UID를 쓴다(Claude가 보는 ID가 PC마다 같다).
            uid = Property("deskcalUid") is { Length: 32 } shared && shared.All(char.IsAsciiHexDigitLower) && !usedUids.Contains(shared)
                ? shared : Guid.NewGuid().ToString("N");
            usedUids.Add(uid);
        }
        long? parentId = null;
        string? key = null;
        if (item.RecurringEventId is not null)
        {
            if (byGoogleId.TryGetValue(item.RecurringEventId, out var parent)) parentId = parent.Record.EventId;
            if (item.OriginalStartTime is not null) key = OriginalKey(item.OriginalStartTime);
        }
        var startRaw = item.Start?.Date ?? item.Start?.DateTimeRaw ?? throw new InvalidDataException("Event has no start.");
        var endRaw = item.End?.Date ?? item.End?.DateTimeRaw ?? startRaw;
        var value = new EventRecord
        {
            EventId = local?.EventId ?? 0, EventUid = uid, GoogleEventId = item.Id, ParentEventId = parentId, GoogleRecurringId = item.RecurringEventId,
            OriginalStartKey = key, Status = item.Status ?? "confirmed", Title = item.Summary ?? "", Memo = item.Description ?? "",
            GoogleStartRaw = startRaw, GoogleEndRaw = endRaw, TimeZone = item.Start?.TimeZone ?? item.End?.TimeZone,
            RecurrenceRule = item.Recurrence is { Count: > 0 } lines ? string.Join('\n', lines) : null,
            ColorId = item.ColorId, IsBusy = item.Transparency != "transparent", IsDone = Property("deskcalDone") == "1",
            IsDday = Property("deskcalDday") == "1", CreatedBy = local?.CreatedBy ?? "google", CreatedAt = local?.CreatedAt ?? now,
            UpdatedAt = now, DeletedAt = null, LocalRevision = local?.LocalRevision ?? 0, IsDirty = false, PushState = "synced",
        };
        return _time.Derive(value);
    }

    // originalStartTime → 회차 키. 시간 일정은 UTC로 맞춘다(RecurrenceExpander와 같은 형식).
    internal static string OriginalKey(EventDateTime original) => original.Date is { } date ? date
        : EventTime.ParseInstant(original.DateTimeRaw!, original.TimeZone).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    // ---- DB ----

    private sealed record Stored(EventRecord Record, string? Etag);

    private string? ReadSyncToken()
    {
        using var connection = database.OpenConnection();
        using var command = Sql.Command(connection, null, "SELECT sync_token FROM sync_state WHERE sync_key=$key;", ("$key", SYNC_KEY));
        return command.ExecuteScalar() as string;
    }

    private List<Stored> ReadAll()
    {
        using var connection = database.OpenConnection();
        return ReadAll(connection, null);
    }

    private static List<Stored> ReadAll(SqliteConnection connection, SqliteTransaction? transaction)
    {
        using var command = Sql.Command(connection, transaction, "SELECT * FROM event ORDER BY event_id;");
        using var reader = command.ExecuteReader();
        var result = new List<Stored>();
        while (reader.Read())
        {
            var r = reader;
            var record = new EventRecord
            {
                EventId = Sql.Number(r, "event_id"), EventUid = Sql.Text(r, "event_uid")!, GoogleEventId = Sql.Text(r, "google_event_id"),
                ParentEventId = r["parent_event_id"] is DBNull ? null : Sql.Number(r, "parent_event_id"), GoogleRecurringId = Sql.Text(r, "google_recurring_id"),
                OriginalStartKey = Sql.Text(r, "original_start_key"), Status = Sql.Text(r, "status")!, Title = Sql.Text(r, "title")!, Memo = Sql.Text(r, "memo")!,
                GoogleStartRaw = Sql.Text(r, "google_start_raw"), GoogleEndRaw = Sql.Text(r, "google_end_raw"), TimeZone = Sql.Text(r, "time_zone"),
                StartDate = Sql.Text(r, "start_date")!, EndDate = Sql.Text(r, "end_date")!, StartTime = Sql.Text(r, "start_time"), EndTime = Sql.Text(r, "end_time"),
                RecurrenceRule = Sql.Text(r, "recurrence_rule"), ColorId = Sql.Text(r, "color_id"), IsBusy = Sql.Number(r, "is_busy") != 0,
                IsDone = Sql.Number(r, "is_done") != 0, IsDday = Sql.Number(r, "is_dday") != 0, CreatedBy = Sql.Text(r, "created_by")!,
                CreatedAt = Sql.Text(r, "created_at")!, UpdatedAt = Sql.Text(r, "updated_at")!, DeletedAt = Sql.Text(r, "deleted_at"),
                LocalRevision = Sql.Number(r, "local_revision"), IsDirty = Sql.Number(r, "is_dirty") != 0, PushState = Sql.Text(r, "push_state")!,
            };
            result.Add(new Stored(record, Sql.Text(r, "google_etag")));
        }
        return result;
    }

    private static long CurrentRevision(SqliteConnection connection, SqliteTransaction transaction, long eventId)
    {
        using var command = Sql.Command(connection, transaction, "SELECT local_revision FROM event WHERE event_id=$id;", ("$id", eventId));
        return (long)command.ExecuteScalar()!;
    }

    private static Dictionary<string, object?> Values(EventRecord e, string? etag) => new()
    {
        ["event_uid"] = e.EventUid, ["google_event_id"] = e.GoogleEventId, ["google_etag"] = etag, ["parent_event_id"] = e.ParentEventId,
        ["google_recurring_id"] = e.GoogleRecurringId, ["original_start_key"] = e.OriginalStartKey, ["status"] = e.Status,
        ["title"] = e.Title, ["memo"] = e.Memo, ["google_start_raw"] = e.GoogleStartRaw, ["google_end_raw"] = e.GoogleEndRaw, ["time_zone"] = e.TimeZone,
        ["start_date"] = e.StartDate, ["end_date"] = e.EndDate, ["start_time"] = e.StartTime, ["end_time"] = e.EndTime,
        ["recurrence_rule"] = e.RecurrenceRule, ["color_id"] = e.ColorId, ["is_busy"] = e.IsBusy, ["is_done"] = e.IsDone, ["is_dday"] = e.IsDday,
        ["created_by"] = e.CreatedBy, ["created_at"] = e.CreatedAt, ["updated_at"] = e.UpdatedAt, ["deleted_at"] = e.DeletedAt,
        ["local_revision"] = e.LocalRevision, ["is_dirty"] = e.IsDirty, ["push_state"] = e.PushState,
    };
}
