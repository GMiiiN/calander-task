namespace DeskCal.Core;

public sealed record Semester(bool Enabled, DateOnly Start, int Weeks)
{
    public DayOfWeek WeekStart => Enabled ? Start.DayOfWeek : DayOfWeek.Monday;

    public int? WeekOn(DateOnly date)
    {
        var days = date.DayNumber - Start.DayNumber;
        if (!Enabled || days < 0) return null;
        var week = days / 7 + 1;
        return week <= Weeks ? week : null;
    }
}
