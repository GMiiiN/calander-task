namespace DeskCal.Core.Tests;

public sealed class RecurrenceTests
{
    private static EventRecord AllDay(string start, string end, string rule) => new()
    {
        EventId = 1, EventUid = "0123456789abcdef0123456789abcdef", Title = "series",
        StartDate = start, EndDate = end, GoogleStartRaw = start,
        GoogleEndRaw = DateOnly.Parse(end).AddDays(1).ToString("yyyy-MM-dd"), RecurrenceRule = rule
    };

    [Theory]
    [InlineData("2026-01-31", "RRULE:FREQ=MONTHLY;COUNT=3", "2026-01-31,2026-03-31,2026-05-31")]
    [InlineData("2024-02-29", "RRULE:FREQ=YEARLY;COUNT=3", "2024-02-29,2028-02-29,2032-02-29")]
    [InlineData("2026-10-30", "RRULE:FREQ=WEEKLY;INTERVAL=2;BYDAY=FR;COUNT=3", "2026-10-30,2026-11-13,2026-11-27")]
    [InlineData("2026-10-30", "RRULE:FREQ=DAILY;UNTIL=20261101", "2026-10-30,2026-10-31,2026-11-01")]
    public void CalendarEdgesAndBounds(string start, string rule, string expected)
    {
        var series = AllDay(start, start, rule);
        var result = new RecurrenceExpander(TestDatabase.Seoul).Expand(series, [], DateOnly.Parse(start), new(2033, 1, 1));
        Assert.Equal(expected.Split(','), result.Select(o => o.Event.StartDate));
    }

    [Fact]
    public void OverseasRecurrenceKeepsWallTimeAcrossDstAndMatchesUtcExceptionKeys()
    {
        var series = new EventRecord
        {
            EventId = 1, EventUid = new string('a', 32), Title = "LA morning",
            GoogleStartRaw = "2026-10-25T09:00:12-07:00", GoogleEndRaw = "2026-10-25T10:00:12-07:00",
            TimeZone = "America/Los_Angeles", RecurrenceRule = "RRULE:FREQ=WEEKLY;COUNT=3"
        };
        var expander = new RecurrenceExpander(TestDatabase.Seoul);
        var result = expander.Expand(series, [], new(2026, 10, 25), new(2026, 11, 9));
        Assert.Equal(new[] { "01:00", "02:00", "02:00" }, result.Select(o => o.Event.StartTime));
        Assert.Equal(new[] { "2026-10-25T16:00:12Z", "2026-11-01T17:00:12Z", "2026-11-08T17:00:12Z" }, result.Select(o => o.Event.OriginalStartKey));
        var exception = result[1].Event with { ParentEventId = 1, EventUid = new string('b', 32), Status = "cancelled", RecurrenceRule = null };
        var filtered = expander.Expand(series, [exception], new(2026, 10, 25), new(2026, 11, 9));
        Assert.Equal(2, filtered.Count);
        Assert.DoesNotContain(filtered, o => o.Event.OriginalStartKey == exception.OriginalStartKey);
        Assert.NotNull(expander.GetOriginal(series, "2026-11-01T17:00:12Z"));
        Assert.Null(expander.GetOriginal(series, "2026-11-01T16:00:12Z"));
    }

    [Fact]
    public void MovedExceptionIsIncludedByActualRangeAndOriginalIsRemoved()
    {
        var series = AllDay("2026-11-01", "2026-11-01", "RRULE:FREQ=DAILY;COUNT=2");
        var moved = series with
        {
            EventId = 2, EventUid = new string('b', 32), ParentEventId = 1, OriginalStartKey = "2026-11-01", RecurrenceRule = null,
            StartDate = "2026-12-01", EndDate = "2026-12-01", GoogleStartRaw = "2026-12-01", GoogleEndRaw = "2026-12-02"
        };
        var expander = new RecurrenceExpander(TestDatabase.Seoul);
        Assert.Equal("2026-11-02", Assert.Single(expander.Expand(series, [moved], new(2026, 11, 1), new(2026, 11, 30))).Event.StartDate);
        var december = Assert.Single(expander.Expand(series, [moved], new(2026, 12, 1), new(2026, 12, 31)));
        Assert.Equal(series.EventUid + "~2026-11-01", december.OccurrenceId);
        Assert.Equal(series.EventUid, december.Event.EventUid);
        Assert.Equal("2026-12-01", expander.Next(series, [moved], new(2026, 11, 3))!.Event.StartDate);
    }

    [Fact]
    public void MultiDayOccurrenceStartingBeforeRangeIsNotLost()
    {
        var series = AllDay("2026-10-28", "2026-11-02", "RRULE:FREQ=MONTHLY;COUNT=2");
        var result = new RecurrenceExpander(TestDatabase.Seoul).Expand(series, [], new(2026, 11, 1), new(2026, 11, 1));
        Assert.Equal("2026-10-28", Assert.Single(result).Event.StartDate);
        Assert.Empty(new RecurrenceExpander(TestDatabase.Seoul).Expand(series, [], new(2026, 11, 3), new(2026, 11, 3)));
    }

    [Fact]
    public void ExdatesAndRdatesFromImportedRulesArePreserved()
    {
        var series = AllDay("2026-11-01", "2026-11-01", "RRULE:FREQ=DAILY;COUNT=3\r\nEXDATE;VALUE=DATE:20261102\r\nRDATE;VALUE=DATE:20261110");
        var result = new RecurrenceExpander(TestDatabase.Seoul).Expand(series, [], new(2026, 11, 1), new(2026, 11, 30));
        Assert.Equal(new[] { "2026-11-01", "2026-11-03", "2026-11-10" }, result.Select(o => o.Event.StartDate));
        Assert.False(RecurrenceInput.IsEditable(series.RecurrenceRule));
    }

    [Theory]
    [InlineData("daily", "RRULE:FREQ=DAILY;INTERVAL=2;COUNT=3")]
    [InlineData("monthly", "RRULE:FREQ=MONTHLY;INTERVAL=2;COUNT=3")]
    [InlineData("yearly", "RRULE:FREQ=YEARLY;INTERVAL=2;COUNT=3")]
    public void SupportedFrequencyConversion(string frequency, string expected)
    {
        var rule = new RecurrenceInput(frequency, 2, Count: 3).ToRule(AllDay("2026-10-30", "2026-10-30", ""));
        Assert.Equal(expected, rule);
        Assert.True(RecurrenceInput.IsEditable(rule));
    }

    [Fact]
    public void WeeklyAndUntilConversion()
    {
        var allDay = AllDay("2026-10-30", "2026-10-30", "");
        Assert.Equal("RRULE:FREQ=WEEKLY;INTERVAL=1;BYDAY=MO,FR;UNTIL=20261106", new RecurrenceInput("weekly", Weekdays: [DayOfWeek.Friday, DayOfWeek.Monday], Until: "2026-11-06").ToRule(allDay));
        var timed = new EventTime(TestDatabase.Seoul).Normalize(allDay with { StartTime = "09:00", EndTime = "10:00" });
        Assert.Equal("RRULE:FREQ=DAILY;INTERVAL=1;UNTIL=20261031T145959Z", new RecurrenceInput("daily", Until: "2026-10-31").ToRule(timed));
    }

    [Fact]
    public void UnsupportedInputsAreRejected()
    {
        var value = AllDay("2026-10-30", "2026-10-30", "");
        RecurrenceInput[] invalid =
        [
            new("hourly"), new("daily", 0), new("daily", Count: 0), new("daily", Until: "2026-11-01", Count: 2),
            new("weekly"), new("weekly", Weekdays: [DayOfWeek.Monday]), new("monthly", Weekdays: [DayOfWeek.Friday]),
            new("daily", Until: "2026-10-29"), new("daily", Until: "2026-02-30"), new("weekly", Weekdays: [DayOfWeek.Friday, (DayOfWeek)8])
        ];
        foreach (var input in invalid) Assert.Throws<ArgumentException>(() => input.ToRule(value));
        Assert.False(RecurrenceInput.IsEditable("RRULE:FREQ=MONTHLY;BYDAY=1MO"));
        Assert.False(RecurrenceInput.IsEditable("RRULE:FREQ=YEARLY;BYMONTH=10"));
        Assert.False(RecurrenceInput.IsEditable("RRULE:FREQ=WEEKLY;WKST=SU"));
    }

    [Theory]
    [InlineData("2026-10-30")]
    [InlineData("2026-10-30T09:00:00Z")]
    public void OccurrenceIdentityRoundTrip(string key)
    {
        var uid = new string('a', 32);
        Assert.Equal((uid, key), OccurrenceId.Parse(OccurrenceId.Create(uid, key)));
    }

    [Theory]
    [InlineData("wrong~2026-10-30")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa~2026-02-30")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa~2026-10-30T09:00:00+09:00")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa~2026-10-30~extra")]
    public void InvalidOccurrenceIdentity(string value) => Assert.Throws<ArgumentException>(() => OccurrenceId.Parse(value));
}
