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

    [Theory]
    [InlineData(true, "synced", null)]
    [InlineData(false, "synced", null)]
    [InlineData(true, "pending", null)]
    [InlineData(false, "pending", "not_signed_in")]
    [InlineData(true, "failed", "push_failed")]
    [InlineData(false, "failed", "not_signed_in")]
    [InlineData(true, "unknown", "needs_review")]
    [InlineData(false, "unknown", "needs_review")]
    public void DetailsUseTheStoredRowAndAccountState(bool signedIn, string state, string? reason)
    {
        using var db = new TestDatabase();
        if (signedIn) db.Execute("INSERT INTO setting VALUES ($key,'test');", ("$key", ProtectedTokenStore.SettingKey));
        db.Todos.Create([new("과제")]);
        db.Execute("UPDATE todo SET push_state=$state", ("$state", state));
        var rows = PendingChanges.List(db.Database);
        if (state == "synced") Assert.Empty(rows);
        else Assert.Equal(reason, Assert.Single(rows).Reason);
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

    // 리뷰 A-7: HasUnsent가 결과 불명(unknown) 할 일까지 세어, 앱이 꺼져 있으면 MCP마다 2분마다 전체 동기화를 끝없이 되풀이했다.
    [Fact]
    public void UnknownTodosAloneAreNotUnsent()
    {
        using var db = new TestDatabase();
        Assert.False(PendingChanges.HasUnsent(db.Database));
        var unknown = db.Todos.Create([new TodoInput("결과 불명")])[0];
        db.Execute("UPDATE todo SET push_state='unknown' WHERE todo_uid=$uid;", ("$uid", unknown.TodoUid));
        db.Todos.Update(unknown.TodoUid, new() { Title = "결과 불명인 채로 고침" }); // 고쳐도 unknown이고 push는 보내지 않는다
        Assert.False(PendingChanges.HasUnsent(db.Database));
        Assert.Equal("needs_review", Assert.Single(PendingChanges.List(db.Database)).Reason); // 상태 상세에는 계속 보인다

        var pending = db.Todos.Create([new TodoInput("보낼 할 일")])[0];
        Assert.True(PendingChanges.HasUnsent(db.Database));
        db.Execute("UPDATE todo SET is_dirty=0, push_state='synced' WHERE todo_uid=$uid;", ("$uid", pending.TodoUid));
        db.Execute("UPDATE todo SET push_state='failed' WHERE todo_uid=$uid;", ("$uid", unknown.TodoUid));
        Assert.True(PendingChanges.HasUnsent(db.Database)); // failed는 다시 보낸다
        db.Execute("UPDATE todo SET push_state='unknown' WHERE todo_uid=$uid;", ("$uid", unknown.TodoUid));
        db.Events.Create([new EventInput("보낼 일정", "2026-11-12")]);
        Assert.True(PendingChanges.HasUnsent(db.Database));
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
