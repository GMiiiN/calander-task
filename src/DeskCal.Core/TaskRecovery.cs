using Microsoft.Data.Sqlite;
using GoogleTask = Google.Apis.Tasks.v1.Data.Task;

namespace DeskCal.Core;

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
    public Task<bool> ResolveAsync(string uid, Candidate? candidate) => SyncCoordinator.WithSenderAsync(database, () =>
    {
        using var c = database.OpenConnection();
        using var tx = c.BeginTransaction(deferred: false);
        using var find = Sql.Command(c, tx, "SELECT todo_id FROM todo WHERE todo_uid=$uid AND push_state='unknown' AND google_task_id IS NULL", ("$uid", uid));
        if (find.ExecuteScalar() is not long localId) return Task.FromResult(false);
        string? remoteId = null;
        if (candidate is not null)
        {
            // 이 사본은 이 PC가 Google에서 읽어 만든 미편집 행이다. 원래 공개 UID로 연결을 옮긴다.
            using var remove = Sql.Command(c, tx, """
                DELETE FROM todo WHERE todo_uid=$uid AND google_etag=$etag AND google_task_id IS NOT NULL
                AND created_by='google' AND local_revision=0 AND is_dirty=0 AND deleted_at IS NULL RETURNING google_task_id
                """, ("$uid", candidate.Uid), ("$etag", candidate.Etag));
            remoteId = remove.ExecuteScalar() as string;
            if (remoteId is null) return Task.FromResult(false);
        }
        using var clear = Sql.Command(c, tx, "DELETE FROM task_attempt WHERE todo_id=$id", ("$id", localId));
        clear.ExecuteNonQuery();
        using var save = Sql.Command(c, tx, """
            UPDATE todo SET google_task_id=$remote, google_etag=$etag, push_state='pending', is_dirty=1,
                attempt_count=0,last_error=NULL,local_revision=local_revision+1,updated_at=$now WHERE todo_id=$id
            """, ("$remote", remoteId), ("$etag", candidate?.Etag), ("$id", localId), ("$now", Validation.Timestamp(TimeProvider.System)));
        save.ExecuteNonQuery();
        tx.Commit();
        return Task.FromResult(true);
    }, CancellationToken.None);

    internal static void Match(SqliteConnection c, SqliteTransaction tx, IReadOnlyList<GoogleTask> completeList)
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
            using var link = Sql.Command(c, tx, """
                UPDATE todo SET google_task_id=$remote,
                    google_etag=CASE WHEN local_revision<>$rev THEN $etag END,
                    is_dirty=(local_revision<>$rev),
                    push_state=CASE WHEN local_revision=$rev THEN 'synced' ELSE 'pending' END,
                    attempt_count=0,last_error=NULL
                WHERE todo_id=$id AND NOT EXISTS (SELECT 1 FROM todo WHERE google_task_id=$remote)
                """, ("$remote", found[0].Id), ("$etag", found[0].ETag), ("$id", attempt.Id), ("$rev", attempt.Revision));
            link.ExecuteNonQuery();
        }
    }
}
