using System.Net;
using System.Text.Json;
using Google;
using Google.Apis.Auth.OAuth2.Responses;
using Google.Apis.Tasks.v1;
using Microsoft.Data.Sqlite;
using GoogleTask = Google.Apis.Tasks.v1.Data.Task;

namespace DeskCal.Core;

/// <summary>Google Tasks 기본 목록 호출. 테스트에서는 가짜로 바꾼다.</summary>
public interface ITasksRemote
{
    Task<IReadOnlyList<GoogleTask>> ListAllAsync(CancellationToken cancellationToken);
    Task<GoogleTask?> GetAsync(string id, CancellationToken cancellationToken);
    Task<GoogleTask> InsertAsync(GoogleTask task, CancellationToken cancellationToken);
    Task<GoogleTask> UpdateAsync(GoogleTask task, CancellationToken cancellationToken);
    Task DeleteAsync(string taskId, string? etag, CancellationToken cancellationToken);
}

public sealed class GoogleTasksRemote(TasksService service) : ITasksRemote
{
    private const string LIST = "@default";

    internal static TasksService CreateService(Google.Apis.Services.BaseClientService.Initializer initializer)
    {
        // 503이 생성 뒤에 돌아올 수도 있다. SDK가 같은 POST를 몰래 되풀이하면 표식만으로 중복을 예방할 수 없다.
        initializer.DefaultExponentialBackOffPolicy = Google.Apis.Http.ExponentialBackOffPolicy.None;
        return new TasksService(initializer);
    }

    public async Task<IReadOnlyList<GoogleTask>> ListAllAsync(CancellationToken cancellationToken)
    {
        var items = new List<GoogleTask>();
        string? page = null;
        do
        {
            var request = service.Tasks.List(LIST);
            request.ShowCompleted = true;
            request.ShowHidden = true;
            request.ShowDeleted = true;
            request.MaxResults = 100;
            request.PageToken = page;
            var result = await request.ExecuteAsync(cancellationToken);
            items.AddRange(result.Items ?? []);
            page = result.NextPageToken;
        } while (page is not null);
        return items;
    }

    public async Task<GoogleTask?> GetAsync(string id, CancellationToken cancellationToken)
    {
        try { return await service.Tasks.Get(LIST, id).ExecuteAsync(cancellationToken); }
        catch (GoogleApiException ex) when (ex.HttpStatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone) { return null; }
    }

    public Task<GoogleTask> InsertAsync(GoogleTask task, CancellationToken cancellationToken) =>
        service.Tasks.Insert(task, LIST).ExecuteAsync(cancellationToken);

    // 전체 교체(PUT)라서 마감일을 지우면 실제로 지워진다. etag가 다르면 412.
    public Task<GoogleTask> UpdateAsync(GoogleTask task, CancellationToken cancellationToken)
    {
        var request = service.Tasks.Update(task, LIST, task.Id);
        if (task.ETag is { } etag) request.ModifyRequest = message => message.Headers.TryAddWithoutValidation("If-Match", etag);
        return request.ExecuteAsync(cancellationToken);
    }

    public Task DeleteAsync(string taskId, string? etag, CancellationToken cancellationToken)
    {
        var request = service.Tasks.Delete(LIST, taskId);
        if (etag is not null) request.ModifyRequest = message => message.Headers.TryAddWithoutValidation("If-Match", etag);
        return request.ExecuteAsync(cancellationToken);
    }
}

/// <summary>
/// 할 일 동기화(§5.4). 매번 push → pull. pull은 기본 목록 전체를 받아 대조한다.
/// 충돌(412)은 원격 우선: 로컬 변경을 sync_conflict에 남기고 원격 값을 받는다.
/// 생성 응답을 못 받으면(결과 불명) 중복을 막으려고 자동으로 다시 보내지 않는다(push_state='unknown').
/// </summary>
public sealed class TaskSync(Database database, ITasksRemote remote, TimeProvider? clock = null)
{
    // 충돌 기록은 사람이 읽는다. 한글을 \uXXXX로 바꾸지 않는다.
    internal static readonly JsonSerializerOptions ConflictJson = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public async Task SyncAsync(CancellationToken cancellationToken)
    {
        var pushed = await PushAsync(cancellationToken);
        var listed = await remote.ListAllAsync(cancellationToken);
        var seen = listed.Select(t => t.Id).ToHashSet();
        var missing = new List<string>();
        using (var connection = database.OpenConnection())
        using (var cmd = Sql.Command(connection, null, """
            SELECT google_task_id FROM todo WHERE google_task_id IS NOT NULL AND is_dirty=0
                AND push_state='synced' AND deleted_at IS NULL
            """))
        using (var reader = cmd.ExecuteReader())
            while (reader.Read())
                if (!seen.Contains(reader.GetString(0)) && !pushed.Contains(reader.GetString(0))) missing.Add(reader.GetString(0));

        var verified = new List<GoogleTask>();
        foreach (var id in missing)
        {
            var result = await remote.GetAsync(id, cancellationToken);
            if (result is not null && result.Id != id) throw new InvalidDataException("할 일 단건 조회의 ID가 다릅니다.");
            verified.Add(result ?? new GoogleTask { Id = id, Deleted = true });
        }
        Pull(listed, verified, cancellationToken);
    }

    // 보낸 결과를 받은 Google ID를 돌려준다. 방금 올린 항목은 바로 이은 목록 조회에 아직 없을 수 있다.
    private async Task<HashSet<string>> PushAsync(CancellationToken cancellationToken)
    {
        var pushed = new HashSet<string>();
        foreach (var snapshot in ReadDirty())
        {
            try
            {
                if (snapshot.DeletedAt is not null)
                {
                    if (snapshot.GoogleTaskId is not null) await DeleteRemoteAsync(snapshot, cancellationToken);
                    Apply(snapshot, snapshot.GoogleTaskId, null);
                }
                else if (snapshot.GoogleTaskId is null)
                {
                    // 보내기 전에 결과 불명으로 둔다. 응답을 저장하기 전에 앱이 끝나도 다시 보내 중복을 만들지 않는다.
                    var marker = Guid.NewGuid().ToString("N");
                    if (!BeginAttempt(snapshot, marker)) continue;
                    GoogleTask created;
                    try
                    {
                        created = await remote.InsertAsync(ToRemote(snapshot with { Marker = marker }), cancellationToken);
                    }
                    catch (GoogleApiException ex)
                    {
                        var uncertain = (int)ex.HttpStatusCode >= 500 || ex.HttpStatusCode == HttpStatusCode.RequestTimeout;
                        MarkFailed(snapshot, uncertain ? "unknown" : "failed", ex.Message);
                        continue;
                    }
                    catch (TokenResponseException)
                    {
                        // 인증 실패는 생성 결과 불명이 아니다. 재연결 후 다시 보내도록 되돌리고 바깥에 알린다.
                        SetPushState(snapshot, "pending");
                        throw;
                    }
                    catch (Exception exception) when (IsNotSent(exception))
                    {
                        // 연결조차 못 했다(이름 풀이·연결 실패, 절전 직후 등). 요청이 Google에 닿지 않았으므로 다음 차례에 다시 보낸다.
                        MarkFailed(snapshot, "pending", exception.Message);
                        continue;
                    }
                    catch (Exception exception) when (exception is not GoogleApiException && !cancellationToken.IsCancellationRequested)
                    {
                        // 요청이 닿았는지 알 수 없다. 다시 보내면 같은 할 일이 둘 생길 수 있다.
                        MarkFailed(snapshot, "unknown", exception.Message);
                        continue;
                    }
                    if (string.IsNullOrEmpty(created.Id)) continue; // ID 없는 성공 응답도 전체 목록에서 확인한다.
                    Apply(snapshot, created.Id, created.ETag);
                    pushed.Add(created.Id);
                }
                else
                {
                    var updated = await remote.UpdateAsync(ToRemote(snapshot), cancellationToken);
                    Apply(snapshot, updated.Id, updated.ETag);
                    if (updated.Id is not null) pushed.Add(updated.Id);
                }
            }
            catch (GoogleApiException exception) when (exception.HttpStatusCode is HttpStatusCode.PreconditionFailed or HttpStatusCode.NotFound or HttpStatusCode.Gone)
            {
                // 다른 곳에서 먼저 바꾸거나 지웠다. 원격 우선: 로컬 변경을 기록하고 버린 뒤 pull이 원격 값을 받게 한다.
                Discard(snapshot);
            }
            catch (GoogleApiException exception)
            {
                MarkFailed(snapshot, "failed", exception.Message);
            }
        }
        return pushed;
    }

    // 요청을 보내기 전 단계에서 실패했는지. 이름 풀이·TCP 연결·TLS 연결 실패는 본문이 서버에 가지 않는다.
    // 시간 초과나 응답 도중 끊김은 닿았을 수 있으므로 여기에 넣지 않는다.
    internal static bool IsNotSent(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            // .NET이 실패 단계를 알려 주면 그것만 믿는다. 연결 단계 오류만 미전송이다.
            if (current is HttpRequestException { HttpRequestError: not HttpRequestError.Unknown } http)
            {
                if (http.HttpRequestError == HttpRequestError.ConnectionError) continue; // 연결 중단인지 연결 전 거절인지는 내부 소켓 오류로 구분한다.
                return http.HttpRequestError is HttpRequestError.NameResolutionError or HttpRequestError.SecureConnectionError
                    or HttpRequestError.ProxyTunnelError;
            }
            // 단계를 모를 때는 연결할 때만 나는 소켓 오류로 좁힌다. NetworkDown 등은 응답을 받다가도 나므로 결과 불명으로 둔다.
            if (current is System.Net.Sockets.SocketException { SocketErrorCode: System.Net.Sockets.SocketError.HostNotFound
                or System.Net.Sockets.SocketError.ConnectionRefused }) return true;
        }
        return false;
    }

    private async Task DeleteRemoteAsync(Snapshot snapshot, CancellationToken cancellationToken)
    {
        try
        {
            await remote.DeleteAsync(snapshot.GoogleTaskId!, snapshot.GoogleEtag, cancellationToken);
        }
        catch (GoogleApiException exception) when (exception.HttpStatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
        {
            // 이미 지워졌다.
        }
    }

    private static GoogleTask ToRemote(Snapshot snapshot) => new()
    {
        Id = snapshot.GoogleTaskId, ETag = snapshot.GoogleEtag, Title = snapshot.Title, Notes = TaskMarker.Add(snapshot.Memo, snapshot.Marker),
        Due = snapshot.DueDate is null ? null : snapshot.DueDate + "T00:00:00.000Z",
        Status = snapshot.IsDone ? "completed" : "needsAction",
        Completed = snapshot.IsDone ? Rfc3339(snapshot.CompletedAt ?? snapshot.UpdatedAt) : null,
    };

    // 로컬 시각은 "O" 형식(소수 7자리, +00:00)이다. Google에는 밀리초까지의 UTC 'Z' 형식으로 보낸다.
    internal static string Rfc3339(string timestamp) =>
        DateTimeOffset.Parse(timestamp, System.Globalization.CultureInfo.InvariantCulture).UtcDateTime
            .ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture);

    // 응답 적용(§5.2): 보낸 뒤 로컬이 또 바뀌었으면(revision 다름) ID·etag만 저장하고 dirty를 남긴다.
    private void Apply(Snapshot snapshot, string? googleId, string? etag)
    {
        using var connection = database.OpenConnection();
        using var transaction = connection.BeginTransaction(deferred: false);
        var same = CurrentRevision(connection, transaction, snapshot.TodoId) == snapshot.LocalRevision;
        using var command = Sql.Command(connection, transaction,
            "UPDATE todo SET google_task_id=$id, google_etag=$etag, attempt_count=0, last_error=NULL" +
            (same ? ", is_dirty=0, push_state='synced'" : ", push_state='pending'") + " WHERE todo_id=$todo;",
            ("$id", googleId), ("$etag", etag), ("$todo", snapshot.TodoId));
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    private void Discard(Snapshot snapshot)
    {
        using var connection = database.OpenConnection();
        using var transaction = connection.BeginTransaction(deferred: false);
        // 보내는 사이에 또 고쳤으면 그 최신 내용을 남긴다(버리는 것은 지금 행이다).
        var discarded = CurrentRevision(connection, transaction, snapshot.TodoId) == snapshot.LocalRevision
            ? snapshot : ReadSnapshots(connection, transaction, "todo_id=$id", ("$id", snapshot.TodoId)).Single();
        InsertConflict(connection, transaction, discarded, Validation.Timestamp(_clock));
        // etag를 비워 다음 pull이 원격 값을 그대로 덮게 한다. 원격에서 지워졌으면 pull의 전체 대조가 로컬도 지운다.
        using var command = Sql.Command(connection, transaction,
            "UPDATE todo SET is_dirty=0, push_state='synced', google_etag=NULL, attempt_count=0, last_error=NULL WHERE todo_id=$todo;",
            ("$todo", snapshot.TodoId));
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    // 원격 우선으로 버리는 지금 로컬 내용을 사람이 읽을 수 있게 남긴다. 결과 불명 할 일을 Google 사본에 연결할 때도 쓴다.
    internal static void RecordDiscarded(SqliteConnection connection, SqliteTransaction transaction, long todoId, string at) =>
        InsertConflict(connection, transaction, ReadSnapshots(connection, transaction, "todo_id=$id", ("$id", todoId)).Single(), at);

    private static void InsertConflict(SqliteConnection connection, SqliteTransaction transaction, Snapshot discarded, string at)
    {
        using var conflict = Sql.Command(connection, transaction,
            "INSERT INTO sync_conflict(item_kind, item_uid, discarded_json, occurred_at) VALUES ('todo', $uid, $json, $at);",
            ("$uid", discarded.TodoUid), ("$json", JsonSerializer.Serialize(discarded, ConflictJson)), ("$at", at));
        conflict.ExecuteNonQuery();
    }

    private bool BeginAttempt(Snapshot snapshot, string marker)
    {
        using var c = database.OpenConnection();
        using var tx = c.BeginTransaction(deferred: false);
        using var cmd = Sql.Command(c, tx, """
            UPDATE todo SET push_state='unknown' WHERE todo_id=$id AND local_revision=$rev
            AND is_dirty=1 AND push_state<>'unknown' AND google_task_id IS NULL
            """, ("$id", snapshot.TodoId), ("$rev", snapshot.LocalRevision));
        if (cmd.ExecuteNonQuery() == 0) return false;
        using var save = Sql.Command(c, tx,
            "INSERT OR REPLACE INTO task_attempt(todo_id,marker,sent_revision) VALUES ($id,$marker,$rev)",
            ("$id", snapshot.TodoId), ("$marker", marker), ("$rev", snapshot.LocalRevision));
        save.ExecuteNonQuery();
        tx.Commit();
        return true;
    }

    private void SetPushState(Snapshot snapshot, string state)
    {
        using var connection = database.OpenConnection();
        using var command = Sql.Command(connection, null, "UPDATE todo SET push_state=$state WHERE todo_id=$todo;",
            ("$state", state), ("$todo", snapshot.TodoId));
        command.ExecuteNonQuery();
    }

    private void MarkFailed(Snapshot snapshot, string state, string error)
    {
        using var connection = database.OpenConnection();
        using var command = Sql.Command(connection, null,
            "UPDATE todo SET push_state=$state, attempt_count=attempt_count+1, last_error=$error WHERE todo_id=$todo;",
            ("$state", state), ("$error", error), ("$todo", snapshot.TodoId));
        command.ExecuteNonQuery();
    }

    // 원격 전체를 로컬에 반영한다. 보낼 것이 남은 행(dirty·failed·unknown)은 덮지 않는다.
    private void Pull(IReadOnlyList<GoogleTask> listed, IReadOnlyList<GoogleTask> verified, CancellationToken cancellationToken)
    {
        var now = Validation.Timestamp(_clock);
        using var connection = database.OpenConnection();
        using var transaction = connection.BeginTransaction(deferred: false);
        TaskRecovery.Match(connection, transaction, listed, now);
        var locals = new Dictionary<string, (long Id, bool Pending, string? Etag, string? DeletedAt)>();
        using (var command = Sql.Command(connection, transaction,
            "SELECT todo_id, google_task_id, google_etag, deleted_at, is_dirty, push_state FROM todo WHERE google_task_id IS NOT NULL;"))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
                locals[Sql.Text(reader, "google_task_id")!] = (Sql.Number(reader, "todo_id"),
                    Sql.Number(reader, "is_dirty") != 0 || Sql.Text(reader, "push_state") != "synced", Sql.Text(reader, "google_etag"), Sql.Text(reader, "deleted_at"));
        }

        foreach (var item in listed.Concat(verified))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (item.Id is null) continue;
            var found = locals.TryGetValue(item.Id, out var local);
            if (found && local.Pending) continue;
            if (item.Deleted == true)
            {
                if (found && local.DeletedAt is null) MarkDeleted(connection, transaction, local.Id, now);
                continue;
            }
            if (found && local.Etag == item.ETag && local.DeletedAt is null) continue;
            var (memo, marker) = TaskMarker.Read(item.Notes);
            var values = new Dictionary<string, object?>
            {
                ["google_task_id"] = item.Id, ["google_etag"] = item.ETag, ["title"] = item.Title ?? "", ["memo"] = memo,
                ["due_date"] = item.Due is { Length: >= 10 } due ? due[..10] : null, ["is_done"] = item.Status == "completed",
                ["completed_at"] = item.Status == "completed" ? item.Completed : null, ["position"] = item.Position,
                ["deleted_at"] = null, ["updated_at"] = now, ["is_dirty"] = false, ["push_state"] = "synced",
            };
            if (!found)
            {
                values["todo_uid"] = Guid.NewGuid().ToString("N");
                values["created_by"] = "google";
                values["created_at"] = now;
                local.Id = Sql.Insert(connection, transaction, "todo", values);
            }
            else if (local.Etag != item.ETag || local.DeletedAt is not null)
            {
                Sql.Update(connection, transaction, "todo", local.Id, values);
            }
            // 다른 PC도 이후 편집에서 서버가 보존한 표식을 그대로 돌려준다.
            if (marker is not null)
            {
                using var remember = Sql.Command(connection, transaction,
                    "INSERT INTO task_attempt(todo_id,marker) VALUES ($id,$marker) ON CONFLICT(todo_id) DO UPDATE SET marker=excluded.marker",
                    ("$id", local.Id), ("$marker", marker));
                remember.ExecuteNonQuery();
            }
            else
            {
                using var clear = Sql.Command(connection, transaction, "DELETE FROM task_attempt WHERE todo_id=$id", ("$id", local.Id));
                clear.ExecuteNonQuery();
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        using var stamp = Sql.Command(connection, transaction, """
            INSERT INTO sync_state(sync_key,last_synced_at) VALUES ('tasks_default',$now)
            ON CONFLICT(sync_key) DO UPDATE SET last_synced_at=excluded.last_synced_at
            """, ("$now", now));
        stamp.ExecuteNonQuery();
        transaction.Commit();
    }

    private static void MarkDeleted(SqliteConnection connection, SqliteTransaction transaction, long todoId, string now)
    {
        using var command = Sql.Command(connection, transaction, "UPDATE todo SET deleted_at=$now, updated_at=$now WHERE todo_id=$id;",
            ("$now", now), ("$id", todoId));
        command.ExecuteNonQuery();
    }

    private static long CurrentRevision(SqliteConnection connection, SqliteTransaction transaction, long todoId)
    {
        using var command = Sql.Command(connection, transaction, "SELECT local_revision FROM todo WHERE todo_id=$id;", ("$id", todoId));
        return (long)command.ExecuteScalar()!;
    }

    // 결과 불명(unknown)은 사용자가 정할 때까지 다시 보내지 않는다.
    private List<Snapshot> ReadDirty()
    {
        using var connection = database.OpenConnection();
        return ReadSnapshots(connection, null, "is_dirty=1 AND push_state<>'unknown'");
    }

    private static List<Snapshot> ReadSnapshots(SqliteConnection connection, SqliteTransaction? transaction, string where,
        params (string Name, object? Value)[] parameters)
    {
        using var command = Sql.Command(connection, transaction, $"SELECT todo.*, (SELECT marker FROM task_attempt a WHERE a.todo_id=todo.todo_id) AS marker FROM todo WHERE {where} ORDER BY todo_id;", parameters);
        using var reader = command.ExecuteReader();
        var result = new List<Snapshot>();
        while (reader.Read())
            result.Add(new Snapshot(Sql.Number(reader, "todo_id"), Sql.Text(reader, "todo_uid")!, Sql.Text(reader, "google_task_id"),
                Sql.Text(reader, "google_etag"), Sql.Text(reader, "title")!, Sql.Text(reader, "memo")!, Sql.Text(reader, "due_date"),
                Sql.Number(reader, "is_done") != 0, Sql.Text(reader, "completed_at"), Sql.Text(reader, "updated_at")!,
                Sql.Text(reader, "deleted_at"), Sql.Number(reader, "local_revision"), Sql.Text(reader, "marker")));
        return result;
    }

    private sealed record Snapshot(long TodoId, string TodoUid, string? GoogleTaskId, string? GoogleEtag, string Title, string Memo,
        string? DueDate, bool IsDone, string? CompletedAt, string UpdatedAt, string? DeletedAt, long LocalRevision, string? Marker);
}
