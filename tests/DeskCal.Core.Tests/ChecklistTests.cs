namespace DeskCal.Core.Tests;

public sealed class ChecklistTests
{
    [Fact]
    public void RepeatingSeriesRejectsChecklistWithoutOccurrence()
    {
        using var db = new TestDatabase();
        var series = db.Events.Create([new EventInput("카공", "2026-11-08") { Repeat = new RecurrenceInput("weekly", Weekdays: [DayOfWeek.Sunday]) }])[0];

        Assert.Throws<ArgumentException>(() => db.Events.AddChecklistItems(series.EventUid, false, ["안 됨"]));
    }

    [Fact]
    public void OccurrencesKeepSeparateChecklists()
    {
        using var db = new TestDatabase();
        db.Events.Create([new EventInput("카공", "2026-11-08") { Repeat = new RecurrenceInput("weekly", Weekdays: [DayOfWeek.Sunday]) }]);
        var list = db.Events.List(new DateOnly(2026, 11, 8), new DateOnly(2026, 11, 15));

        db.Events.AddChecklistItems(list[0].OccurrenceId!, true, ["첫 주"]);
        db.Events.AddChecklistItems(list[1].OccurrenceId!, true, ["둘째 주"]);

        var after = db.Events.List(new DateOnly(2026, 11, 8), new DateOnly(2026, 11, 15));
        Assert.Equal("첫 주", Assert.Single(after[0].Event.Checklist).Text);
        Assert.Equal("둘째 주", Assert.Single(after[1].Event.Checklist).Text);
    }

    [Fact]
    public void AddingToOneItemDoesNotChangeTheOthersOrder()
    {
        var items = Checklist.Add(Checklist.Add([], ["하나", "둘"]), ["셋"]);
        items = Checklist.Update(items, items[1].ItemId, isDeleted: true);

        Assert.Equal(["하나", "셋"], Checklist.Visible(items).Select(i => i.Text));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void RejectsEmptyText(string text) => Assert.Throws<ArgumentException>(() => Checklist.Add([], [text]));

    [Fact]
    public void RejectsTextOverTheLimitAndTooManyItems()
    {
        Assert.Throws<ArgumentException>(() => Checklist.Add([], [new string('가', Checklist.MAX_TEXT_LENGTH + 1)]));
        Assert.Throws<InvalidOperationException>(() => Checklist.Add([], Enumerable.Repeat("항목", Checklist.MAX_ITEMS + 1).ToArray()));
    }

    [Fact]
    public void LongestTextsFitGoogleLimits()
    {
        var items = Checklist.Add([], Enumerable.Repeat(new string('"', Checklist.MAX_TEXT_LENGTH), Checklist.MAX_ITEMS).ToArray());
        var properties = new Dictionary<string, string>();

        Checklist.WriteProperties(items, properties);

        Assert.All(properties, p => Assert.True(p.Key.Length <= 44 && p.Value.Length <= 1024));
        Assert.True(properties.Sum(p => System.Text.Encoding.UTF8.GetByteCount(p.Key + p.Value)) < 32_000);
        Assert.Equal(items, Checklist.ReadProperties(properties));
    }

    [Fact]
    public void LongestKoreanTextsFitGoogleLimits()
    {
        var items = Checklist.Add([], Enumerable.Repeat(new string('가', Checklist.MAX_TEXT_LENGTH), Checklist.MAX_ITEMS).ToArray());
        var properties = new Dictionary<string, string>();

        Checklist.WriteProperties(items, properties);

        Assert.True(properties.Sum(p => System.Text.Encoding.UTF8.GetByteCount(p.Key + p.Value)) < 32_000);
    }

    [Fact]
    public void ControlCharactersThatWouldOverflowGoogleValueAreRejected() =>
        Assert.Throws<ArgumentException>(() => Checklist.Add([], ["a" + new string((char)1, 198) + "a"]));

    [Fact]
    public void UpdateThatChangesNothingKeepsTheRevision()
    {
        var items = Checklist.Add([], ["안건"]);

        var same = Checklist.Update(items, items[0].ItemId, text: "안건", isDone: false);

        Assert.Equal(1, same[0].Revision);
    }

    [Fact]
    public void BrokenRemoteValueIsSkipped()
    {
        var properties = new Dictionary<string, string> { ["deskcalCheckabc"] = "not json", ["deskcalUid"] = "x" };

        Assert.Empty(Checklist.ReadProperties(properties));
    }

    [Fact]
    public void MergeKeepsTheHigherRevisionPerItem()
    {
        var a = new ChecklistItem("a", "로컬 새것", true, 1, 3);
        var b = new ChecklistItem("b", "로컬 옛것", false, 2, 1);
        var merged = Checklist.Merge([a, b], [a with { Text = "원격 옛것", Revision = 2 }, b with { Text = "원격 새것", Revision = 2 }, new("c", "원격만", false, 3, 1)]);

        Assert.Equal(["로컬 새것", "원격 새것", "원격만"], merged.Select(i => i.Text));
    }
}
