namespace DeskCal.App.Calendar;

public sealed record ChipModel(
    string Title, string? TimeText, string ColorHex,
    bool IsDone, bool IsTodo, bool IsDday,
    string? EventId, string? OccurrenceId, string? TodoId, string CreatedBy)
{
    // 여러 날 일정 막대의 한 칸. 앞날·다음 날 칸과 이어 그린다.
    public bool JoinsLeft { get; init; }
    public bool JoinsRight { get; init; }
    public string? PeriodText { get; init; }
    // 위 줄의 막대 높이를 맞추려고 비워 두는 자리.
    public bool IsPlaceholder { get; init; }

    public static readonly ChipModel Placeholder =
        new("", null, "#00000000", false, false, false, null, null, null, "user") { IsPlaceholder = true };
}

public sealed record DayCellModel(
    DateOnly Date, bool IsCurrentMonth, bool IsToday, DayOfWeek DayOfWeek,
    IReadOnlyList<ChipModel> Chips, int OverflowCount, IReadOnlyList<ChipModel> AllItems, string? HolidayName = null)
{
    public bool IsHoliday => HolidayName is not null;
}

public sealed record MonthModel(int Year, int Month, IReadOnlyList<IReadOnlyList<DayCellModel>> Weeks);

public sealed record WeekBlockModel(ChipModel Chip, int StartMinute, int EndMinute, int Column, int ColumnCount);

public sealed record WeekDayModel(DateOnly Date, bool IsToday, IReadOnlyList<ChipModel> AllDay, IReadOnlyList<WeekBlockModel> Blocks,
    string? HolidayName = null)
{
    public bool IsHoliday => HolidayName is not null;
}

public sealed record WeekModel(DateOnly WeekStart, IReadOnlyList<WeekDayModel> Days);
