namespace DeskCal.Core.Tests;

public sealed class ChangeNotificationTests
{
    [Fact]
    public void DataVersionTracksCommitsOnAnotherConnection()
    {
        using var db = new TestDatabase();
        using var watcher = new DataVersionWatcher(db.Database);
        Assert.False(watcher.HasChanged());
        Assert.False(watcher.HasChanged());

        db.Todos.Create([new("first")]);
        Assert.True(watcher.HasChanged());
        Assert.False(watcher.HasChanged());

        db.Todos.Create([new("second")]);
        Assert.True(watcher.HasChanged());
        Assert.False(watcher.HasChanged());
    }

    [Fact]
    public void DataVersionIgnoresUncommittedAndRolledBackWrites()
    {
        using var db = new TestDatabase();
        using var watcher = new DataVersionWatcher(db.Database);
        Assert.False(watcher.HasChanged());
        using var connection = db.Database.OpenConnection();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO category(color_id,name,color_hex) VALUES ('test','test','#123456');";
        command.ExecuteNonQuery();
        Assert.False(watcher.HasChanged());
        transaction.Rollback();
        Assert.False(watcher.HasChanged());
    }

}
