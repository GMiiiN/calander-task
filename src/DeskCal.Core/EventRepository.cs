using Microsoft.Data.Sqlite;

namespace DeskCal.Core;

public sealed class EventRepository
{
    private readonly Database _database;
    private readonly EventTime _time;
    private readonly TimeProvider _clock;
    private readonly RecurrenceExpander _expander;

    public EventRepository(Database database, TimeZoneInfo? localTimeZone = null, TimeProvider? clock = null)
    {
        _database = database;
        _time = new EventTime(localTimeZone);
        _clock = clock ?? TimeProvider.System;
        _expander = new RecurrenceExpander(_time.LocalTimeZone);
    }

    public IReadOnlyList<EventRecord> Create(IReadOnlyList<EventInput> inputs)
    {
        Validation.Batch(inputs.Count);
        using var connection = _database.OpenConnection();
        using var transaction = connection.BeginTransaction(deferred: false);
        var result = new List<EventRecord>();
        foreach (var input in inputs)
        {
            Validation.CreatedBy(input.CreatedBy);
            var now = Validation.Timestamp(_clock);
            var uid = Guid.NewGuid().ToString("N");
            var value = _time.Normalize(new EventRecord
            {
                EventUid = uid, GoogleEventId = uid, Title = Validation.Title(input.Title), Memo = input.Memo ?? "",
                StartDate = input.StartDate, EndDate = input.EndDate ?? input.StartDate, StartTime = input.StartTime, EndTime = input.EndTime,
                ColorId = input.ColorId, IsBusy = input.IsBusy, IsDday = input.IsDday, CreatedBy = input.CreatedBy,
                CreatedAt = now, UpdatedAt = now, LocalRevision = 1, IsDirty = true, PushState = "pending"
            });
            value = value with { RecurrenceRule = input.Repeat?.ToRule(value) };
            result.Add(value with { EventId = Sql.Insert(connection, transaction, "event", Values(value)) });
        }
        transaction.Commit();
        return result;
    }

    public EventRecord Get(string uid)
    {
        using var connection = _database.OpenConnection();
        return Get(connection, null, uid);
    }

    public IReadOnlyList<EventRecord> GetRecords()
    {
        using var connection = _database.OpenConnection();
        return ReadAll(connection, null);
    }

    public IReadOnlyList<EventOccurrence> List(DateOnly start, DateOnly end, string? keyword = null, string? colorId = null, bool includeDeleted = false)
    {
        Validation.Range(start, end, 366);
        var records = GetRecords();
        return records.Where(e => e.ParentEventId is null && e.GoogleRecurringId is null)
            .SelectMany(parent => _expander.Expand(parent, records.Where(e => e.ParentEventId == parent.EventId), start, end, includeDeleted))
            .Where(o => keyword is null || o.Event.Title.Contains(keyword, StringComparison.OrdinalIgnoreCase) || o.Event.Memo.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            .Where(o => colorId is null || o.Event.ColorId == colorId)
            .OrderBy(o => o.Start).ThenBy(o => o.Event.EventUid).ToArray();
    }

    public EventRecord Update(string uid, EventPatch patch)
    {
        using var connection = _database.OpenConnection();
        using var transaction = connection.BeginTransaction(deferred: false);
        var previous = Get(connection, transaction, uid);
        if (previous.ParentEventId is not null) throw new ArgumentException("Use occurrence ID for occurrence changes.");
        if (previous.DeletedAt is not null) throw new InvalidOperationException("Restore the deleted event before editing.");
        if (patch.Repeat.IsSpecified && !RecurrenceInput.IsEditable(previous.RecurrenceRule))
            throw new InvalidOperationException("Edit this recurrence in Google Calendar.");
        var start = Validation.Required(patch.StartDate, previous.StartDate);
        var value = previous with
        {
            Title = Validation.Title(Validation.Required(patch.Title, previous.Title)), StartDate = start,
            EndDate = Validation.Required(patch.EndDate, previous.EndDate),
            StartTime = patch.StartTime.IsSpecified ? patch.StartTime.Value : previous.StartTime,
            EndTime = patch.EndTime.IsSpecified ? patch.EndTime.Value : previous.EndTime,
            Memo = patch.Memo.IsSpecified ? patch.Memo.Value ?? "" : previous.Memo,
            ColorId = patch.ColorId.IsSpecified ? patch.ColorId.Value : previous.ColorId,
            IsDone = Validation.Required(patch.IsDone, previous.IsDone), IsBusy = Validation.Required(patch.IsBusy, previous.IsBusy), IsDday = Validation.Required(patch.IsDday, previous.IsDday)
        };
        if (patch.ChangesTime) value = _time.Normalize(value);
        if (patch.Repeat.IsSpecified) value = value with { RecurrenceRule = patch.Repeat.Value?.ToRule(value) };
        if (value.StartDate != previous.StartDate || value.RecurrenceRule != previous.RecurrenceRule)
        {
            using var command = Sql.Command(connection, transaction, "SELECT EXISTS(SELECT 1 FROM event WHERE parent_event_id=$id);", ("$id", previous.EventId));
            if ((long)command.ExecuteScalar()! != 0) throw new InvalidOperationException("Edit in Google Calendar or resolve occurrence exceptions first.");
        }
        value = Save(connection, transaction, value);
        transaction.Commit();
        return value;
    }

    public EventRecord Delete(string uid) => SetDeleted(uid, true);
    public EventRecord Restore(string uid) => SetDeleted(uid, false);

    private EventRecord SetDeleted(string uid, bool deleted)
    {
        using var connection = _database.OpenConnection();
        using var transaction = connection.BeginTransaction(deferred: false);
        var value = Get(connection, transaction, uid);
        if (value.ParentEventId is not null) throw new ArgumentException("Use occurrence ID for occurrence changes.");
        value = Save(connection, transaction, value with { DeletedAt = deleted ? value.DeletedAt ?? Validation.Timestamp(_clock) : null });
        transaction.Commit();
        return value;
    }

    public EventRecord SetOccurrenceDone(string occurrenceId, bool isDone) => ChangeOccurrence(occurrenceId, e => e with { IsDone = isDone });
    public EventRecord DeleteOccurrence(string occurrenceId) => ChangeOccurrence(occurrenceId, e => e with { Status = "cancelled" });
    public EventRecord RestoreOccurrence(string occurrenceId) => ChangeOccurrence(occurrenceId, e => e with { Status = "confirmed", DeletedAt = null });

    private EventRecord ChangeOccurrence(string occurrenceId, Func<EventRecord, EventRecord> change)
    {
        var (uid, key) = OccurrenceId.Parse(occurrenceId);
        using var connection = _database.OpenConnection();
        using var transaction = connection.BeginTransaction(deferred: false);
        var parent = Get(connection, transaction, uid);
        if (parent.RecurrenceRule is null || parent.ParentEventId is not null) throw new ArgumentException("Occurrence requires a series.");
        if (parent.DeletedAt is not null || parent.Status == "cancelled") throw new InvalidOperationException("Restore the series first.");
        var value = ReadAll(connection, transaction).SingleOrDefault(e => e.ParentEventId == parent.EventId && e.OriginalStartKey == key);
        if (value is null)
        {
            var occurrence = _expander.GetOriginal(parent, key) ?? throw new KeyNotFoundException("Occurrence not found in series.");
            var now = Validation.Timestamp(_clock);
            value = occurrence.Event with
            {
                EventId = 0, EventUid = Guid.NewGuid().ToString("N"), GoogleEventId = null, ParentEventId = parent.EventId,
                GoogleRecurringId = parent.GoogleEventId, OriginalStartKey = key, RecurrenceRule = null,
                CreatedAt = now, UpdatedAt = now, LocalRevision = 0, DeletedAt = null
            };
        }
        value = Save(connection, transaction, change(value));
        transaction.Commit();
        return value;
    }

    private EventRecord Save(SqliteConnection connection, SqliteTransaction transaction, EventRecord value)
    {
        value = value with { LocalRevision = value.LocalRevision + 1, IsDirty = true, PushState = "pending", UpdatedAt = Validation.Timestamp(_clock) };
        if (value.EventId == 0) return value with { EventId = Sql.Insert(connection, transaction, "event", Values(value)) };
        Sql.Update(connection, transaction, "event", value.EventId, Values(value));
        return value;
    }

    private static EventRecord Get(SqliteConnection connection, SqliteTransaction? transaction, string uid)
    {
        using var command = Sql.Command(connection, transaction, "SELECT * FROM event WHERE event_uid=$uid;", ("$uid", uid));
        using var reader = command.ExecuteReader();
        return reader.Read() ? Read(reader) : throw new KeyNotFoundException("Event not found.");
    }

    private static List<EventRecord> ReadAll(SqliteConnection connection, SqliteTransaction? transaction)
    {
        using var command = Sql.Command(connection, transaction, "SELECT * FROM event;");
        using var reader = command.ExecuteReader();
        var result = new List<EventRecord>();
        while (reader.Read()) result.Add(Read(reader));
        return result;
    }

    private static EventRecord Read(SqliteDataReader r) => new()
    {
        EventId = Sql.Number(r, "event_id"), EventUid = Sql.Text(r, "event_uid")!, GoogleEventId = Sql.Text(r, "google_event_id"),
        ParentEventId = r["parent_event_id"] is DBNull ? null : Sql.Number(r, "parent_event_id"), GoogleRecurringId = Sql.Text(r, "google_recurring_id"),
        OriginalStartKey = Sql.Text(r, "original_start_key"), Status = Sql.Text(r, "status")!, Title = Sql.Text(r, "title")!, Memo = Sql.Text(r, "memo")!,
        GoogleStartRaw = Sql.Text(r, "google_start_raw"), GoogleEndRaw = Sql.Text(r, "google_end_raw"), TimeZone = Sql.Text(r, "time_zone"),
        StartDate = Sql.Text(r, "start_date")!, EndDate = Sql.Text(r, "end_date")!, StartTime = Sql.Text(r, "start_time"), EndTime = Sql.Text(r, "end_time"),
        RecurrenceRule = Sql.Text(r, "recurrence_rule"), ColorId = Sql.Text(r, "color_id"), IsBusy = Sql.Number(r, "is_busy") != 0,
        IsDone = Sql.Number(r, "is_done") != 0, IsDday = Sql.Number(r, "is_dday") != 0, CreatedBy = Sql.Text(r, "created_by")!,
        CreatedAt = Sql.Text(r, "created_at")!, UpdatedAt = Sql.Text(r, "updated_at")!, DeletedAt = Sql.Text(r, "deleted_at"),
        LocalRevision = Sql.Number(r, "local_revision"), IsDirty = Sql.Number(r, "is_dirty") != 0, PushState = Sql.Text(r, "push_state")!
    };

    private static Dictionary<string, object?> Values(EventRecord e) => new()
    {
        ["event_uid"] = e.EventUid, ["google_event_id"] = e.GoogleEventId, ["parent_event_id"] = e.ParentEventId,
        ["google_recurring_id"] = e.GoogleRecurringId, ["original_start_key"] = e.OriginalStartKey, ["status"] = e.Status,
        ["title"] = e.Title, ["memo"] = e.Memo, ["google_start_raw"] = e.GoogleStartRaw, ["google_end_raw"] = e.GoogleEndRaw, ["time_zone"] = e.TimeZone,
        ["start_date"] = e.StartDate, ["end_date"] = e.EndDate, ["start_time"] = e.StartTime, ["end_time"] = e.EndTime,
        ["recurrence_rule"] = e.RecurrenceRule, ["color_id"] = e.ColorId, ["is_busy"] = e.IsBusy, ["is_done"] = e.IsDone, ["is_dday"] = e.IsDday,
        ["created_by"] = e.CreatedBy, ["created_at"] = e.CreatedAt, ["updated_at"] = e.UpdatedAt, ["deleted_at"] = e.DeletedAt,
        ["local_revision"] = e.LocalRevision, ["is_dirty"] = e.IsDirty, ["push_state"] = e.PushState
    };
}
