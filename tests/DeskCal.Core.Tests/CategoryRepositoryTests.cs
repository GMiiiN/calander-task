namespace DeskCal.Core.Tests;

public sealed class CategoryRepositoryTests
{
    [Fact]
    public void ListsSeededColorsInNumericOrder()
    {
        using var db = new TestDatabase();
        var categories = new CategoryRepository(db.Database).List();
        Assert.Equal(11, categories.Count);
        Assert.Equal(["1", "2", "3", "4", "5", "6", "7", "8", "9", "10", "11"], categories.Select(c => c.ColorId));
    }

    [Fact]
    public void RenamesTrimmedNamesAndKeepsOthers()
    {
        using var db = new TestDatabase();
        var repository = new CategoryRepository(db.Database);
        var before = repository.List();
        repository.Rename(new Dictionary<string, string> { ["1"] = "  업무 ", ["2"] = "운동" });
        var after = repository.List();
        Assert.Equal("업무", after[0].Name);
        Assert.Equal("운동", after[1].Name);
        Assert.Equal(before.Skip(2), after.Skip(2));
        Assert.Equal(before[0].ColorHex, after[0].ColorHex);
    }

    [Fact]
    public void RejectsEmptyDuplicateIdLikeAndUnknownWithoutChanges()
    {
        using var db = new TestDatabase();
        var repository = new CategoryRepository(db.Database);
        var before = repository.List();
        Assert.Throws<ArgumentException>(() => repository.Rename(new Dictionary<string, string> { ["1"] = "  " }));
        Assert.Throws<ArgumentException>(() => repository.Rename(new Dictionary<string, string> { ["1"] = "같음", ["2"] = "같음" }));
        Assert.Throws<ArgumentException>(() => repository.Rename(new Dictionary<string, string> { ["1"] = before[1].Name }));
        Assert.Throws<ArgumentException>(() => repository.Rename(new Dictionary<string, string> { ["1"] = "5" }));
        Assert.Throws<ArgumentException>(() => repository.Rename(new Dictionary<string, string> { ["99"] = "없음" }));
        Assert.Equal(before, repository.List());
    }

    [Fact]
    public void SwappingTwoNamesInOneCallIsAllowed()
    {
        using var db = new TestDatabase();
        var repository = new CategoryRepository(db.Database);
        var before = repository.List();
        repository.Rename(new Dictionary<string, string> { ["1"] = before[1].Name, ["2"] = before[0].Name });
        var after = repository.List();
        Assert.Equal(before[1].Name, after[0].Name);
        Assert.Equal(before[0].Name, after[1].Name);
    }
}
