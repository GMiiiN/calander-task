using Microsoft.Data.Sqlite;

namespace DeskCal.Core;

public sealed class TodoRepository(Database database, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public IReadOnlyList<TodoRecord> Create(IReadOnlyList<TodoInput> inputs)
    {
        Validation.Batch(inputs.Count);
        using var connection = database.OpenConnection();
        using var transaction = connection.BeginTransaction(deferred: false);
        var result = new List<TodoRecord>();
        foreach (var input in inputs)
        {
            Validation.CreatedBy(input.CreatedBy);
            if (input.DueDate is not null) Validation.Date(input.DueDate);
            var now = Validation.Timestamp(_clock);
            var value = new TodoRecord
            {
                TodoUid = Guid.NewGuid().ToString("N"), Title = Validation.Title(input.Title), DueDate = input.DueDate,
                Memo = input.Memo ?? "", CreatedBy = input.CreatedBy, CreatedAt = now, UpdatedAt = now,
                LocalRevision = 1, IsDirty = true, PushState = "pending"
            };
            result.Add(value with { TodoId = Sql.Insert(connection, transaction, "todo", Values(value)) });
        }
        transaction.Commit();
        return result;
    }

    public TodoRecord Get(string uid)
    {
        using var connection = database.OpenConnection();
        return Get(connection, null, uid);
    }

    public IReadOnlyList<TodoRecord> List(string status = "open", string? keyword = null, bool includeDeleted = false)
    {
        if (status is not ("open" or "done" or "all")) throw new ArgumentException("Status must be open, done or all.");
        using var connection = database.OpenConnection();
        using var command = Sql.Command(connection, null, "SELECT * FROM todo ORDER BY position, due_date, todo_id;");
        using var reader = command.ExecuteReader();
        var result = new List<TodoRecord>();
        while (reader.Read())
        {
            var value = Read(reader);
            if (!includeDeleted && value.DeletedAt is not null) continue;
            if (status == "open" && value.IsDone || status == "done" && !value.IsDone) continue;
            if (keyword is not null && !value.Title.Contains(keyword, StringComparison.OrdinalIgnoreCase) && !value.Memo.Contains(keyword, StringComparison.OrdinalIgnoreCase)) continue;
            result.Add(value);
        }
        return result;
    }

    public TodoRecord Update(string uid, TodoPatch patch) => Write(uid, value =>
    {
        if (value.DeletedAt is not null) throw new InvalidOperationException("Restore the deleted todo before editing.");
        var due = patch.DueDate.IsSpecified ? patch.DueDate.Value : value.DueDate;
        if (due is not null) Validation.Date(due);
        var done = Validation.Required(patch.IsDone, value.IsDone);
        return value with
        {
            Title = Validation.Title(Validation.Required(patch.Title, value.Title)), DueDate = due,
            Memo = patch.Memo.IsSpecified ? patch.Memo.Value ?? "" : value.Memo, IsDone = done,
            CompletedAt = done ? value.IsDone ? value.CompletedAt : Validation.Timestamp(_clock) : null
        };
    });

    public TodoRecord Delete(string uid) => Write(uid, value => value with { DeletedAt = value.DeletedAt ?? Validation.Timestamp(_clock) });
    public TodoRecord Restore(string uid) => Write(uid, value => value with { DeletedAt = null });

    private TodoRecord Write(string uid, Func<TodoRecord, TodoRecord> change)
    {
        using var connection = database.OpenConnection();
        using var transaction = connection.BeginTransaction(deferred: false);
        var previous = Get(connection, transaction, uid);
        var value = change(previous) with { LocalRevision = previous.LocalRevision + 1, IsDirty = true, PushState = "pending", UpdatedAt = Validation.Timestamp(_clock) };
        Sql.Update(connection, transaction, "todo", value.TodoId, Values(value));
        transaction.Commit();
        return value;
    }

    private static TodoRecord Get(SqliteConnection connection, SqliteTransaction? transaction, string uid)
    {
        using var command = Sql.Command(connection, transaction, "SELECT * FROM todo WHERE todo_uid=$uid;", ("$uid", uid));
        using var reader = command.ExecuteReader();
        return reader.Read() ? Read(reader) : throw new KeyNotFoundException("Todo not found.");
    }

    private static TodoRecord Read(SqliteDataReader reader) => new()
    {
        TodoId = Sql.Number(reader, "todo_id"), TodoUid = Sql.Text(reader, "todo_uid")!, Title = Sql.Text(reader, "title")!, Memo = Sql.Text(reader, "memo")!,
        DueDate = Sql.Text(reader, "due_date"), IsDone = Sql.Number(reader, "is_done") != 0, CompletedAt = Sql.Text(reader, "completed_at"), Position = Sql.Text(reader, "position"),
        CreatedBy = Sql.Text(reader, "created_by")!, CreatedAt = Sql.Text(reader, "created_at")!, UpdatedAt = Sql.Text(reader, "updated_at")!, DeletedAt = Sql.Text(reader, "deleted_at"),
        LocalRevision = Sql.Number(reader, "local_revision"), IsDirty = Sql.Number(reader, "is_dirty") != 0, PushState = Sql.Text(reader, "push_state")!
    };

    private static Dictionary<string, object?> Values(TodoRecord value) => new()
    {
        ["todo_uid"] = value.TodoUid, ["title"] = value.Title, ["memo"] = value.Memo, ["due_date"] = value.DueDate,
        ["is_done"] = value.IsDone, ["completed_at"] = value.CompletedAt, ["created_by"] = value.CreatedBy, ["created_at"] = value.CreatedAt,
        ["updated_at"] = value.UpdatedAt, ["deleted_at"] = value.DeletedAt, ["local_revision"] = value.LocalRevision,
        ["is_dirty"] = value.IsDirty, ["push_state"] = value.PushState
    };
}
