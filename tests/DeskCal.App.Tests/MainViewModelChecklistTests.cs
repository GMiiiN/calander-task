using System.IO;
using DeskCal.Core;

namespace DeskCal.App.Tests;

public sealed class MainViewModelChecklistTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "deskcal-" + Guid.NewGuid().ToString("N") + ".db");
    private readonly Database _database;
    private readonly EventRepository _events;
    private readonly string _eventId;

    public MainViewModelChecklistTests()
    {
        _database = new Database(_path);
        _database.Initialize();
        _events = new EventRepository(_database);
        _eventId = _events.Create([new EventInput("회의", DateOnly.FromDateTime(DateTime.Now).ToString("yyyy-MM-dd"))])[0].EventUid;
    }

    public void Dispose()
    {
        foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(_path + suffix);
    }

    private static SidebarItemViewModel Item(MainViewModel model) => model.Groups.SelectMany(g => g.Items).Single(i => i.EventId is not null);

    [Fact]
    public void ToggleAppearsOnlyWithItemsAndExpansionIsNotRemembered()
    {
        var model = new MainViewModel(_database);
        Assert.False(Item(model).HasChecklist);

        _events.AddChecklistItems(_eventId, false, ["안건"]);
        model.Refresh();
        Assert.True(Item(model).HasChecklist);
        Assert.False(Item(model).IsExpanded);

        model.ToggleChecklistCommand.Execute(Item(model));
        model.Refresh();
        Assert.True(Item(model).IsExpanded);
        Assert.False(Item(new MainViewModel(_database)).IsExpanded);
    }

    [Fact]
    public void CheckingAndDeletingChangeTheStoredItem()
    {
        _events.AddChecklistItems(_eventId, false, ["안건", "자료"]);
        var model = new MainViewModel(_database);

        model.ToggleChecklistItemCommand.Execute(Item(model).Checklist![0]);
        Assert.True(Item(model).Checklist![0].IsDone);

        model.DeleteChecklistItem(Item(model).Checklist![1]);
        Assert.Single(Item(model).Checklist!);
        Assert.Equal(2, _events.Get(_eventId).Checklist.Count);
    }

    [Fact]
    public void EditingSurvivesRefreshAndEmptyTextDoesNotDeleteTheItem()
    {
        _events.AddChecklistItems(_eventId, false, ["안건"]);
        var model = new MainViewModel(_database);
        var item = Item(model).Checklist![0];

        model.BeginChecklistEdit(item);
        Assert.True(Item(model).Checklist![0].IsEditing);
        model.Refresh();
        Assert.True(Item(model).Checklist![0].IsEditing);

        model.EndChecklistEdit(item, "   ");
        Assert.Equal("안건", Item(model).Checklist![0].Text);
        Assert.False(Item(model).Checklist![0].IsEditing);

        model.BeginChecklistEdit(item);
        model.EndChecklistEdit(item, null);
        Assert.Equal("안건", Item(model).Checklist![0].Text);

        model.BeginChecklistEdit(item);
        model.EndChecklistEdit(item, "바뀐 안건");
        Assert.Equal("바뀐 안건", Item(model).Checklist![0].Text);
    }
}
