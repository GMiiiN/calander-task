using Google.Apis.Auth.OAuth2.Responses;
using Google.Apis.Calendar.v3.Data;
using GoogleTask = Google.Apis.Tasks.v1.Data.Task;

namespace DeskCal.Core.Tests;

// S4: 412 때 마지막으로 맞춘 값·이 PC 값·Google 값을 필드 묶음별로 비교해 합치고, 겹친 것은 충돌 기록으로 확인·다시 적용한다.
public sealed class ConflictMergeTests
{
    [Fact]
    public async Task SameFieldConflictKeepsTheGoogleValueAndCanReapplyMine()
    {
        using var db = new TestDatabase();
        var remote = new EventSyncTests.FakeCalendar();
        var created = db.Events.Create([new EventInput("원래", "2026-11-26") { Memo = "메모" }]).Single();
        await Sync(db, remote);
        db.Events.Update(created.EventUid, new() { Title = "이 PC" });
        remote.Edit(created.EventUid, e => e.Summary = "휴대폰");
        await Sync(db, remote);

        Assert.Equal("휴대폰", remote.Items[created.EventUid].Summary);
        Assert.Equal(("휴대폰", "메모", false), (db.Events.Get(created.EventUid).Title, db.Events.Get(created.EventUid).Memo, db.Events.Get(created.EventUid).IsDirty));
        var conflict = Assert.Single(ConflictLog.Unseen(db.Database));
        Assert.Equal(("event", "제목 Google 값 유지", true), (conflict.Kind, conflict.Summary, conflict.CanReapply));
        Assert.Equal(("이 PC", "휴대폰"), (conflict.Fields.Single().Mine, conflict.Fields.Single().Kept));
        Assert.Equal(1, ConflictLog.UnseenCount(db.Database));

        ConflictLog.Reapply(db.Database, conflict.Id, TestDatabase.Seoul);
        Assert.Equal(0, ConflictLog.UnseenCount(db.Database));
        await Sync(db, remote);
        Assert.Equal("이 PC", remote.Items[created.EventUid].Summary);
        Assert.False(db.Events.Get(created.EventUid).IsDirty);
    }

    // 일정 시간 묶음: 시작·끝·시간대·반복은 함께 움직인다. 다른 곳은 시간을, 이 PC는 제목을 바꿨으면 둘 다 남는다.
    [Fact]
    public async Task TimeMovesAsOneGroupAndMergesWithALocalTitle()
    {
        using var db = new TestDatabase();
        var remote = new EventSyncTests.FakeCalendar();
        var created = db.Events.Create([new EventInput("회의", "2026-11-26") { StartTime = "10:00", EndTime = "11:00" }]).Single();
        await Sync(db, remote);
        db.Events.Update(created.EventUid, new() { Title = "회의(장소 변경)" });
        remote.Edit(created.EventUid, e =>
        {
            e.Start = new EventDateTime { DateTimeRaw = "2026-11-26T15:00:00+09:00", TimeZone = "Asia/Seoul" };
            e.End = new EventDateTime { DateTimeRaw = "2026-11-26T16:30:00+09:00", TimeZone = "Asia/Seoul" };
        });
        await Sync(db, remote);

        var local = db.Events.Get(created.EventUid);
        Assert.Equal(("회의(장소 변경)", "15:00", "16:30"), (local.Title, local.StartTime, local.EndTime));
        Assert.Equal("회의(장소 변경)", remote.Items[created.EventUid].Summary);
        Assert.Equal("2026-11-26T15:00:00+09:00", remote.Items[created.EventUid].Start.DateTimeRaw);
        Assert.Equal(0, ConflictLog.UnseenCount(db.Database));

        // 둘 다 시간을 다르게 바꾸면 Google 시간을 남기고 이 PC 시간은 충돌로 둔다(시작만 이 PC 값, 끝은 Google 값처럼 갈라지지 않는다).
        db.Events.Update(created.EventUid, new() { StartTime = "09:00", EndTime = "09:30" });
        remote.Edit(created.EventUid, e =>
        {
            e.Start = new EventDateTime { DateTimeRaw = "2026-11-26T18:00:00+09:00", TimeZone = "Asia/Seoul" };
            e.End = new EventDateTime { DateTimeRaw = "2026-11-26T19:00:00+09:00", TimeZone = "Asia/Seoul" };
        });
        await Sync(db, remote);
        local = db.Events.Get(created.EventUid);
        Assert.Equal(("18:00", "19:00"), (local.StartTime, local.EndTime));
        var conflict = Assert.Single(ConflictLog.Unseen(db.Database));
        Assert.Equal("시각", conflict.Fields.Single().Label);
        Assert.True(conflict.CanReapply);
        ConflictLog.Reapply(db.Database, conflict.Id, TestDatabase.Seoul);
        await Sync(db, remote);
        Assert.Equal("2026-11-26T09:00:00+09:00", remote.Items[created.EventUid].Start.DateTimeRaw);
        Assert.Equal("09:30", db.Events.Get(created.EventUid).EndTime);
    }

    // 삭제/수정 겹침: 이 PC는 지웠는데 다른 곳에서 고쳤다. 원격 우선(삭제 취소)이고 다시 지울 수 있다. 고친 것이 없으면 그냥 지운다.
    [Fact]
    public async Task LocalDeleteLosesToARemoteEditAndCanBeDeletedAgain()
    {
        using var db = new TestDatabase();
        var remote = new EventSyncTests.FakeCalendar();
        var edited = db.Events.Create([new EventInput("고친 일정", "2026-11-26")]).Single();
        var touched = db.Events.Create([new EventInput("건드리기만 한 일정", "2026-11-26")]).Single();
        await Sync(db, remote);
        db.Events.Delete(edited.EventUid);
        db.Events.Delete(touched.EventUid);
        remote.Edit(edited.EventUid, e => e.Description = "다른 곳에서 메모 추가");
        remote.Edit(touched.EventUid, _ => { }); // etag만 바뀌었다
        await Sync(db, remote);
        await Sync(db, remote);

        Assert.Null(db.Events.Get(edited.EventUid).DeletedAt);
        Assert.Equal("다른 곳에서 메모 추가", db.Events.Get(edited.EventUid).Memo);
        Assert.Equal("confirmed", remote.Items[edited.EventUid].Status);
        Assert.Equal("cancelled", remote.Items[touched.EventUid].Status);
        var conflict = Assert.Single(ConflictLog.Unseen(db.Database));
        Assert.Equal(("local", true), (conflict.Deleted, conflict.CanReapply));

        ConflictLog.Reapply(db.Database, conflict.Id, TestDatabase.Seoul);
        await Sync(db, remote);
        Assert.Equal("cancelled", remote.Items[edited.EventUid].Status);
    }

    [Fact]
    public async Task RemoteDeleteWinsOverALocalEdit()
    {
        using var db = new TestDatabase();
        var remote = new EventSyncTests.FakeCalendar();
        var created = db.Events.Create([new EventInput("지워질 일정", "2026-11-26")]).Single();
        await Sync(db, remote);
        db.Events.Update(created.EventUid, new() { Title = "이 PC에서 고침" });
        remote.Cancel(created.EventUid);
        await Sync(db, remote);
        await Sync(db, remote);

        Assert.NotNull(db.Events.Get(created.EventUid).DeletedAt);
        Assert.Equal("cancelled", remote.Items[created.EventUid].Status);
        var conflict = Assert.Single(ConflictLog.Unseen(db.Database));
        Assert.Equal(("remote", false), (conflict.Deleted, conflict.CanReapply));
        Assert.Equal(1, db.Scalar("SELECT COUNT(*) FROM sync_conflict WHERE discarded_json LIKE '%이 PC에서 고침%'"));
    }

    // 412 재발: 합친 값을 보내는 사이 또 바뀌었다. dirty로 남았다가 다음 동기화에서 새 값과 다시 합친다.
    [Fact]
    public async Task RepeatedPreconditionStaysDirtyAndMergesAgainNextTime()
    {
        using var db = new TestDatabase();
        var remote = new EventSyncTests.FakeCalendar();
        var created = db.Events.Create([new EventInput("원래", "2026-11-26")]).Single();
        await Sync(db, remote);
        db.Events.Update(created.EventUid, new() { Title = "이 PC 제목" });
        remote.Edit(created.EventUid, e => e.Description = "첫 원격 메모");
        var patches = 0;
        remote.BeforePatch = () => { if (++patches == 2) remote.Edit(created.EventUid, e => e.ColorId = "5"); };
        await Sync(db, remote);
        Assert.True(db.Events.Get(created.EventUid).IsDirty);

        remote.BeforePatch = null;
        await Sync(db, remote);
        var item = remote.Items[created.EventUid];
        Assert.Equal(("이 PC 제목", "첫 원격 메모", "5"), (item.Summary, item.Description, item.ColorId));
        var local = db.Events.Get(created.EventUid);
        Assert.Equal(("이 PC 제목", "첫 원격 메모", "5", false), (local.Title, local.Memo, local.ColorId, local.IsDirty));
        Assert.Equal(0, ConflictLog.UnseenCount(db.Database));
    }

    // 기준값이 없는 S4 이전 행은 예전처럼 원격 우선이다(다른 필드여도 이 PC 변경은 충돌 기록으로 간다).
    [Fact]
    public async Task RowsWithoutABaselineKeepTheOldRemoteFirstRule()
    {
        using var db = new TestDatabase();
        var remote = new EventSyncTests.FakeCalendar();
        var created = db.Events.Create([new EventInput("원래", "2026-11-26")]).Single();
        await Sync(db, remote);
        db.Execute("UPDATE event SET sync_base=NULL");
        db.Events.Update(created.EventUid, new() { Title = "이 PC" });
        remote.Edit(created.EventUid, e => e.Description = "원격 메모");
        await Sync(db, remote);
        await Sync(db, remote);

        Assert.Equal(("원래", "원격 메모"), (db.Events.Get(created.EventUid).Title, db.Events.Get(created.EventUid).Memo));
        var conflict = Assert.Single(ConflictLog.Unseen(db.Database));
        Assert.Equal(("Google 값으로 바뀜", false), (conflict.Summary, conflict.CanReapply));
        // 그 뒤 동기화는 기준값을 다시 채운다.
        Assert.Equal(0, db.Scalar("SELECT COUNT(*) FROM event WHERE sync_base IS NULL"));
    }

    [Fact]
    public async Task TodoFieldsMergeAndCompletionTravelsWithItsTime()
    {
        using var db = new TestDatabase();
        var remote = new TaskSyncTests.FakeTasks();
        remote.Add(new GoogleTask { Id = "t1", Title = "보고서", Status = "needsAction" });
        await TaskSync(db, remote);
        var uid = Assert.Single(db.Todos.List("all")).TodoUid;

        db.Todos.Update(uid, new TodoPatch { DueDate = "2026-11-30", IsDone = true });
        remote.Edit("t1", t => t.Notes = "폰에서 쓴 메모");
        await TaskSync(db, remote);

        var item = remote.Items.Single();
        Assert.Equal(("보고서", "폰에서 쓴 메모", "2026-11-30T00:00:00.000Z", "completed"), (item.Title, item.Notes, item.Due, item.Status));
        Assert.NotNull(item.Completed);
        var local = db.Todos.Get(uid);
        Assert.Equal(("폰에서 쓴 메모", "2026-11-30", true, false), (local.Memo, local.DueDate, local.IsDone, local.IsDirty));
        Assert.Equal(0, ConflictLog.UnseenCount(db.Database));

        // 같은 필드(제목)는 Google 값을 남기고 이 PC 값은 다시 적용할 수 있다.
        db.Todos.Update(uid, new TodoPatch { Title = "이 PC 제목" });
        remote.Edit("t1", t => t.Title = "폰 제목");
        await TaskSync(db, remote);
        var conflict = Assert.Single(ConflictLog.Unseen(db.Database));
        Assert.Equal(("todo", "제목 Google 값 유지"), (conflict.Kind, conflict.Summary));
        ConflictLog.Reapply(db.Database, conflict.Id);
        await TaskSync(db, remote);
        Assert.Equal("이 PC 제목", remote.Items.Single().Title);
    }

    [Fact]
    public async Task TodoDeleteAndEditOverlapUseRemoteFirst()
    {
        using var db = new TestDatabase();
        var remote = new TaskSyncTests.FakeTasks();
        remote.Add(new GoogleTask { Id = "keep", Title = "남을 할 일", Status = "needsAction" });
        remote.Add(new GoogleTask { Id = "gone", Title = "지워질 할 일", Status = "needsAction" });
        await TaskSync(db, remote);
        var keep = db.Todos.List("all").Single(t => t.Title == "남을 할 일").TodoUid;
        var gone = db.Todos.List("all").Single(t => t.Title == "지워질 할 일").TodoUid;

        db.Todos.Delete(keep);
        remote.Edit("keep", t => t.Title = "다른 곳에서 고침");
        db.Todos.Update(gone, new TodoPatch { Memo = "이 PC 메모" });
        remote.Items.RemoveAll(t => t.Id == "gone");
        await TaskSync(db, remote);
        await TaskSync(db, remote);

        Assert.Null(db.Todos.Get(keep).DeletedAt);
        Assert.Equal("다른 곳에서 고침", db.Todos.Get(keep).Title);
        Assert.Contains(remote.Items, t => t.Id == "keep");
        Assert.NotNull(db.Todos.Get(gone).DeletedAt);
        var conflicts = ConflictLog.Unseen(db.Database);
        Assert.Equal(new[] { "local", "remote" }, conflicts.Select(c => c.Deleted).Order());
    }

    // 지난 리뷰 사소 항목: invalid_grant가 아닌 4xx로 갱신이 실패해도 트레이가 "연결됨"으로 남았다. 이유를 남기고 성공하면 지운다.
    [Fact]
    public void AuthErrorIsReadFromTokenFailuresButNotFromTransientOnes()
    {
        Assert.Equal("invalid_client", GoogleAccount.AuthErrorOf(Token("invalid_client", 401)));
        Assert.Equal("unauthorized_client", GoogleAccount.AuthErrorOf(new AggregateException(Token("unauthorized_client", 400))));
        Assert.Null(GoogleAccount.AuthErrorOf(Token("invalid_grant", 400)));
        Assert.Null(GoogleAccount.AuthErrorOf(Token("rate_limit_exceeded", 429)));
        Assert.Null(GoogleAccount.AuthErrorOf(Token("internal_failure", 500)));
        Assert.Null(GoogleAccount.AuthErrorOf(new HttpRequestException("offline")));

        static TokenResponseException Token(string error, int status) =>
            new(new TokenErrorResponse { Error = error }, (System.Net.HttpStatusCode)status);
    }

    // 지난 리뷰 사소 항목: 전송 잠금이 "사용 중"인지 "로그인 없음"인지 구분하지 않아 영수증이 모두 sync_deferred였다.
    [Fact]
    public async Task SendOutcomeSeparatesBusyFromSignInProblems()
    {
        using var db = new TestDatabase();
        var folder = Path.Combine(Path.GetTempPath(), "deskcal-outcome-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var account = new GoogleAccount(db.Database, Path.Combine(folder, "client_secret.json"));
            var coordinator = new SyncCoordinator(db.Database, account, _ => { });
            Assert.Equal(SyncOutcome.NotSignedIn, await coordinator.RunWithOutcomeAsync(false, default));
            await new ProtectedTokenStore(db.Database).StoreAsync("user", new TokenResponse { AccessToken = "a", RefreshToken = "r" });
            Assert.Equal(SyncOutcome.NoClientFile, await coordinator.RunWithOutcomeAsync(false, default));

            using var held = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var owner = SyncCoordinator.WithSenderAsync(db.Database, () => Task.Run(() => { held.Set(); release.Wait(); return true; }), default);
            held.Wait();
            Assert.Equal(SyncOutcome.Busy, await coordinator.RunWithOutcomeAsync(false, default));
            release.Set();
            await owner;
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    // 공유 설정(S6) 충돌도 같은 화면에서 확인하고 이 PC 값을 다시 적용한다.
    [Fact]
    public async Task SettingsConflictCanBeReappliedAndShared()
    {
        using var a = new TestDatabase();
        using var b = new TestDatabase();
        var drive = new SharedSettingsTests.FakeAppData();
        new CategoryRepository(a.Database).Rename("1", "처음");
        await Share(a, drive);
        await Share(b, drive);
        new CategoryRepository(b.Database).Rename("1", "B안");
        new CategoryRepository(a.Database).Rename("1", "A안");
        await Share(a, drive);
        await Share(b, drive);

        var conflict = Assert.Single(ConflictLog.Unseen(b.Database));
        Assert.Equal(("settings", "카테고리 이름 ‘B안’", true), (conflict.Kind, conflict.Title, conflict.CanReapply));
        Assert.Equal(("B안", "A안"), (conflict.Fields.Single().Mine, conflict.Fields.Single().Kept));
        ConflictLog.Reapply(b.Database, conflict.Id);
        await Share(b, drive);
        await Share(a, drive);
        Assert.Equal("B안", new CategoryRepository(a.Database).List().Single(c => c.ColorId == "1").Name);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task MergedOccurrenceKeepsTheResponseId(bool sendMerge, bool editDuringMerge)
    {
        using var db = new TestDatabase();
        var remote = new EventSyncTests.FakeCalendar();
        var day = new DateOnly(2026, 11, 15);
        db.Events.Create([new EventInput("시리즈", "2026-11-08") { Repeat = new RecurrenceInput("weekly", Weekdays: [DayOfWeek.Sunday]) }]);
        await Sync(db, remote);
        var occurrence = db.Events.List(day, day).Single().OccurrenceId!;
        db.Events.AddChecklistItems(occurrence, true, ["처음"]);
        await Sync(db, remote);
        var id = remote.Items.Keys.Single(key => key.Contains('_'));
        db.Execute("UPDATE event SET google_event_id=NULL WHERE parent_event_id IS NOT NULL");
        db.Events.SetOccurrenceDone(occurrence, true);
        remote.Edit(id, e =>
        {
            e.Description = "원격 메모";
            if (!sendMerge) e.ExtendedProperties.Private__["deskcalDone"] = "1";
        });
        var attempts = 0;
        remote.BeforePatch = () =>
        {
            if (++attempts == 2 && editDuringMerge)
                db.Events.AddChecklistItems(occurrence, true, ["전송 중 편집"]);
        };

        await Sync(db, remote);

        var saved = db.Events.GetRecords().Single(e => e.ParentEventId is not null);
        Assert.Equal(id, saved.GoogleEventId);
        Assert.Equal(editDuringMerge, saved.IsDirty);
        Assert.Equal(sendMerge ? 2 : 1, attempts);
    }

    private static Task Sync(TestDatabase db, EventSyncTests.FakeCalendar remote) =>
        new EventSync(db.Database, remote, TestDatabase.Seoul, db.Clock).SyncAsync(CancellationToken.None);

    private static Task TaskSync(TestDatabase db, TaskSyncTests.FakeTasks remote) =>
        new TaskSync(db.Database, remote, db.Clock).SyncAsync(CancellationToken.None);

    private static Task Share(TestDatabase db, SharedSettingsTests.FakeAppData drive) =>
        new SharedSettingsSync(db.Database, drive, db.Clock).SyncAsync(CancellationToken.None);
}
