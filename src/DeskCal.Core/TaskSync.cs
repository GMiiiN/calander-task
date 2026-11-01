using System.Net;
using System.Text.Json;
using Google;
using Google.Apis.Tasks.v1;
using Microsoft.Data.Sqlite;
using GoogleTask = Google.Apis.Tasks.v1.Data.Task;

namespace DeskCal.Core;

/// <summary>Google Tasks 기본 목록 호출. 테스트에서는 가짜로 바꾼다.</summary>
public interface ITasksRemote
{
    Task<IReadOnlyList<GoogleTask>> ListAllAsync(CancellationToken cancellationToken);
    Task<GoogleTask> InsertAsync(GoogleTask task, CancellationToken cancellationToken);
    Task<GoogleTask> UpdateAsync(GoogleTask task, CancellationToken cancellationToken);
    Task DeleteAsync(string taskId, string? etag, CancellationToken cancellationToken);
}

public sealed class GoogleTasksRemote(TasksService service) : ITasksRemote
{
    private const string LIST = "@default";

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
        await PushAsync(cancellationToken);
        Pull(await remote.ListAllAsync(cancellationToken));
    }

    private async Task PushAsync(CancellationToken cancellationToken)
    {
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
                    GoogleTask created;
                    try
                    {
                        created = await remote.InsertAsync(ToRemote(snapshot), cancellationToken);
                    }
                    catch (Exception exception) when (exception is not GoogleApiException && !cancellationToken.IsCancellationRequested)
                    {
                        // 요청이 닿았는지 알 수 없다. 다시 보내면 같은 할 일이 둘 생길 수 있다.
                        MarkFailed(snapshot, "unknown", exception.Message);
                        continue;
                    }
                    Apply(snapshot, created.Id, created.ETag);
                }
                else
                {
                    var updated = await remote.UpdateAsync(ToRemote(snapshot), cancellationToken);
                    Apply(snapshot, updated.Id, updated.ETag);
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
        Id = snapshot.GoogleTaskId, ETag = snapshot.GoogleEtag, Title = snapshot.Title, Notes = snapshot.Memo,
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
            (same ? ", is_dirty=0, push_state='synced'" : "") + " WHERE todo_id=$todo;",
            ("$id", googleId), ("$etag", etag), ("$todo", snapshot.TodoId));
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    private void Discard(Snapshot snapshot)
    {
        using var connection = database.OpenConnection();
        using var transaction = connection.BeginTransaction(deferred: false);
        using (var conflict = Sql.Command(connection, transaction,
            "INSERT INTO sync_conflict(item_kind, item_uid, discarded_json, occurred_at) VALUES ('todo', $uid, $json, $at);",
            ("$uid", snapshot.TodoUid), ("$json", JsonSerializer.Serialize(snapshot, ConflictJson)), ("$at", Validation.Timestamp(_clock))))
            conflict.ExecuteNonQuery();
        // etag를 비워 다음 pull이 원격 값을 그대로 덮게 한다. 원격에서 지워졌으면 pull의 전체 대조가 로컬도 지운다.
        using var command = Sql.Command(connection, transaction,
            "UPDATE todo SET is_dirty=0, push_state='synced', google_etag=NULL, attempt_count=0, last_error=NULL WHERE todo_id=$todo;",
            ("$todo", snapshot.TodoId));
        command.ExecuteNonQuery();
        transaction.Commit();
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
    private void Pull(IReadOnlyList<GoogleTask> items)
    {
        var now = Validation.Timestamp(_clock);
        using var connection = database.OpenConnection();
        using var transaction = connection.BeginTransaction(deferred: false);
        var locals = new Dictionary<string, (long Id, bool Pending, string? Etag, string? DeletedAt)>();
        using (var command = Sql.Command(connection, transaction,
            "SELECT todo_id, google_task_id, google_etag, deleted_at, is_dirty, push_state FROM todo WHERE google_task_id IS NOT NULL;"))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
                locals[Sql.Text(reader, "google_task_id")!] = (Sql.Number(reader, "todo_id"),
                    Sql.Number(reader, "is_dirty") != 0 || Sql.Text(reader, "push_state") != "synced", Sql.Text(reader, "google_etag"), Sql.Text(reader, "deleted_at"));
        }

        var seen = new HashSet<string>();
        foreach (var item in items)
        {
            if (item.Id is null) continue;
            var found = locals.TryGetValue(item.Id, out var local);
            if (item.Deleted != true) seen.Add(item.Id);
            if (found && local.Pending) continue;
            if (item.Deleted == true)
            {
                if (found && local.DeletedAt is null) MarkDeleted(connection, transaction, local.Id, now);
                continue;
            }
            var values = new Dictionary<string, object?>
            {
                ["google_task_id"] = item.Id, ["google_etag"] = item.ETag, ["title"] = item.Title ?? "", ["memo"] = item.Notes ?? "",
                ["due_date"] = item.Due is { Length: >= 10 } due ? due[..10] : null, ["is_done"] = item.Status == "completed",
                ["completed_at"] = item.Status == "completed" ? item.Completed : null, ["position"] = item.Position,
                ["deleted_at"] = null, ["updated_at"] = now, ["is_dirty"] = false, ["push_state"] = "synced",
            };
            if (!found)
            {
                values["todo_uid"] = Guid.NewGuid().ToString("N");
                values["created_by"] = "google";
                values["created_at"] = now;
                Sql.Insert(connection, transaction, "todo", values);
            }
            else if (local.Etag != item.ETag || local.DeletedAt is not null)
            {
                Sql.Update(connection, transaction, "todo", local.Id, values);
            }
        }
        // 전체 대조: 원격 목록에 없는 동기화된 행은 다른 곳에서 지운 것이다.
        foreach (var (googleId, local) in locals)
            if (!seen.Contains(googleId) && !local.Pending && local.DeletedAt is null) MarkDeleted(connection, transaction, local.Id, now);
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
        using var command = Sql.Command(connection, null,
            "SELECT * FROM todo WHERE is_dirty=1 AND push_state<>'unknown' ORDER BY todo_id;");
        using var reader = command.ExecuteReader();
        var result = new List<Snapshot>();
        while (reader.Read())
            result.Add(new Snapshot(Sql.Number(reader, "todo_id"), Sql.Text(reader, "todo_uid")!, Sql.Text(reader, "google_task_id"),
                Sql.Text(reader, "google_etag"), Sql.Text(reader, "title")!, Sql.Text(reader, "memo")!, Sql.Text(reader, "due_date"),
                Sql.Number(reader, "is_done") != 0, Sql.Text(reader, "completed_at"), Sql.Text(reader, "updated_at")!,
                Sql.Text(reader, "deleted_at"), Sql.Number(reader, "local_revision")));
        return result;
    }

    private sealed record Snapshot(long TodoId, string TodoUid, string? GoogleTaskId, string? GoogleEtag, string Title, string Memo,
        string? DueDate, bool IsDone, string? CompletedAt, string UpdatedAt, string? DeletedAt, long LocalRevision);
}
