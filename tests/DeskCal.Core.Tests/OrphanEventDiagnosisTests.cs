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
        Assert.False(cleared.ContainsKey("colorId"));
        Assert.Equal("8", (string?)Assert.Single(http.Items.Values)["colorId"]);
        Assert.Null(db.Events.Get(local.EventUid).ColorId);
        Assert.Empty(http.InsertIds);
        Assert.Single(db.Events.GetRecords());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SwitchingTimeTypeLeavesOldEventAfter400AndSeparateCreateAddsASecondId(bool originallyAllDay)
    {
        using var db = new TestDatabase();
        var http = new CalendarHandler(allDay: originallyAllDay);
        using var service = OfflineService(http);
        var sync = new EventSync(db.Database, new GoogleCalendarRemote(service), TestDatabase.Seoul, db.Clock);
        await sync.SyncAsync(CancellationToken.None);
        var original = Assert.Single(db.Events.GetRecords());
        db.Events.Update(original.EventUid, SwitchTimeType(originallyAllDay));
        db.Clock.Now = db.Clock.Now.AddMinutes(2);
        await sync.SyncAsync(CancellationToken.None);

        var patch = Assert.Single(http.Patches);
        var obsoleteField = originallyAllDay ? "date" : "dateTime";
        var desiredField = originallyAllDay ? "dateTime" : "date";
        foreach (var field in new[] { "start", "end" })
        {
            Assert.True(patch[field]!.AsObject().ContainsKey(desiredField));
            Assert.False(patch[field]!.AsObject().ContainsKey(obsoleteField));
            Assert.NotNull(Assert.Single(http.Items.Values)[field]![obsoleteField]);
        }
        Assert.Empty(http.InsertIds);
        var local = db.Events.Get(original.EventUid);
        Assert.True(local.IsDirty);
        Assert.Equal("failed", local.PushState);
        Assert.Equal(1, db.Scalar("SELECT count(*) FROM event WHERE last_error LIKE '%Invalid start time%' AND attempt_count=1;"));
        Assert.Equal(db.Clock.Now, EventSync.LastSyncedAt(db.Database));

        // 같은 수정만 재시도하면 옛 원격 하나가 남는다. 별도 생성 호출을 하는 경우를 이어서 비교한다.
        await sync.SyncAsync(CancellationToken.None);
        Assert.Equal(2, db.Scalar("SELECT attempt_count FROM event;"));
        Assert.Empty(http.InsertIds);
        var replacement = db.Events.Create([new(local.Title, local.StartDate)
        {
            EndDate = local.EndDate, StartTime = local.StartTime, EndTime = local.EndTime
        }]).Single();
        await sync.SyncAsync(CancellationToken.None);
        Assert.Equal([replacement.EventUid], http.InsertIds);
        Assert.NotEqual(original.GoogleEventId, replacement.GoogleEventId);
        Assert.Equal(2, http.Items.Count);
        Assert.NotNull(http.Items[original.GoogleEventId!]["start"]![obsoleteField]);
        Assert.NotNull(http.Items[replacement.GoogleEventId!]["start"]![desiredField]);
        Assert.DoesNotContain(http.Patches, p => (string?)p["status"] == "cancelled");
        Assert.All(http.Items.Values, item => Assert.Equal("confirmed", (string?)item["status"]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitNullForTheOldTimeFieldsConvertsTheSameId(bool originallyAllDay)
    {
        using var db = new TestDatabase();
        var http = new CalendarHandler(originallyAllDay);
        using var service = OfflineService(http);
        var sync = new EventSync(db.Database, new GoogleCalendarRemote(service), TestDatabase.Seoul);
        await sync.SyncAsync(CancellationToken.None);
        var original = Assert.Single(db.Events.GetRecords());
        db.Events.Update(original.EventUid, SwitchTimeType(originallyAllDay));
        await sync.SyncAsync(CancellationToken.None);
        var body = Assert.Single(http.Patches).DeepClone().AsObject();
        var obsolete = originallyAllDay ? "date" : "dateTime";
        body["start"]![obsolete] = null;
        body["end"]![obsolete] = null;

        // 같은 실패 본문에서 두 키만 명시적으로 지워 같은 ID와 etag로 대조한다.
        var request = service.Events.Patch(new Event(), "primary", original.GoogleEventId);
        request.ModifyRequest = message =>
        {
            message.Headers.TryAddWithoutValidation("If-Match", (string?)http.Items[original.GoogleEventId!]["etag"]);
            message.Content?.Dispose();
            message.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        };
        var converted = await request.ExecuteAsync();
        Assert.Equal(original.GoogleEventId, converted.Id);
        Assert.Equal(originallyAllDay, converted.Start.DateTimeRaw is not null);
        Assert.Equal(!originallyAllDay, converted.Start.Date is not null);
        Assert.Single(http.Items);
        Assert.Empty(http.InsertIds);
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
        public List<string> InsertIds { get; } = [];
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
            Assert.True(request.Method == HttpMethod.Patch || request.Method == HttpMethod.Post, request.Method.ToString());
            var content = request.Content!;
            using var stream = await content.ReadAsStreamAsync(token);
            using var gzip = content.Headers.ContentEncoding.Contains("gzip")
                ? new GZipStream(stream, CompressionMode.Decompress, leaveOpen: true) : null;
            var patch = (await JsonNode.ParseAsync(gzip ?? stream, cancellationToken: token))!.AsObject();
            string id;
            JsonObject result;
            if (request.Method == HttpMethod.Post)
            {
                Assert.Equal(Collection, uri.AbsolutePath);
                id = (string)patch["id"]!;
                Assert.False(Items.ContainsKey(id));
                InsertIds.Add(id);
                result = patch;
            }
            else
            {
                Assert.Equal(Collection + "/" + RemoteId, uri.AbsolutePath);
                id = RemoteId;
                Assert.Equal((string?)Items[id]["etag"], Assert.Single(request.Headers.IfMatch).ToString());
                Patches.Add(patch);
                result = Items[id].DeepClone().AsObject();
                Merge(result, patch);
            }
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
            Items[id] = result;
            _changed.Add(id);
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
