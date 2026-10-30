namespace DeskCal.Core;

// 기본값은 생략이며, 명시적인 null과 구분한다.
public readonly record struct Patch<T>(bool IsSpecified, T Value)
{
    public static implicit operator Patch<T>(T value) => new(true, value);
}

public sealed record EventInput(string Title, string StartDate)
{
    public string? EndDate { get; init; }
    public string? StartTime { get; init; }
    public string? EndTime { get; init; }
    public string? Memo { get; init; }
    public string? ColorId { get; init; }
    public bool IsBusy { get; init; } = true;
    public bool IsDday { get; init; }
    public string CreatedBy { get; init; } = "user";
    public RecurrenceInput? Repeat { get; init; }
}

public sealed record EventPatch
{
    public Patch<string?> Title { get; init; }
    public Patch<string?> StartDate { get; init; }
    public Patch<string?> EndDate { get; init; }
    public Patch<string?> StartTime { get; init; }
    public Patch<string?> EndTime { get; init; }
    public Patch<string?> Memo { get; init; }
    public Patch<string?> ColorId { get; init; }
    public Patch<bool?> IsBusy { get; init; }
    public Patch<bool?> IsDone { get; init; }
    public Patch<bool?> IsDday { get; init; }
    public Patch<RecurrenceInput?> Repeat { get; init; }
    public bool ChangesTime => StartDate.IsSpecified || EndDate.IsSpecified || StartTime.IsSpecified || EndTime.IsSpecified;
}

public sealed record EventRecord
{
    public long EventId { get; init; }
    public string EventUid { get; init; } = "";
    public string? GoogleEventId { get; init; }
    public long? ParentEventId { get; init; }
    public string? GoogleRecurringId { get; init; }
    public string? OriginalStartKey { get; init; }
    public string Status { get; init; } = "confirmed";
    public string Title { get; init; } = "";
    public string Memo { get; init; } = "";
    public string? GoogleStartRaw { get; init; }
    public string? GoogleEndRaw { get; init; }
    public string? TimeZone { get; init; }
    public string StartDate { get; init; } = "";
    public string EndDate { get; init; } = "";
    public string? StartTime { get; init; }
    public string? EndTime { get; init; }
    public string? RecurrenceRule { get; init; }
    public string? ColorId { get; init; }
    public bool IsBusy { get; init; } = true;
    public bool IsDone { get; init; }
    public bool IsDday { get; init; }
    public string CreatedBy { get; init; } = "user";
    public string CreatedAt { get; init; } = "";
    public string UpdatedAt { get; init; } = "";
    public string? DeletedAt { get; init; }
    public long LocalRevision { get; init; }
    public bool IsDirty { get; init; }
    public string PushState { get; init; } = "synced";
}

public sealed record EventOccurrence(EventRecord Event, string? OccurrenceId, DateTime Start, DateTime End)
{
    public bool IsAllDay => Event.StartTime is null;
}

public sealed record TodoInput(string Title, string? DueDate = null, string? Memo = null, string CreatedBy = "user");

public sealed record TodoPatch
{
    public Patch<string?> Title { get; init; }
    public Patch<string?> DueDate { get; init; }
    public Patch<string?> Memo { get; init; }
    public Patch<bool?> IsDone { get; init; }
}

public sealed record TodoRecord
{
    public long TodoId { get; init; }
    public string TodoUid { get; init; } = "";
    public string Title { get; init; } = "";
    public string Memo { get; init; } = "";
    public string? DueDate { get; init; }
    public bool IsDone { get; init; }
    public string? CompletedAt { get; init; }
    public string? Position { get; init; }
    public string CreatedBy { get; init; } = "user";
    public string CreatedAt { get; init; } = "";
    public string UpdatedAt { get; init; } = "";
    public string? DeletedAt { get; init; }
    public long LocalRevision { get; init; }
    public bool IsDirty { get; init; }
    public string PushState { get; init; } = "synced";
}
