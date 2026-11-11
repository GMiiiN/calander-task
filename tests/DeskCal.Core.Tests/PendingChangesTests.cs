using System.Globalization;

namespace DeskCal.Core.Tests;

public sealed class PendingChangesTests
{
    private static string At(DateTimeOffset time) => time.ToString("O", CultureInfo.InvariantCulture);

    [Fact]
    public void OnlyUnsentChangesAfterTheGivenTimeCount()
    {
        using var db = new TestDatabase();
        var syncStarted = db.Clock.Now;

        // 동기화 시작 전에 바뀌어 남은 행(예: 그 동기화에서 실패한 것)에는 반응하지 않는다. 그래야 자기 신호로 되풀이되지 않는다.
        db.Clock.Now = syncStarted.AddMinutes(-1);
        db.Todos.Create([new TodoInput("동기화 전에 만든 할 일")]);
        Assert.False(PendingChanges.ExistSince(db.Database, At(syncStarted)));

        db.Clock.Now = syncStarted.AddSeconds(5);
        db.Todos.Create([new TodoInput("Claude가 방금 만든 할 일")]);
        Assert.True(PendingChanges.ExistSince(db.Database, At(syncStarted)));
    }

    [Fact]
    public void EventChangesCountToo()
    {
        using var db = new TestDatabase();
        var syncStarted = db.Clock.Now;
        db.Clock.Now = syncStarted.AddSeconds(5);
        db.Events.Create([new EventInput("방금 만든 일정", "2026-11-12")]);

        Assert.True(PendingChanges.ExistSince(db.Database, At(syncStarted)));
        Assert.False(PendingChanges.ExistSince(db.Database, At(db.Clock.Now)));
    }

    [Fact]
    public void UnknownTodosDoNotTriggerSync()
    {
        using var db = new TestDatabase();
        var syncStarted = db.Clock.Now;
        db.Clock.Now = syncStarted.AddSeconds(5);
        var created = db.Todos.Create([new TodoInput("결과 불명")])[0];
        db.Execute("UPDATE todo SET push_state='unknown' WHERE todo_uid=$uid;", ("$uid", created.TodoUid));

        Assert.False(PendingChanges.ExistSince(db.Database, At(syncStarted)));
    }
}
