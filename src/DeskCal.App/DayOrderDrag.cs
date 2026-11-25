using DeskCal.App.Calendar;

namespace DeskCal.App;

/// <summary>
/// 같은 날 일정의 순서를 끌어 바꾸는 규칙(U6). 달력 칸·우측 목록·+N 목록이 함께 쓴다.
/// 여러 날 일정과 할 일은 끌 수 없고, 완료 상태가 같은 단일 일정끼리만 받는다(DayEventOrder.Move와 같은 규칙).
/// </summary>
public sealed record DayOrderItem(DateOnly Day, string Key, bool IsDone)
{
    public static DayOrderItem? From(object? data) => data switch
    {
        ChipModel { OrderDate: { } day, EventId: { } id } chip => new(day, chip.OccurrenceId ?? id, chip.IsDone),
        SidebarItemViewModel { OrderDate: { } day, EventId: { } id } row => new(day, row.OccurrenceId ?? id, row.IsDone),
        _ => null
    };

    public bool Accepts(DayOrderItem? target) => target is not null && target.Day == Day && target.IsDone == IsDone && target.Key != Key;

    // DayEventOrder.Move는 아래로 옮기면 대상 다음 자리, 위로 옮기면 대상 앞자리에 넣는다. +N 목록은 놓기 전에 그 자리에 선을 긋는다.
    public bool LandsBelow(IEnumerable<ChipModel> shown, DayOrderItem target)
    {
        var keys = shown.Select(chip => chip.OccurrenceId ?? chip.EventId).ToList();
        var from = keys.IndexOf(Key);
        return from >= 0 && from < keys.IndexOf(target.Key);
    }
}
