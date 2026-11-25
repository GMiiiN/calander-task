namespace DeskCal.Core.Tests;

public class SemesterTests
{
    [Theory]
    [InlineData(-1, null)]
    [InlineData(0, 1)]
    [InlineData(6, 1)]
    [InlineData(7, 2)]
    [InlineData(111, 16)]
    [InlineData(112, null)]
    public void CountsSevenDayWeeksFromStart(int offset, int? expected)
    {
        var term = new Semester(true, new(2026, 9, 2), 16);
        var date = term.Start.AddDays(offset);
        Assert.Equal(expected, term.WeekOn(date));
        Assert.Null((term with { Enabled = false }).WeekOn(date));
    }
}
