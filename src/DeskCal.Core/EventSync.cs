using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Google;
using Google.Apis.Calendar.v3;
using Google.Apis.Calendar.v3.Data;
using Google.Apis.Json;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json.Linq;

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
        // 체크리스트 충돌 병합은 extendedProperties만 보낸다. 이때는 시간과 색을 건드리지 않는다.
        var request = value.Start is not null && value.End is not null
            ? new EventDetailsPatchRequest(service, value, eventId)
            : service.Events.Patch(value, PRIMARY, eventId);
        if (etag is not null) request.ModifyRequest = message => message.Headers.TryAddWithoutValidation("If-Match", etag);
        return request.ExecuteAsync(cancellationToken);
    }

    private sealed class EventDetailsPatchRequest(CalendarService service, Event value, string eventId)
        : EventsResource.PatchRequest(service, value, PRIMARY, eventId)
    {
        protected override object GetBody()
        {
            var details = (Event)base.GetBody();
            // SDK DTO는 null을 생략하지만 Google PATCH는 생략된 하위 필드를 보존한다.
            var serializer = Newtonsoft.Json.JsonSerializer.Create(NewtonsoftJsonSerializer.CreateDefaultSettings());
            var body = JObject.FromObject(details, serializer);
            body["colorId"] = details.ColorId;
            foreach (var field in new[] { "start", "end" })
            {
                var time = (JObject)body[field]!;
                if (time.ContainsKey("date"))
                {
                    time["dateTime"] = null;
                    time["timeZone"] = null;
                }
                else
                {
                    time["date"] = null;
                }
            }
            return body;
        }
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

    // S8: 생성 시각을 Google의 created로 맞춘 적이 있는지. 없으면 한 번 전체를 받아 바뀌지 않은 옛 일정까지 맞춘다.
    private const string CreatedBasisKey = "event_created_basis";
    // S4: 412 병합 기준값(sync_base)을 채운 적이 있는지. 없으면 한 번 전체를 받아 바뀌지 않은 옛 행에도 채운다.
    private const string SyncBaseKey = "event_sync_base";

    public async Task SyncAsync(CancellationToken cancellationToken)
    {
        await PushAsync(cancellationToken);
        var token = HasSetting(CreatedBasisKey) && HasSetting(SyncBaseKey) ? ReadSyncToken() : null;
        try
        {
            var (items, next) = await remote.ListAsync(token, cancellationToken);
            await PullAsync(items, next, fullList: token is null, cancellationToken);
        }
        catch (GoogleApiException exception) when (exception.HttpStatusCode == HttpStatusCode.Gone && token is not null)
        {
            // 토큰이 만료됐다. 전체를 다시 받아 대조한다.
            var (items, next) = await remote.ListAsync(null, cancellationToken);
            await PullAsync(items, next, fullList: true, cancellationToken);
        }
    }

    // 전체 목록에서 빠진 동기화된 일정은 지우기 전에 ID로 한 번 더 묻는다(S4). 방금 올린 일정이나 잠깐 목록에서 빠진 살아 있는 일정을
    // 지운 것으로 보지 않는다. 살아 있으면 목록에 있던 것처럼 반영하고, 조회가 실패하면 이번에는 지우지 않는다.
    private const int MissingLookups = 50;

    private async Task PullAsync(IReadOnlyList<Event> items, string? next, bool fullList, CancellationToken cancellationToken)
    {
        var held = new HashSet<string>();
        if (fullList)
        {
            var listed = items.Where(i => i.Id is not null).Select(i => i.Id!).ToHashSet();
            List<string> missing;
            using (var connection = database.OpenConnection())
            using (var command = Sql.Command(connection, null,
                "SELECT google_event_id FROM event WHERE google_event_id IS NOT NULL AND is_dirty=0 AND push_state='synced' AND deleted_at IS NULL ORDER BY event_id;"))
            using (var reader = command.ExecuteReader())
            {
                missing = [];
                while (reader.Read()) if (!listed.Contains(reader.GetString(0))) missing.Add(reader.GetString(0));
            }
            var found = new List<Event>();
            foreach (var id in missing)
            {
                if (found.Count + held.Count >= MissingLookups)
                {
                    held.Add(id); // 다음 동기화에서 확인한다
                    continue;
                }
                try { found.Add(await remote.GetAsync(id, cancellationToken)); }
                catch (GoogleApiException exception) when (exception.HttpStatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone) { }
                catch (Exception exception) when (exception is not OperationCanceledException && !GoogleAccount.IsRevoked(exception)) { held.Add(id); }
            }
            items = items.Concat(found.Where(e => e.Id is not null)).ToList();
        }
        Pull(items, next, fullList, held, cancellationToken);
    }

    // ---- push ----

    private async Task PushAsync(CancellationToken cancellationToken)
    {
        var records = ReadPushRecords();
        foreach (var snapshot in records.Where(r => r.Record.IsDirty).ToList())
        {
            var value = snapshot.Record;
            string? patchedId = null;
            try
            {
                if (value.ParentEventId is { } parentId)
                {
                    // 예외 회차: 시리즈가 Google에 올라간 뒤에만 회차 인스턴스를 고친다.
                    var parent = records.Single(r => r.Record.EventId == parentId);
                    if (parent.Etag is null || parent.Record.GoogleEventId is null) continue;
                    var instanceId = value.GoogleEventId ?? parent.Record.GoogleEventId + "_" + InstanceSuffix(value.OriginalStartKey!);
                    patchedId = instanceId;
                    var etag = snapshot.Etag;
                    if (etag is null)
                    {
                        // 처음 고치는 회차라 비교할 etag가 없다. 마지막으로 받은 뒤 다른 곳에서 바뀐 회차면
                        // 받아 둔 옛 시각으로 덮지 않고 원격 우선으로 처리한다.
                        var current = await remote.GetAsync(instanceId, cancellationToken);
                        if (current.UpdatedDateTimeOffset is { } updated && (LastSyncedAt(database) is not { } seen || updated > seen))
                        {
                            await ResolveConflictAsync(snapshot, instanceId, cancellationToken);
                            continue;
                        }
                        etag = current.ETag;
                    }
                    var patched = await remote.PatchAsync(instanceId, ToRemote(value, isInstance: true), etag, cancellationToken);
                    Apply(snapshot, patched.Id, patched.ETag, patched);
                }
                else if (value.DeletedAt is not null)
                {
                    // etag가 없어도 지운다. 생성 응답만 못 받았고 실제로는 올라가 있을 수 있다(없으면 404로 끝난다).
                    try { await DeleteRemoteAsync(value.GoogleEventId!, snapshot.Etag, cancellationToken); }
                    catch (GoogleApiException exception) when (exception.HttpStatusCode == HttpStatusCode.PreconditionFailed
                        && FieldMerge.Parse(snapshot.Base) is { } baseline)
                    {
                        // 지우기 전에 다른 곳에서 고쳤다. 고친 내용이 있으면 삭제/수정 겹침이라 원격 우선(삭제 취소), 없으면 지금 etag로 지운다.
                        var current = await remote.GetAsync(value.GoogleEventId!, cancellationToken);
                        if (current.Status != "cancelled" && FieldMerge.Changed(baseline, EventFields.Of(current), EventFields.Same))
                        {
                            Discard(snapshot, deleted: "local");
                            continue;
                        }
                        await DeleteRemoteAsync(value.GoogleEventId!, current.ETag, cancellationToken);
                    }
                    Apply(snapshot, value.GoogleEventId, null);
                }
                else if (snapshot.Etag is null)
                {
                    await InsertRemoteAsync(snapshot, cancellationToken);
                }
                else
                {
                    patchedId = value.GoogleEventId!;
                    var patched = await remote.PatchAsync(patchedId, ToRemote(value, isInstance: false), snapshot.Etag, cancellationToken);
                    Apply(snapshot, patched.Id, patched.ETag, patched);
                }
            }
            catch (GoogleApiException exception) when (exception.HttpStatusCode == HttpStatusCode.PreconditionFailed && patchedId is not null)
            {
                await ResolveConflictAsync(snapshot, patchedId, cancellationToken);
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

    /// <summary>
    /// 다른 곳에서 일정이 먼저 바뀌었다(412). 마지막으로 맞춘 값(sync_base)이 있으면 필드 묶음별로 합쳐(S4) 서로 다른 필드는 둘 다 살리고,
    /// 같은 묶음을 둘 다 바꿨으면 Google 값을 남긴 채 이 PC 값을 충돌로 기록한다. 회차 삭제와 Google 수정이 겹치면 원격 우선(삭제 취소),
    /// Google에서 지웠으면 이 PC 변경은 원격 삭제에 밀린다. 체크리스트는 항목별로 합친다. 기준값이 없는 예전 행은 예전처럼 원격 우선이다.
    /// </summary>
    private async Task ResolveConflictAsync(Stored snapshot, string remoteId, CancellationToken cancellationToken)
    {
        var value = snapshot.Record;
        if (FieldMerge.Parse(snapshot.Base) is not { } baseline)
        {
            await ResolveWithoutBaseAsync(snapshot, remoteId, cancellationToken);
            return;
        }
        try
        {
            var current = await remote.GetAsync(remoteId, cancellationToken);
            var theirs = EventFields.Of(current);
            if (current.Status == "cancelled" && value.ParentEventId is null)
            {
                Discard(snapshot, deleted: "remote");
                return;
            }
            var mine = EventFields.Of(value);
            if (value.Status == "cancelled" && current.Status != "cancelled" && FieldMerge.Changed(baseline, theirs, EventFields.Same))
            {
                Discard(snapshot, deleted: "local");
                return;
            }
            var merged = FieldMerge.Merge(baseline, mine, theirs, EventFields.Same);
            var remoteChecklist = Checklist.ReadProperties(current.ExtendedProperties?.Private__);
            var checklist = Checklist.Merge(value.Checklist, remoteChecklist);
            var record = EventFields.Apply(value, merged.Values, _time) with { Checklist = checklist };
            // 남길 이 PC 변경이 없으면 Google 값을 그대로 받는다(보낼 것이 없다).
            var response = !FieldMerge.Changed(theirs, merged.Values, EventFields.Same) && Checklist.Serialize(checklist) == Checklist.Serialize(remoteChecklist)
                ? current
                : await remote.PatchAsync(remoteId, ToRemote(record, isInstance: value.ParentEventId is not null), current.ETag, cancellationToken);
            ApplyMerged(snapshot, record, response, merged);
        }
        catch (GoogleApiException exception) when (exception.HttpStatusCode == HttpStatusCode.PreconditionFailed)
        {
            // 합치는 사이에 또 바뀌었다(412 재발). dirty로 남겨 다음 동기화에서 다시 합친다.
        }
        catch (GoogleApiException exception) when (exception.HttpStatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
        {
            Discard(snapshot, deleted: "remote");
        }
        catch (GoogleApiException exception)
        {
            MarkFailed(snapshot, exception.Message);
        }
    }

    // 병합 결과를 반영한다. 보내는 사이 이 PC에서 또 고쳤으면, Google 쪽 값을 받은 묶음 중 다시 고치지 않은 것만 넣고 dirty로 둔다
    // (다음 push가 옛 etag로 412를 받아 새 값으로 다시 합친다). 충돌은 한 번만 기록한다.
    private void ApplyMerged(Stored snapshot, EventRecord merged, Event response, FieldMerge.Result result)
    {
        var now = Validation.Timestamp(_clock);
        using var connection = database.OpenConnection();
        using var transaction = connection.BeginTransaction(deferred: false);
        var current = ReadStored(connection, transaction, "WHERE event_id=$id", ("$id", snapshot.Record.EventId)).Single();
        if (current.Record.LocalRevision == snapshot.Record.LocalRevision)
        {
            Sql.Update(connection, transaction, "event", merged.EventId,
                Values(merged with { GoogleEventId = response.Id, IsDirty = false, PushState = "synced", UpdatedAt = now },
                    response.ETag, EventFields.Of(response).ToJsonString()));
            using var reset = Sql.Command(connection, transaction, "UPDATE event SET attempt_count=0, last_error=NULL WHERE event_id=$id;", ("$id", merged.EventId));
            reset.ExecuteNonQuery();
        }
        else
        {
            var before = EventFields.Of(snapshot.Record);
            var latest = EventFields.Of(current.Record);
            var adopted = (JsonObject)latest.DeepClone();
            foreach (var field in result.FromTheirs.Where(f => EventFields.Same(f, latest[f], before[f])))
                adopted[field] = result.Values[field]?.DeepClone();
            var pending = EventFields.Apply(current.Record, adopted, _time) with { GoogleEventId = response.Id };
            Sql.Update(connection, transaction, "event", pending.EventId, Values(pending, current.Etag, current.Base));
        }
        if (result.Conflicts.Count > 0) InsertConflict(connection, transaction, snapshot.Record, FieldMerge.Detail(result.Conflicts), now);
        transaction.Commit();
    }

    // S4 이전에 동기화한 행(기준값 없음): 일정 내용은 원격 우선이지만 체크리스트는 항목 단위로 합쳐 올린 뒤 원격을 받는다.
    private async Task ResolveWithoutBaseAsync(Stored snapshot, string remoteId, CancellationToken cancellationToken)
    {
        var value = snapshot.Record;
        if (value.Checklist.Count == 0 || value.DeletedAt is not null || value.Status == "cancelled")
        {
            Discard(snapshot);
            return;
        }
        try
        {
            var current = await remote.GetAsync(remoteId, cancellationToken);
            var merged = Checklist.Merge(value.Checklist, Checklist.ReadProperties(current.ExtendedProperties?.Private__));
            var properties = new Dictionary<string, string>();
            Checklist.WriteProperties(merged, properties);
            await remote.PatchAsync(remoteId, new Event { ExtendedProperties = new Event.ExtendedPropertiesData { Private__ = properties } }, current.ETag, cancellationToken);
            Discard(snapshot, merged);
        }
        catch (GoogleApiException exception) when (exception.HttpStatusCode == HttpStatusCode.PreconditionFailed)
        {
            // 합치는 사이에 또 바뀌었다. dirty로 남겨 다음 동기화에서 다시 합친다.
        }
        catch (GoogleApiException exception) when (exception.HttpStatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
        {
            Discard(snapshot);
        }
        catch (GoogleApiException exception)
        {
            MarkFailed(snapshot, exception.Message);
        }
    }

    // ID를 미리 정해 두었으므로(§5.3) 응답을 못 받고 다시 보내도 409가 나서 중복이 생기지 않는다.
    private async Task InsertRemoteAsync(Stored snapshot, CancellationToken cancellationToken)
    {
        var value = snapshot.Record;
        try
        {
            var created = await remote.InsertAsync(ToRemote(value, isInstance: false), cancellationToken);
            Apply(snapshot, created.Id, created.ETag, created);
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
                Apply(snapshot, restored.Id, restored.ETag, restored);
                return;
            }
            // 지난번 생성이 실제로는 성공했다. 연결하고 dirty를 남겨 다음 회차에 최신 내용을 patch한다.
            LinkOnly(snapshot, existing);
        }
    }

    private async Task DeleteRemoteAsync(string eventId, string? etag, CancellationToken cancellationToken)
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
        var properties = new Dictionary<string, string>
        {
            ["deskcalUid"] = value.EventUid, ["deskcalDone"] = value.IsDone ? "1" : "0", ["deskcalDday"] = value.IsDday ? "1" : "0",
        };
        Checklist.WriteProperties(value.Checklist, properties);
        var result = new Event
        {
            Id = isInstance ? null : value.GoogleEventId, Summary = value.Title, Description = value.Memo, Status = value.Status,
            Start = ToRemoteTime(value.GoogleStartRaw!, value.TimeZone), End = ToRemoteTime(value.GoogleEndRaw!, value.TimeZone),
            ColorId = value.ColorId, Transparency = value.IsBusy ? "opaque" : "transparent",
            ExtendedProperties = new Event.ExtendedPropertiesData { Private__ = properties },
        };
        // 반복을 없앤 경우에도 반영되도록 시리즈 본체는 빈 목록까지 보낸다.
        if (!isInstance) result.Recurrence = value.RecurrenceRule?.Split('\n', StringSplitOptions.RemoveEmptyEntries) ?? [];
        return result;
    }

    private static EventDateTime ToRemoteTime(string raw, string? timeZone) =>
        raw.Length == 10 ? new EventDateTime { Date = raw } : new EventDateTime { DateTimeRaw = raw, TimeZone = timeZone };

    // 회차 키(종일 'YYYY-MM-DD', 시간 'YYYY-MM-DDTHH:MM:SSZ')를 Google 인스턴스 ID 꼬리로 바꾼다.
    internal static string InstanceSuffix(string key) => key.Length == 10 ? key.Replace("-", "") : key.Replace("-", "").Replace(":", "");

    // patch는 보내지 않은 키를 남기므로 응답에 다른 PC가 넣은 항목이 있을 수 있다. 그 뒤 etag가 같으면 pull이 건너뛰므로 여기서 합친다.
    private void Apply(Stored snapshot, string? googleId, string? etag, Event? response = null)
    {
        using var connection = database.OpenConnection();
        using var transaction = connection.BeginTransaction(deferred: false);
        var same = CurrentRevision(connection, transaction, snapshot.Record.EventId) == snapshot.Record.LocalRevision;
        var checklist = same && response is not null
            ? Checklist.Serialize(Checklist.Merge(snapshot.Record.Checklist, Checklist.ReadProperties(response.ExtendedProperties?.Private__))) : null;
        using var command = Sql.Command(connection, transaction,
            "UPDATE event SET google_event_id=$id, google_etag=$etag, attempt_count=0, last_error=NULL, checklist=COALESCE($checklist, checklist), " +
            "sync_base=COALESCE($base, sync_base)" +
            (same ? ", is_dirty=0, push_state='synced'" : "") + " WHERE event_id=$event;",
            ("$id", googleId), ("$etag", etag), ("$checklist", checklist), ("$event", snapshot.Record.EventId),
            ("$base", response is null ? null : EventFields.Of(response).ToJsonString()));
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    private void LinkOnly(Stored snapshot, Event existing)
    {
        using var connection = database.OpenConnection();
        using var transaction = connection.BeginTransaction(deferred: false);
        var current = ReadStored(connection, transaction, "WHERE event_id=$id", ("$id", snapshot.Record.EventId)).Single().Record;
        // 생성 응답을 못 받은 사이 다른 PC가 고쳤을 수 있다. 다음 patch가 그 값을 되돌리지 않게 먼저 합친다.
        var merged = Checklist.Merge(current.Checklist, Checklist.ReadProperties(existing.ExtendedProperties?.Private__));
        using var command = Sql.Command(connection, transaction, "UPDATE event SET google_etag=$etag, checklist=$checklist, sync_base=$base WHERE event_id=$event;",
            ("$etag", existing.ETag), ("$checklist", Checklist.Serialize(merged)), ("$event", snapshot.Record.EventId),
            ("$base", EventFields.Of(existing).ToJsonString()));
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    // deleted: local(이 PC 삭제가 Google 수정에 밀림) | remote(이 PC 변경이 Google 삭제에 밀림). 둘 다 아니면 예전 원격 우선(기준값 없음).
    private void Discard(Stored snapshot, IReadOnlyList<ChecklistItem>? merged = null, string? deleted = null)
    {
        using var connection = database.OpenConnection();
        using var transaction = connection.BeginTransaction(deferred: false);
        // 보내는 사이에 또 고쳤으면 그 최신 내용을 남긴다(버리는 것은 지금 행이다).
        var isCurrent = CurrentRevision(connection, transaction, snapshot.Record.EventId) == snapshot.Record.LocalRevision;
        var discarded = isCurrent ? snapshot.Record : ReadStored(connection, transaction, "WHERE event_id=$id", ("$id", snapshot.Record.EventId)).Single().Record;
        // 체크리스트를 또 고쳤으면 dirty로 남겨 다음 동기화에서 합친다. 여기서 풀면 이어지는 pull이 그 변경을 덮는다.
        if (!isCurrent && discarded.Checklist.Count > 0) return;
        if (merged is not null)
        {
            using var save = Sql.Command(connection, transaction, "UPDATE event SET checklist=$checklist WHERE event_id=$event;",
                ("$checklist", Checklist.Serialize(merged)), ("$event", snapshot.Record.EventId));
            save.ExecuteNonQuery();
        }
        InsertConflict(connection, transaction, discarded, deleted is null ? null : FieldMerge.Detail([], deleted), Validation.Timestamp(_clock));
        // etag를 비워 다음 pull이 원격 값을 덮게 한다. 증분 조회에 안 올 수 있으므로 토큰도 지워 전체 대조를 한 번 한다.
        using var command = Sql.Command(connection, transaction,
            "UPDATE event SET is_dirty=0, push_state='synced', google_etag=NULL, attempt_count=0, last_error=NULL WHERE event_id=$event;" +
            "UPDATE sync_state SET sync_token=NULL WHERE sync_key=$key;",
            ("$event", snapshot.Record.EventId), ("$key", SYNC_KEY));
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    // 원격 우선으로 버린 이 PC 내용(discarded_json, 사람이 읽음)과 어떤 필드가 겹쳤는지(detail_json, S4).
    private static void InsertConflict(SqliteConnection connection, SqliteTransaction transaction, EventRecord discarded, string? detail, string at)
    {
        using var conflict = Sql.Command(connection, transaction,
            "INSERT INTO sync_conflict(item_kind, item_uid, discarded_json, occurred_at, detail_json) VALUES ('event', $uid, $json, $at, $detail);",
            ("$uid", discarded.EventUid), ("$json", JsonSerializer.Serialize(discarded, TaskSync.ConflictJson)), ("$at", at), ("$detail", detail));
        conflict.ExecuteNonQuery();
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

    private void Pull(IReadOnlyList<Event> items, string? nextSyncToken, bool fullList, IReadOnlySet<string> held, CancellationToken cancellationToken)
    {
        var now = Validation.Timestamp(_clock);
        using var connection = database.OpenConnection();
        var sorted = items.OrderBy(i => i.RecurringEventId is null ? 0 : 1).ToArray();
        var remoteIds = fullList ? items.Where(i => i.Id is not null).Select(i => i.Id!).ToHashSet() : null;
        using var version = Sql.Command(connection, null, "PRAGMA data_version;");
        var before = (long)version.ExecuteScalar()!;
        var records = !fullList && sorted.Length == 0 ? [] : ReadStored(connection, null);
        cancellationToken.ThrowIfCancellationRequested();
        using var transaction = connection.BeginTransaction(deferred: false);
        version.Transaction = transaction;
        // WAL 읽기 동안 MCP가 썼으면 최신 revision으로 다시 판단한다.
        if ((fullList || sorted.Length != 0) && (long)version.ExecuteScalar()! != before) records = ReadStored(connection, transaction);
        var byGoogleId = records.Where(s => s.Record.GoogleEventId != null).ToDictionary(s => s.Record.GoogleEventId!);
        var localInstances = records.Where(s => s.Record.ParentEventId != null)
            .ToDictionary(s => (s.Record.ParentEventId!.Value, s.Record.OriginalStartKey!));
        var usedUids = records.Select(s => s.Record.EventUid).ToHashSet();

        // 예외는 부모보다 먼저 올 수 있으므로 시리즈 본체를 먼저 적용한다.
        foreach (var item in sorted)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (item.Id is null) continue;
            byGoogleId.TryGetValue(item.Id, out var local);
            // 이 PC에서 만든 예외 회차는 올리기 전까지 Google ID가 없다. 시리즈와 회차 키로 찾는다.
            if (local is null && item.RecurringEventId is not null && item.OriginalStartTime is not null
                && byGoogleId.TryGetValue(item.RecurringEventId, out var series))
            {
                var key = OriginalKey(item.OriginalStartTime);
                localInstances.TryGetValue((series.Record.EventId, key), out local);
            }
            if (local is not null && (local.Record.IsDirty || local.Record.PushState != "synced")) continue;
            if (item.Status == "cancelled")
            {
                ApplyCancelled(connection, transaction, item, local, byGoogleId, now);
                continue;
            }
            // 같은 날 일정의 기본 순서는 생성순이다. 두 PC가 같은 순서를 쓰도록 생성 시각은 Google의 created로 맞춘다(S8).
            // 일정 내용이 그대로여도(같은 etag) 맞춘다. 순서에만 쓰는 값이라 dirty·revision은 바꾸지 않는다.
            if (local is not null && Created(item) is { } created && created != local.Record.CreatedAt)
            {
                using var align = Sql.Command(connection, transaction, "UPDATE event SET created_at=$created WHERE event_id=$id;",
                    ("$created", created), ("$id", local.Record.EventId));
                align.ExecuteNonQuery();
                local = local with { Record = local.Record with { CreatedAt = created } };
            }
            if (local is not null && local.Etag == item.ETag && local.Record.DeletedAt is null)
            {
                // 내용이 그대로인 S4 이전 행에 412 병합 기준값을 채운다(같은 etag = 마지막으로 맞춘 값과 같다).
                if (local.Base is null)
                {
                    using var baseline = Sql.Command(connection, transaction, "UPDATE event SET sync_base=$base WHERE event_id=$id;",
                        ("$base", EventFields.Of(item).ToJsonString()), ("$id", local.Record.EventId));
                    baseline.ExecuteNonQuery();
                }
                continue;
            }
            var value = FromRemote(item, local?.Record, byGoogleId, usedUids, now);
            var synced = EventFields.Of(item).ToJsonString();
            if (local is null)
            {
                var id = Sql.Insert(connection, transaction, "event", Values(value, item.ETag, synced));
                byGoogleId[item.Id] = new Stored(value with { EventId = id }, item.ETag, synced);
            }
            else if (local.Etag != item.ETag || local.Record.DeletedAt is not null)
            {
                Sql.Update(connection, transaction, "event", local.Record.EventId, Values(value, item.ETag, synced));
                byGoogleId[item.Id] = new Stored(value, item.ETag, synced);
            }
        }

        // 시리즈보다 먼저(또는 다른 회차에) 받은 예외 회차를 시리즈에 잇는다.
        using (var command = Sql.Command(connection, transaction,
            "UPDATE event SET parent_event_id=(SELECT p.event_id FROM event p WHERE p.google_event_id=event.google_recurring_id) " +
            "WHERE parent_event_id IS NULL AND google_recurring_id IS NOT NULL;"))
            command.ExecuteNonQuery();

        if (fullList)
        {
            // 전체 대조: 보낼 것이 없는 동기화된 행 중 원격에 없는 것은 다른 곳에서 지운 것이다.
            // 아직 안 올라간 행은 dirty라서 빠진다. 충돌 처리로 etag를 비운 행(404·410)도 여기서 지운다.
            foreach (var stored in byGoogleId.Values)
            {
                var record = stored.Record;
                if (remoteIds!.Contains(record.GoogleEventId!) || held.Contains(record.GoogleEventId!) || record.IsDirty || record.PushState != "synced"
                    || record.DeletedAt is not null) continue;
                using var command = Sql.Command(connection, transaction, "UPDATE event SET deleted_at=$now, updated_at=$now WHERE event_id=$id;",
                    ("$now", now), ("$id", record.EventId));
                command.ExecuteNonQuery();
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        using (var command = Sql.Command(connection, transaction,
            "INSERT INTO sync_state(sync_key, sync_token, last_synced_at) VALUES ($key, $token, $at) " +
            "ON CONFLICT(sync_key) DO UPDATE SET sync_token=excluded.sync_token, last_synced_at=excluded.last_synced_at;",
            ("$key", SYNC_KEY), ("$token", nextSyncToken), ("$at", now)))
            command.ExecuteNonQuery();
        if (fullList)
        {
            foreach (var key in new[] { CreatedBasisKey, SyncBaseKey })
            {
                using var basis = Sql.Command(connection, transaction,
                    "INSERT INTO setting(setting_key, setting_value) VALUES ($key, '1') ON CONFLICT(setting_key) DO NOTHING;", ("$key", key));
                basis.ExecuteNonQuery();
            }
        }
        transaction.Commit();
    }

    // Google이 정한 생성 시각(로컬 시각 문자열과 같은 형식, UTC). 없으면(옛 응답·가짜) null.
    private static string? Created(Event item) =>
        item.CreatedDateTimeOffset?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private bool HasSetting(string key)
    {
        using var connection = database.OpenConnection();
        using var command = Sql.Command(connection, null, "SELECT 1 FROM setting WHERE setting_key=$key;", ("$key", key));
        return command.ExecuteScalar() is not null;
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
                ", google_etag=$etag, updated_at=$now, sync_base=NULL WHERE event_id=$id;",
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
            IsDday = Property("deskcalDday") == "1", Checklist = Checklist.ReadProperties(properties), CreatedBy = local?.CreatedBy ?? "google",
            CreatedAt = local?.CreatedAt ?? Created(item) ?? now,
            UpdatedAt = now, DeletedAt = null, LocalRevision = local?.LocalRevision ?? 0, IsDirty = false, PushState = "synced",
        };
        return _time.Derive(value);
    }

    // originalStartTime → 회차 키. 시간 일정은 UTC로 맞춘다(RecurrenceExpander와 같은 형식).
    internal static string OriginalKey(EventDateTime original) => original.Date is { } date ? date
        : EventTime.ParseInstant(original.DateTimeRaw!, original.TimeZone).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    // ---- DB ----

    // Base: 마지막으로 Google과 맞춘 필드 묶음(sync_base, S4). 없으면 412 때 예전처럼 원격 우선이다.
    private sealed record Stored(EventRecord Record, string? Etag, string? Base = null);

    private string? ReadSyncToken()
    {
        using var connection = database.OpenConnection();
        using var command = Sql.Command(connection, null, "SELECT sync_token FROM sync_state WHERE sync_key=$key;", ("$key", SYNC_KEY));
        return command.ExecuteScalar() as string;
    }

    private List<Stored> ReadPushRecords()
    {
        using var connection = database.OpenConnection();
        return ReadStored(connection, null, "WHERE is_dirty=1 OR event_id IN (SELECT parent_event_id FROM event WHERE is_dirty=1 AND parent_event_id IS NOT NULL)");
    }

    private static List<Stored> ReadStored(SqliteConnection connection, SqliteTransaction? transaction,
        string filter = "", params (string Name, object? Value)[] parameters)
    {
        using var command = Sql.Command(connection, transaction, $"SELECT * FROM event {filter} ORDER BY event_id;", parameters);
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
                IsDone = Sql.Number(r, "is_done") != 0, IsDday = Sql.Number(r, "is_dday") != 0, Checklist = Checklist.Parse(Sql.Text(r, "checklist")!), CreatedBy = Sql.Text(r, "created_by")!,
                CreatedAt = Sql.Text(r, "created_at")!, UpdatedAt = Sql.Text(r, "updated_at")!, DeletedAt = Sql.Text(r, "deleted_at"),
                LocalRevision = Sql.Number(r, "local_revision"), IsDirty = Sql.Number(r, "is_dirty") != 0, PushState = Sql.Text(r, "push_state")!,
            };
            result.Add(new Stored(record, Sql.Text(r, "google_etag"), Sql.Text(r, "sync_base")));
        }
        return result;
    }

    private static long CurrentRevision(SqliteConnection connection, SqliteTransaction transaction, long eventId)
    {
        using var command = Sql.Command(connection, transaction, "SELECT local_revision FROM event WHERE event_id=$id;", ("$id", eventId));
        return (long)command.ExecuteScalar()!;
    }

    private static Dictionary<string, object?> Values(EventRecord e, string? etag, string? syncBase = null) => new()
    {
        ["sync_base"] = syncBase,
        ["event_uid"] = e.EventUid, ["google_event_id"] = e.GoogleEventId, ["google_etag"] = etag, ["parent_event_id"] = e.ParentEventId,
        ["google_recurring_id"] = e.GoogleRecurringId, ["original_start_key"] = e.OriginalStartKey, ["status"] = e.Status,
        ["title"] = e.Title, ["memo"] = e.Memo, ["google_start_raw"] = e.GoogleStartRaw, ["google_end_raw"] = e.GoogleEndRaw, ["time_zone"] = e.TimeZone,
        ["start_date"] = e.StartDate, ["end_date"] = e.EndDate, ["start_time"] = e.StartTime, ["end_time"] = e.EndTime,
        ["recurrence_rule"] = e.RecurrenceRule, ["color_id"] = e.ColorId, ["is_busy"] = e.IsBusy, ["is_done"] = e.IsDone, ["is_dday"] = e.IsDday, ["checklist"] = Checklist.Serialize(e.Checklist),
        ["created_by"] = e.CreatedBy, ["created_at"] = e.CreatedAt, ["updated_at"] = e.UpdatedAt, ["deleted_at"] = e.DeletedAt,
        ["local_revision"] = e.LocalRevision, ["is_dirty"] = e.IsDirty, ["push_state"] = e.PushState,
    };
}
