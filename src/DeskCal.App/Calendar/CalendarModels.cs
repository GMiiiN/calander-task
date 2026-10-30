namespace DeskCal.App.Calendar;

public sealed record ChipModel(
    string Title, string? TimeText, string ColorHex,
    bool IsDone, bool IsTodo, bool IsDday,
    string? EventId, string? OccurrenceId, string? TodoId, string CreatedBy);

public sealed record DayCellModel(
    DateOnly Date, bool IsCurrentMonth, bool IsToday, DayOfWeek DayOfWeek,
    IReadOnlyList<ChipModel> Chips, int OverflowCount, IReadOnlyList<ChipModel> AllItems);

public sealed record MonthModel(int Year, int Month, IReadOnlyList<IReadOnlyList<DayCellModel>> Weeks);

public sealed record WeekBlockModel(ChipModel Chip, int StartMinute, int EndMinute, int Column, int ColumnCount);

public sealed record WeekDayModel(DateOnly Date, bool IsToday, IReadOnlyList<ChipModel> AllDay, IReadOnlyList<WeekBlockModel> Blocks);

public sealed record WeekModel(DateOnly WeekStart, IReadOnlyList<WeekDayModel> Days);
