using Microsoft.Data.Sqlite;
using GoogleTask = Google.Apis.Tasks.v1.Data.Task;

namespace DeskCal.Core;

/// <summary>
/// 생성 결과 불명(unknown) 할 일을 Google 사본에 연결한다. 연결은 TaskSync의 412 처리와 같은 원격 우선이다:
/// Google 값을 받고, 이 PC에만 있는 편집은 sync_conflict에 남긴다. 연결하면서 이 PC 내용으로 덮어쓰면
/// 그사이 폰에서 바꾼 마감·완료가 충돌 기록 없이 사라진다(If-Match가 지금 etag라 412도 나지 않는다).
/// </summary>
public sealed class TaskRecovery(Database database)
{
    public sealed record Candidate(string Uid, string Title, string Memo, string? DueDate, string Etag);

    public IReadOnlyList<Candidate> Candidates()
    {
        using var connection = database.OpenConnection();
        using var cmd = Sql.Command(connection, null, """
            SELECT todo_uid,title,memo,due_date,google_etag FROM todo
            WHERE created_by='google' AND google_task_id IS NOT NULL AND google_etag IS NOT NULL
                AND deleted_at IS NULL AND is_dirty=0 AND local_revision=0 ORDER BY title,due_date,todo_uid
            """);
        using var reader = cmd.ExecuteReader();
        var result = new List<Candidate>();
        while (reader.Read()) result.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetString(4)));
        return result;
    }

    // 사용자가 고른 Google 사본만 연결한다. 목록을 연 뒤 편집된 후보는 재선택해야 한다.
    // candidate가 없으면 새 할 일로 다시 보낸다(사용자가 중복 가능성을 확인했다).
    public Task<bool> ResolveAsync(string uid, Candidate? candidate) => SyncCoordinator.WithSenderAsync(database, () =>
    {
        using var c = database.OpenConnection();
        using var tx = c.BeginTransaction(deferred: false);
        using var find = Sql.Command(c, tx, "SELECT todo_id FROM todo WHERE todo_uid=$uid AND push_state='unknown' AND google_task_id IS NULL", ("$uid", uid));
        if (find.ExecuteScalar() is not long localId) return Task.FromResult(false);
        var now = Validation.Timestamp(TimeProvider.System);
        using (var clear = Sql.Command(c, tx, "DELETE FROM task_attempt WHERE todo_id=$id", ("$id", localId)))
            clear.ExecuteNonQuery();
        if (candidate is null)
        {
            using var resend = Sql.Command(c, tx, """
                UPDATE todo SET push_state='pending', is_dirty=1, attempt_count=0, last_error=NULL,
                    local_revision=local_revision+1, updated_at=$now WHERE todo_id=$id
                """, ("$id", localId), ("$now", now));
            resend.ExecuteNonQuery();
        }
        else
        {
            // 이 사본은 이 PC가 Google에서 읽어 만든 미편집 행이다. 그 값을 원래 공개 UID의 행으로 옮기고 사본은 지운다.
            var remote = new Dictionary<string, object?>();
            using (var remove = Sql.Command(c, tx, """
                DELETE FROM todo WHERE todo_uid=$uid AND google_etag=$etag AND google_task_id IS NOT NULL
                AND created_by='google' AND local_revision=0 AND is_dirty=0 AND deleted_at IS NULL
                RETURNING google_task_id, google_etag, title, memo, due_date, is_done, completed_at, position
                """, ("$uid", candidate.Uid), ("$etag", candidate.Etag)))
            using (var reader = remove.ExecuteReader())
                while (reader.Read())
                    for (var i = 0; i < reader.FieldCount; i++) remote[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            if (remote.Count == 0) return Task.FromResult(false);
            // 과거 행에는 보낸 시점 기록이 없다. 어느 쪽이 바뀌었는지 모르므로 Google 값과 다르면 이 PC 내용을 남긴다.
            if (DiffersFromRemote(c, tx, localId, remote)) TaskSync.RecordDiscarded(c, tx, localId, now);
            remote["deleted_at"] = null;
            remote["is_dirty"] = false;
            remote["push_state"] = "synced";
            remote["attempt_count"] = 0;
            remote["last_error"] = null;
            remote["updated_at"] = now;
            Sql.Update(c, tx, "todo", localId, remote);
        }
        tx.Commit();
        return Task.FromResult(true);
    }, CancellationToken.None);

    private static bool DiffersFromRemote(SqliteConnection c, SqliteTransaction tx, long localId, IReadOnlyDictionary<string, object?> remote)
    {
        using var cmd = Sql.Command(c, tx, """
            SELECT 1 FROM todo WHERE todo_id=$id AND title IS $title AND memo IS $memo AND due_date IS $due
                AND is_done IS $done AND deleted_at IS NULL
            """, ("$id", localId), ("$title", remote["title"]), ("$memo", remote["memo"]), ("$due", remote["due_date"]), ("$done", remote["is_done"]));
        return cmd.ExecuteScalar() is null;
    }

    internal static void Match(SqliteConnection c, SqliteTransaction tx, IReadOnlyList<GoogleTask> completeList, string now)
    {
        var matches = completeList.Where(t => t.Deleted != true && t.Id is not null)
            .Select(t => (Task: t, Token: TaskMarker.Read(t.Notes).Token)).Where(p => p.Token is not null)
            .ToLookup(p => p.Token!, p => p.Task);
        using var cmd = Sql.Command(c, tx, """
            SELECT a.todo_id,a.marker,a.sent_revision FROM task_attempt a JOIN todo t USING(todo_id)
            WHERE t.google_task_id IS NULL AND t.push_state='unknown' AND a.ambiguous=0 AND a.sent_revision IS NOT NULL
            """);
        var attempts = new List<(long Id, string Token, long Revision)>();
        using (var reader = cmd.ExecuteReader())
            while (reader.Read()) attempts.Add((reader.GetInt64(0), reader.GetString(1), reader.GetInt64(2)));
        foreach (var attempt in attempts)
        {
            var found = matches[attempt.Token].DistinctBy(t => t.Id).Take(2).ToArray();
            if (found.Length == 0) continue;
            if (found.Length > 1)
            {
                using var flag = Sql.Command(c, tx, "UPDATE task_attempt SET ambiguous=1 WHERE todo_id=$id", ("$id", attempt.Id));
                flag.ExecuteNonQuery();
                continue;
            }
            // etag를 비워 두면 같은 트랜잭션의 pull이 이 Google 값을 그대로 받는다(원격 우선).
            using var link = Sql.Command(c, tx, """
                UPDATE todo SET google_task_id=$remote, google_etag=NULL, is_dirty=0, push_state='synced',
                    attempt_count=0, last_error=NULL
                WHERE todo_id=$id AND NOT EXISTS (SELECT 1 FROM todo WHERE google_task_id=$remote)
                RETURNING local_revision
                """, ("$remote", found[0].Id), ("$id", attempt.Id));
            if (link.ExecuteScalar() is not long revision) continue;
            // 보낸 뒤 이 PC에서 또 고쳤다. 그 편집은 버리지 않고 충돌 기록에 남긴다.
            if (revision != attempt.Revision) TaskSync.RecordDiscarded(c, tx, attempt.Id, now);
        }
    }
}
