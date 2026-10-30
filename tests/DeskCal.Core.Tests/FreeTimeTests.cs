namespace DeskCal.Core.Tests;

public sealed class FreeTimeTests
{
    [Fact]
    public void ClipsBusyIntervalsMergesOverlapAndAdjacentBlocksAndIgnoresTransparentAndAllDay()
    {
        using var db = new TestDatabase();
        db.Events.Create([
            new("all day", "2026-10-30"),
            new("transparent", "2026-10-30") { StartTime = "14:00", EndTime = "18:00", IsBusy = false },
            new("touch start", "2026-10-30") { StartTime = "08:00", EndTime = "09:00" },
            new("span start", "2026-10-30") { StartTime = "08:30", EndTime = "10:00" },
            new("adjacent", "2026-10-30") { StartTime = "10:00", EndTime = "11:00" },
            new("overlap", "2026-10-30") { StartTime = "10:30", EndTime = "12:00" },
            new("afternoon", "2026-10-30") { StartTime = "13:00", EndTime = "14:00" },
            new("touch end", "2026-10-30") { StartTime = "22:00", EndTime = "23:00" }
        ]);
        var slots = new FreeTimeFinder(db.Events).Find(new(2026, 10, 30), new(2026, 10, 30), 60);
        Assert.Equal(2, slots.Count);
        Assert.Equal(new DateTime(2026, 10, 30, 12, 0, 0), slots[0].Start);
        Assert.Equal(new DateTime(2026, 10, 30, 13, 0, 0), slots[0].End);
        Assert.Equal(new DateTime(2026, 10, 30, 14, 0, 0), slots[1].Start);
        Assert.Equal(new DateTime(2026, 10, 30, 22, 0, 0), slots[1].End);
    }

    [Fact]
    public void DurationAndRangeAreValidated()
    {
        using var db = new TestDatabase();
        var finder = new FreeTimeFinder(db.Events);
        Assert.Throws<ArgumentException>(() => finder.Find(new(2026, 10, 1), new(2026, 10, 1), 0));
        Assert.Throws<ArgumentException>(() => finder.Find(new(2026, 10, 1), new(2026, 10, 1), -1));
        Assert.Throws<ArgumentException>(() => finder.Find(new(2026, 10, 2), new(2026, 10, 1), 60));
        Assert.Throws<ArgumentException>(() => finder.Find(new(2026, 10, 1), new(2026, 11, 1), 60));
        Assert.Throws<ArgumentException>(() => finder.Find(new(2026, 10, 1), new(2026, 10, 1), 60, new(22, 0), new(9, 0)));
        Assert.Equal(31, finder.Find(new(2026, 10, 1), new(2026, 10, 31), 60).Count);
        Assert.Empty(finder.Find(new(2026, 10, 1), new(2026, 10, 1), 781));
        Assert.Single(finder.Find(new(2026, 10, 1), new(2026, 10, 1), 780));
    }

    [Fact]
    public void InternalQueryIsNotTruncatedAndIncludesRecurringAndMultiDayEvents()
    {
        using var db = new TestDatabase();
        for (var batch = 0; batch < 5; batch++)
            db.Events.Create(Enumerable.Repeat(new EventInput("transparent", "2026-10-30") { StartTime = "09:00", EndTime = "10:00", IsBusy = false }, 50).ToArray());
        db.Events.Create([
            new("real busy", "2026-10-29") { StartTime = "23:00", EndDate = "2026-10-30", EndTime = "10:00" },
            new("repeating", "2026-10-01") { StartTime = "10:00", EndTime = "11:00", Repeat = new("daily") }
        ]);
        Assert.Equal(252, db.Events.List(new(2026, 10, 30), new(2026, 10, 30)).Count);
        var slot = Assert.Single(new FreeTimeFinder(db.Events).Find(new(2026, 10, 30), new(2026, 10, 30), 30));
        Assert.Equal(11, slot.Start.Hour);
        Assert.Equal(22, slot.End.Hour);
    }

    [Fact]
    public void MidnightEndDoesNotBlockFollowingDay()
    {
        using var db = new TestDatabase();
        db.Events.Create([new("overnight", "2026-10-29") { StartTime = "23:00", EndDate = "2026-10-30", EndTime = "00:00" }]);
        Assert.Empty(db.Events.List(new(2026, 10, 30), new(2026, 10, 30)));
        Assert.Single(new FreeTimeFinder(db.Events).Find(new(2026, 10, 30), new(2026, 10, 30), 60, new(0, 0), new(1, 0)));
    }
}
