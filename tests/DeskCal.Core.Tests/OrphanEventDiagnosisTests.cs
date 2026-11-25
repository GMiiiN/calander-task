using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Google.Apis.Calendar.v3;
using Google.Apis.Calendar.v3.Data;
using Google.Apis.Http;
using Google.Apis.Services;
using FakeCalendar = DeskCal.Core.Tests.EventSyncTests.FakeCalendar;

namespace DeskCal.Core.Tests;

public sealed class OrphanEventDiagnosisTests
{
    private static readonly DateOnly Monday = new(2026, 11, 9);

    [Fact]
    public async Task EditingPulledEventSendsEachPatchToTheOriginalGoogleId()
    {
        using var db = new TestDatabase();
        var http = new CalendarHandler(allDay: false);
        using var service = OfflineService(http);
        var sync = new EventSync(db.Database, new GoogleCalendarRemote(service), TestDatabase.Seoul);
        await sync.SyncAsync(CancellationToken.None);
        var local = Assert.Single(db.Events.GetRecords());
        Assert.NotEqual(local.EventUid, local.GoogleEventId);

        async Task<JsonObject> Edit(EventPatch patch)
        {
            var previousCount = http.Patches.Count;
            db.Events.Update(local.EventUid, patch);
            await sync.SyncAsync(CancellationToken.None);
            Assert.Equal(previousCount + 1, http.Patches.Count);
            Assert.Equal(local.GoogleEventId, db.Events.Get(local.EventUid).GoogleEventId);
            Assert.False(db.Events.Get(local.EventUid).IsDirty);
            return http.Patches[^1];
        }

        Assert.Equal("새 제목", (string?)(await Edit(new() { Title = "새 제목" }))["summary"]);
        var later = await Edit(new() { StartTime = "11:00", EndTime = "12:00" });
        Assert.Equal("2026-11-09T11:00:00+09:00", (string?)later["start"]!["dateTime"]);
        var moved = await Edit(new() { StartDate = "2026-11-10", EndDate = "2026-11-10" });
        Assert.Equal("2026-11-10T11:00:00+09:00", (string?)moved["start"]!["dateTime"]);
        Assert.Equal("8", (string?)(await Edit(new() { ColorId = "8" }))["colorId"]);
        var cleared = await Edit(new() { ColorId = new(true, null) });
        Assert.True(cleared.ContainsKey("colorId"));
        Assert.Null(cleared["colorId"]);
        Assert.False(Assert.Single(http.Items.Values).ContainsKey("colorId"));
        Assert.Null(db.Events.Get(local.EventUid).ColorId);
        Assert.Single(db.Events.GetRecords());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task SwitchingTimeTypeSyncsTheOriginalIdInOnePatch(bool originallyAllDay, bool previouslyFailed)
    {
        using var db = new TestDatabase();
        var http = new CalendarHandler(allDay: originallyAllDay);
        using var service = OfflineService(http);
        var sync = new EventSync(db.Database, new GoogleCalendarRemote(service), TestDatabase.Seoul, db.Clock);
        await sync.SyncAsync(CancellationToken.None);
        var original = Assert.Single(db.Events.GetRecords());
        db.Events.Update(original.EventUid, SwitchTimeType(originallyAllDay));
        if (previouslyFailed)
        {
            // 구버전이 400을 받고 종료한 DB 상태에서, 사용자 재편집 없이 다시 시작한다.
            db.Execute("UPDATE event SET push_state='failed', last_error='Invalid start time.', attempt_count=3 WHERE event_id=$id;",
                ("$id", original.EventId));
            Assert.True(db.Events.Get(original.EventUid).IsDirty);
            Assert.Equal("failed", db.Events.Get(original.EventUid).PushState);
            sync = new EventSync(new Database(db.Path), new GoogleCalendarRemote(service), TestDatabase.Seoul, db.Clock);
        }

        await sync.SyncAsync(CancellationToken.None);
        var patch = Assert.Single(http.Patches);
        var (id, item) = Assert.Single(http.Items);
        Assert.Equal(original.GoogleEventId, id);
        Assert.Equal("confirmed", (string?)item["status"]);
        var expectedStart = originallyAllDay ? "2026-11-09T11:00:00+09:00" : "2026-11-09";
        var expectedEnd = originallyAllDay ? "2026-11-09T12:00:00+09:00" : "2026-11-10";
        foreach (var (field, expected) in new[] { ("start", expectedStart), ("end", expectedEnd) })
        {
            var time = patch[field]!.AsObject();
            var desired = originallyAllDay ? "dateTime" : "date";
            var obsolete = originallyAllDay ? "date" : "dateTime";
            Assert.Equal(expected, (string?)time[desired]);
            Assert.True(time.ContainsKey(obsolete));
            Assert.Null(time[obsolete]);
            Assert.True(time.ContainsKey("timeZone"));
            Assert.Equal(originallyAllDay ? "Asia/Seoul" : null, (string?)time["timeZone"]);
            Assert.Equal(expected, (string?)item[field]![desired]);
            Assert.False(item[field]!.AsObject().ContainsKey(obsolete));
        }
        var local = Assert.Single(db.Events.GetRecords());
        Assert.Equal(original.EventUid, local.EventUid);
        Assert.Equal(id, local.GoogleEventId);
        Assert.False(local.IsDirty);
        Assert.Equal("synced", local.PushState);
        Assert.Equal(1, db.Scalar("SELECT count(*) FROM event WHERE last_error IS NULL AND attempt_count=0;"));
    }

    [Fact]
    public async Task ChecklistOnlyPatchLeavesRemoteTimeAndColorIntact()
    {
        var http = new CalendarHandler(allDay: false);
        var (id, item) = Assert.Single(http.Items);
        item["colorId"] = "8";
        using var service = OfflineService(http);
        var properties = new Dictionary<string, string>();
        Checklist.WriteProperties(Checklist.Add([], ["예약 확인"]), properties);
        var changes = new Event { ExtendedProperties = new() { Private__ = properties } };
        await new GoogleCalendarRemote(service).PatchAsync(id, changes, (string?)item["etag"], CancellationToken.None);

        var patch = Assert.Single(http.Patches);
        Assert.Equal("extendedProperties", Assert.Single(patch).Key);
        var updated = http.Items[id];
        Assert.Equal("8", (string?)updated["colorId"]);
        Assert.True(JsonNode.DeepEquals(item["start"], updated["start"]));
        Assert.True(JsonNode.DeepEquals(item["end"], updated["end"]));
    }

    private static EventPatch SwitchTimeType(bool originallyAllDay) => originallyAllDay
        ? new() { StartTime = "11:00", EndTime = "12:00" }
        : new() { StartTime = new(true, null), EndTime = new(true, null) };

    [Fact]
    public async Task EarlierStartDateAloneIncludesTheOldDayOnTheSameEvent()
    {
        using var db = new TestDatabase();
        var remote = new FakeCalendar();
        var original = db.Events.Create([new("원래 일정", "2026-11-09")]).Single();
        await Sync(db, remote);

        db.Events.Update(original.EventUid, new() { StartDate = "2026-11-08" });
        await Sync(db, remote);

        var item = Assert.Single(remote.Items.Values);
        Assert.Equal(original.GoogleEventId, item.Id);
        Assert.Equal("2026-11-08", item.Start.Date);
        Assert.Equal("2026-11-10", item.End.Date); // Google 종료일은 배타적이다.
        Assert.Equal(original.EventId, Assert.Single(db.Events.List(Monday, Monday)).Event.EventId);
        Assert.Equal([original.EventUid], remote.InsertAttempts);
        Assert.Equal(original.GoogleEventId, Assert.Single(remote.PatchAttempts).Id);
        Assert.Empty(remote.DeleteAttempts);
        Assert.Throws<ArgumentException>(() => db.Events.Update(original.EventUid, new() { StartDate = "2026-11-11" }));
    }

    [Fact]
    public async Task LostInsertResponseRetriesTheSameIdAndEventuallyPatchesIt()
    {
        using var db = new TestDatabase();
        var remote = new FakeCalendar { LoseInsertResponse = true };
        var original = db.Events.Create([new("옛 제목", "2026-11-09")]).Single();
        await Assert.ThrowsAsync<HttpRequestException>(() => Sync(db, remote));
        db.Events.Update(original.EventUid, new() { Title = "새 제목" });
        remote.LoseInsertResponse = false;

        await Sync(db, remote); // 409 후 기존 ID와 etag만 연결한다.
        Assert.Equal("옛 제목", Assert.Single(remote.Items.Values).Summary);
        Assert.True(db.Events.Get(original.EventUid).IsDirty);
        await Sync(db, remote);

        Assert.Equal([original.EventUid, original.EventUid], remote.InsertAttempts);
        Assert.Equal(original.GoogleEventId, Assert.Single(remote.PatchAttempts).Id);
        Assert.Empty(remote.DeleteAttempts);
        Assert.Equal("새 제목", Assert.Single(remote.Items.Values).Summary);
    }

    [Fact]
    public async Task PatchConflictKeepsOneRemoteEventAndRecordsTheDiscardedEdit()
    {
        using var db = new TestDatabase();
        var remote = new FakeCalendar();
        var original = db.Events.Create([new("옛 제목", "2026-11-09")]).Single();
        await Sync(db, remote);
        db.Events.Update(original.EventUid, new() { Title = "새 제목" });
        remote.Edit(original.GoogleEventId!, e => e.Description = "다른 기기에서 메모 수정");
        await Sync(db, remote);

        Assert.Equal("옛 제목", Assert.Single(remote.Items.Values).Summary);
        Assert.Equal("옛 제목", db.Events.Get(original.EventUid).Title);
        Assert.False(db.Events.Get(original.EventUid).IsDirty);
        Assert.Equal(1, db.Scalar("SELECT count(*) FROM sync_conflict WHERE item_kind='event' AND is_seen=0;"));
        Assert.Equal([original.EventUid], remote.InsertAttempts);
        Assert.Equal(original.GoogleEventId, Assert.Single(remote.PatchAttempts).Id);
        Assert.Empty(remote.DeleteAttempts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SeparateCreatePreservesOldRemoteWhenDeleteIsOmittedOrConflicts(bool deleteConflicts)
    {
        using var db = new TestDatabase();
        var remote = new FakeCalendar();
        var original = db.Events.Create([new("옛 일정", "2026-11-09")]).Single();
        await Sync(db, remote);
        if (deleteConflicts)
        {
            db.Events.Delete(original.EventUid);
            remote.Edit(original.GoogleEventId!, e => e.Description = "다른 기기에서 메모 수정");
        }
        var replacement = db.Events.Create([new("바꾼 일정", "2026-11-10")]).Single();
        await Sync(db, remote);
        await Sync(db, remote);

        Assert.NotEqual(original.GoogleEventId, replacement.GoogleEventId);
        Assert.Equal([original.EventUid, replacement.EventUid], remote.InsertAttempts);
        if (deleteConflicts) Assert.Equal([original.EventUid], remote.DeleteAttempts);
        else Assert.Empty(remote.DeleteAttempts);
        Assert.Empty(remote.PatchAttempts);
        Assert.Equal(2, remote.Items.Count);
        Assert.All(remote.Items.Values, e => Assert.Equal("confirmed", e.Status));
        Assert.Equal("옛 일정", remote.Items[original.GoogleEventId!].Summary);
        Assert.Equal("바꾼 일정", remote.Items[replacement.GoogleEventId!].Summary);
        Assert.Null(db.Events.Get(original.EventUid).DeletedAt);
        Assert.Equal(deleteConflicts ? 1 : 0, db.Scalar("SELECT count(*) FROM sync_conflict WHERE item_kind='event';"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MovingSeriesStartLeavesOldChecklistSlotUntilItsCancellationCanBeMatched(bool uploadExceptionFirst)
    {
        using var db = new TestDatabase();
        var remote = new FakeCalendar();
        var series = db.Events.Create([new("반복 일정", "2026-11-02")
        {
            StartTime = "09:00", EndTime = "10:00", Repeat = new("weekly", Weekdays: [DayOfWeek.Monday])
        }]).Single();
        await Sync(db, remote);
        var occurrence = Assert.Single(db.Events.List(Monday, Monday));
        db.Events.AddChecklistItems(occurrence.OccurrenceId!, true, ["준비"]);
        var oldId = series.GoogleEventId + "_20261109T000000Z";
        if (uploadExceptionFirst) await Sync(db, remote);
        else remote.MissingInstances.Add(oldId); // 본체 시간 변경 뒤 옛 인스턴스 GET이 404인 응답을 모사한다.

        db.Events.Update(series.EventUid, new() { StartTime = "11:00", EndTime = "12:00" });
        var slots = db.Events.List(Monday, Monday);
        Assert.Equal([9, 11], slots.Select(o => o.Start.Hour));
        Assert.Single(slots[0].Event.Checklist);
        Assert.Empty(slots[1].Event.Checklist);
        await Sync(db, remote);
        Assert.Equal([series.EventUid], remote.InsertAttempts);
        Assert.Contains(remote.PatchAttempts, call => call.Id == series.GoogleEventId);
        Assert.Empty(remote.DeleteAttempts);

        if (uploadExceptionFirst)
        {
            // 설계 문서의 옛 인스턴스 tombstone 예시처럼 ID와 취소 상태만 보낸다.
            remote.Put(new Event { Id = oldId, Status = "cancelled" });
            await Sync(db, remote);
            Assert.Equal(11, Assert.Single(db.Events.List(Monday, Monday)).Start.Hour);
        }
        else
        {
            // 404로 전체를 받은 뒤에도 원격 ID 없는 옛 예외가 로컬에 남는다.
            await Sync(db, remote);
            Assert.Single(remote.Items);
            Assert.Equal([9, 11], db.Events.List(Monday, Monday).Select(o => o.Start.Hour));
            var exception = Assert.Single(db.Events.GetRecords(), e => e.ParentEventId == series.EventId);
            Assert.Null(exception.GoogleEventId);
            Assert.False(exception.IsDirty);
            Assert.Equal(1, db.Scalar("SELECT count(*) FROM sync_conflict WHERE item_kind='event';"));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PendingWriteSurvivesReopeningAndTargetsTheSameRemoteEvent(bool delete)
    {
        using var db = new TestDatabase();
        var remote = new FakeCalendar();
        var original = db.Events.Create([new("옛 제목", "2026-11-09")]).Single();
        await Sync(db, remote);

        var writer = new EventRepository(new Database(db.Path), TestDatabase.Seoul);
        var pending = delete ? writer.Delete(original.EventUid) : writer.Update(original.EventUid, new() { Title = "새 제목" });
        Assert.True(pending.IsDirty);
        Assert.Equal("pending", pending.PushState);
        Assert.Equal("옛 제목", Assert.Single(remote.Items.Values).Summary);
        Assert.Empty(remote.PatchAttempts);
        Assert.Empty(remote.DeleteAttempts);

        var restarted = new EventSync(new Database(db.Path), remote, TestDatabase.Seoul);
        await restarted.SyncAsync(CancellationToken.None);
        var item = Assert.Single(remote.Items.Values);
        Assert.Equal(original.GoogleEventId, item.Id);
        Assert.Equal([original.EventUid], remote.InsertAttempts);
        Assert.False(writer.Get(original.EventUid).IsDirty);
        if (delete)
        {
            Assert.Equal("cancelled", item.Status);
            Assert.Equal([original.EventUid], remote.DeleteAttempts);
            Assert.Empty(remote.PatchAttempts);
        }
        else
        {
            Assert.Equal("새 제목", item.Summary);
            Assert.Equal(original.EventUid, Assert.Single(remote.PatchAttempts).Id);
            Assert.Empty(remote.DeleteAttempts);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingFromFullListHidesLiveRemoteAndRecreationLeavesTwoIds(bool afterTokenExpiry)
    {
        using var db = new TestDatabase();
        var remote = new FakeCalendar();
        var original = db.Events.Create([new("원래 일정", "2026-11-09")]).Single();
        if (afterTokenExpiry)
        {
            await Sync(db, remote);
            db.Events.Update(original.EventUid, new() { Memo = "메모 수정" });
            remote.ExpireTokens = true;
        }
        // 가정은 한 가지다. 성공한 쓰기 직후 전체 목록에서만 해당 ID가 빠지고, GET으로는 살아 있다.
        remote.OmitFromNextFullList = original.GoogleEventId;
        await Sync(db, remote);

        var hidden = db.Events.Get(original.EventUid);
        Assert.NotNull(hidden.DeletedAt);
        Assert.False(hidden.IsDirty);
        Assert.Empty(db.Events.List(Monday, Monday));
        Assert.Equal("confirmed", (await remote.GetAsync(original.GoogleEventId!, CancellationToken.None)).Status);

        var replacement = db.Events.Create([new("바꾼 일정", "2026-11-10")]).Single();
        await Sync(db, remote);
        Assert.Equal([original.EventUid, replacement.EventUid], remote.InsertAttempts);
        Assert.Empty(remote.DeleteAttempts);
        Assert.DoesNotContain(remote.PatchAttempts, call => call.Status == "cancelled");
        Assert.Equal(2, remote.Items.Count);
        Assert.All(remote.Items.Values, e => Assert.Equal("confirmed", e.Status));
        Assert.NotNull(db.Events.Get(original.EventUid).DeletedAt);
    }

    private static Task Sync(TestDatabase db, FakeCalendar remote) =>
        new EventSync(db.Database, remote, TestDatabase.Seoul).SyncAsync(CancellationToken.None);

    private static CalendarService OfflineService(CalendarHandler handler) => new(new BaseClientService.Initializer
    {
        HttpClientFactory = new HttpClientFromMessageHandlerFactory(_ => new(handler, false, false))
    });

    // Calendar PATCH의 하위 필드 병합과 명시적 null 삭제를 모사한다. HTTP 전송은 이 핸들러에서 끝난다.
    private sealed class CalendarHandler : HttpMessageHandler
    {
        private const string Collection = "/calendar/v3/calendars/primary/events";
        private const string RemoteId = "abc123";
        private readonly HashSet<string> _changed = [RemoteId];
        private int _version;
        public Dictionary<string, JsonObject> Items { get; } = [];
        public List<JsonObject> Patches { get; } = [];

        public CalendarHandler(bool allDay)
        {
            Items.Add(RemoteId, new JsonObject
            {
                ["id"] = RemoteId, ["etag"] = "\"e0\"", ["summary"] = "옛 제목", ["status"] = "confirmed",
                ["start"] = allDay ? new JsonObject { ["date"] = "2026-11-09" }
                    : new JsonObject { ["dateTime"] = "2026-11-09T09:00:00+09:00", ["timeZone"] = "Asia/Seoul" },
                ["end"] = allDay ? new JsonObject { ["date"] = "2026-11-10" }
                    : new JsonObject { ["dateTime"] = "2026-11-09T10:00:00+09:00", ["timeZone"] = "Asia/Seoul" }
            });
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var uri = request.RequestUri!;
            if (request.Method == HttpMethod.Get)
            {
                Assert.Equal(Collection, uri.AbsolutePath);
                IEnumerable<string> ids = uri.Query.Contains("syncToken=") ? _changed : Items.Keys;
                var items = new JsonArray(ids.Select(listId => Items[listId].DeepClone()).ToArray());
                _changed.Clear();
                return Reply(HttpStatusCode.OK, new JsonObject { ["items"] = items, ["nextSyncToken"] = "cursor" });
            }
            // 편집은 기존 리소스에만 PATCH한다. POST와 DELETE를 포함한 다른 쓰기는 테스트 실패다.
            Assert.Equal(HttpMethod.Patch, request.Method);
            Assert.Equal(Collection + "/" + RemoteId, uri.AbsolutePath);
            Assert.Equal((string?)Items[RemoteId]["etag"], Assert.Single(request.Headers.IfMatch).ToString());
            var content = request.Content!;
            using var stream = await content.ReadAsStreamAsync(token);
            using var gzip = content.Headers.ContentEncoding.Contains("gzip")
                ? new GZipStream(stream, CompressionMode.Decompress, leaveOpen: true) : null;
            var patch = (await JsonNode.ParseAsync(gzip ?? stream, cancellationToken: token))!.AsObject();
            Patches.Add(patch);
            var result = Items[RemoteId].DeepClone().AsObject();
            Merge(result, patch);
            foreach (var field in new[] { "start", "end" })
            {
                var time = result[field]!;
                if ((time["date"] is null) == (time["dateTime"] is null))
                    return Reply(HttpStatusCode.BadRequest, new JsonObject
                    {
                        ["error"] = new JsonObject { ["code"] = 400, ["message"] = $"Invalid {field} time." }
                    });
            }
            result["etag"] = $"\"e{++_version}\"";
            Items[RemoteId] = result;
            _changed.Add(RemoteId);
            return Reply(HttpStatusCode.OK, result);
        }

        private static void Merge(JsonObject target, JsonObject patch)
        {
            foreach (var (name, value) in patch)
            {
                if (value is null) target.Remove(name);
                else if (value is JsonObject child && target[name] is JsonObject previous) Merge(previous, child);
                else target[name] = value.DeepClone();
            }
        }

        private static HttpResponseMessage Reply(HttpStatusCode status, JsonObject body) => new(status)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
        };
    }
}
