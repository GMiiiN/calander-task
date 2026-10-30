namespace DeskCal.Core.Tests;

public sealed class TimeTests
{
    [Fact]
    public void MultiDayTimedEventRequiresExplicitEndTime()
    {
        using var db = new TestDatabase();
        var error = Assert.Throws<ArgumentException>(() => db.Events.Create([
            new("multi", "2026-10-30") { EndDate = "2026-11-01", StartTime = "09:00" }
        ]));
        Assert.Contains("end_time", error.Message);
        Assert.Empty(db.Events.GetRecords());
        var valid = db.Events.Create([new("multi", "2026-10-30") { EndDate = "2026-11-01", StartTime = "09:00", EndTime = "10:00" }])[0];
        Assert.Equal("2026-11-01", valid.EndDate);
        Assert.Throws<ArgumentException>(() => db.Events.Update(valid.EventUid, new() { EndTime = (string?)null }));
        Assert.Equal(1, db.Events.Get(valid.EventUid).LocalRevision);
    }

    [Fact]
    public void AllDayRoundTripUsesExclusiveSourceEnd()
    {
        var time = new EventTime(TestDatabase.Seoul);
        var normalized = time.Normalize(new() { StartDate = "2026-10-30", EndDate = "2026-11-02" });
        Assert.Equal("2026-11-03", normalized.GoogleEndRaw);
        var derived = time.Derive(normalized);
        Assert.Equal("2026-11-02", derived.EndDate);
        Assert.Null(derived.TimeZone);
        Assert.Null(derived.StartTime);
    }

    [Fact]
    public void RawSecondsAndTimezoneSurviveUnrelatedUpdate()
    {
        using var db = new TestDatabase();
        var value = db.Events.Create([new("Imported", "2026-10-30")])[0];
        db.Execute("UPDATE event SET google_start_raw='2026-10-30T09:00:23-07:00', google_end_raw='2026-10-30T10:00:45-07:00', time_zone='America/Los_Angeles' WHERE event_id=$id", ("$id", value.EventId));
        var updated = db.Events.Update(value.EventUid, new() { Title = "Retitled" });
        Assert.Equal("2026-10-30T09:00:23-07:00", updated.GoogleStartRaw);
        Assert.Equal("2026-10-30T10:00:45-07:00", updated.GoogleEndRaw);
        Assert.Equal("America/Los_Angeles", updated.TimeZone);
        var occurrence = Assert.Single(db.Events.List(new(2026, 10, 31), new(2026, 10, 31)));
        Assert.Equal("01:00", occurrence.Event.StartTime);
        Assert.Equal(23, occurrence.Start.Second);
        Assert.Equal(45, occurrence.End.Second);
    }

    [Fact]
    public void WindowsTimezoneMapsToIanaAndSpringGapIsRejected()
    {
        var time = new EventTime(TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time"));
        Assert.Equal("America/Los_Angeles", time.IanaId);
        Assert.Throws<ArgumentException>(() => time.Normalize(new() { StartDate = "2026-03-08", EndDate = "2026-03-08", StartTime = "02:30", EndTime = "03:30" }));
    }
}
