using System.Globalization;
using System.Net;
using Google;
using Google.Apis.Calendar.v3.Data;

namespace DeskCal.Core.Tests;

public sealed class EventSyncTests
{
    private static readonly TimeZoneInfo Seoul = TimeZoneInfo.FindSystemTimeZoneById("Korea Standard Time");

    [Fact]
    public async Task PushesNewEventWithPresetIdOnceAndKeepsItClean()
    {
        using var db = new TestDatabase();
        var events = new EventRepository(db.Database, Seoul);
        var created = events.Create([new EventInput("팀 회의", "2026-11-02") { StartTime = "14:00", EndTime = "15:00" }])[0];
        var remote = new FakeCalendar();

        await Sync(db, remote);
        await Sync(db, remote);

        var item = Assert.Single(remote.Items.Values);
        Assert.Equal(created.EventUid, item.Id);
        Assert.Equal("팀 회의", item.Summary);
        Assert.Equal("2026-11-02T14:00:00+09:00", item.Start.DateTimeRaw);
        Assert.Equal(created.EventUid, item.ExtendedProperties.Private__["deskcalUid"]);
        var local = events.Get(created.EventUid);
        Assert.False(local.IsDirty);
        Assert.Single(events.GetRecords());
    }

    [Fact]
    public async Task PullsPhoneEventAndAppliesRemoteDeletion()
    {
        using var db = new TestDatabase();
        var remote = new FakeCalendar();
        remote.Put(new Event { Id = "phone1", Summary = "치과", Start = new EventDateTime { DateTimeRaw = "2026-11-03T10:00:00+09:00" },
            End = new EventDateTime { DateTimeRaw = "2026-11-03T11:00:00+09:00" } });
        var events = new EventRepository(db.Database, Seoul);

        await Sync(db, remote);
        var pulled = Assert.Single(events.List(new DateOnly(2026, 11, 3), new DateOnly(2026, 11, 3)));
        Assert.Equal("치과", pulled.Event.Title);
        Assert.Equal(new DateTime(2026, 11, 3, 10, 0, 0), pulled.Start);
        Assert.Equal("google", pulled.Event.CreatedBy);

        remote.Cancel("phone1");
        await Sync(db, remote);
        Assert.Empty(events.List(new DateOnly(2026, 11, 3), new DateOnly(2026, 11, 3)));
    }

    [Fact]
    public async Task EventFromOtherDeskCalKeepsItsUid()
    {
        using var db = new TestDatabase();
        var remote = new FakeCalendar();
        var uid = Guid.NewGuid().ToString("N");
        remote.Put(new Event { Id = uid, Summary = "다른 PC", Start = new EventDateTime { Date = "2026-11-05" }, End = new EventDateTime { Date = "2026-11-06" },
            ExtendedProperties = new Event.ExtendedPropertiesData { Private__ = new Dictionary<string, string> { ["deskcalUid"] = uid, ["deskcalDone"] = "1" } } });

        await Sync(db, remote);

        var local = new EventRepository(db.Database, Seoul).Get(uid);
        Assert.True(local.IsDone);
        Assert.Null(local.StartTime);
        Assert.Equal("2026-11-05", local.EndDate);
    }

    [Fact]
    public async Task RemoteWinsOnConflict()
    {
        using var db = new TestDatabase();
        var events = new EventRepository(db.Database, Seoul);
        var created = events.Create([new EventInput("원래", "2026-11-02")])[0];
        var remote = new FakeCalendar();
        await Sync(db, remote);

        events.Update(created.EventUid, new EventPatch { Title = "이 PC" });
        remote.Edit(created.EventUid, e => e.Summary = "휴대폰");
        await Sync(db, remote);
        await Sync(db, remote);

        Assert.Equal("휴대폰", events.Get(created.EventUid).Title);
        Assert.Equal("휴대폰", remote.Items[created.EventUid].Summary);
    }

    [Fact]
    public async Task DeletedOccurrenceCancelsGoogleInstance()
    {
        using var db = new TestDatabase();
        var events = new EventRepository(db.Database, Seoul);
        var series = events.Create([new EventInput("운동", "2026-11-02") { StartTime = "09:00", EndTime = "10:00",
            Repeat = new RecurrenceInput("weekly", Weekdays: [DayOfWeek.Monday]) }])[0];
        var remote = new FakeCalendar();
        await Sync(db, remote);

        var occurrence = events.List(new DateOnly(2026, 11, 9), new DateOnly(2026, 11, 9)).Single();
        events.DeleteOccurrence(occurrence.OccurrenceId!);
        await Sync(db, remote);

        var instance = remote.Items[series.EventUid + "_20261109T000000Z"];
        Assert.Equal("cancelled", instance.Status);
        Assert.Empty(events.List(new DateOnly(2026, 11, 9), new DateOnly(2026, 11, 9)));
        Assert.All(events.GetRecords(), r => Assert.False(r.IsDirty));
    }

    [Fact]
    public async Task ExpiredTokenRefetchesAllAndRemovesMissingEvents()
    {
        using var db = new TestDatabase();
        var events = new EventRepository(db.Database, Seoul);
        var remote = new FakeCalendar();
        remote.Put(new Event { Id = "a1", Summary = "남을 것", Start = new EventDateTime { Date = "2026-11-10" }, End = new EventDateTime { Date = "2026-11-11" } });
        remote.Put(new Event { Id = "b2", Summary = "사라질 것", Start = new EventDateTime { Date = "2026-11-12" }, End = new EventDateTime { Date = "2026-11-13" } });
        await Sync(db, remote);

        remote.Items.Remove("b2");
        remote.ExpireTokens = true;
        await Sync(db, remote);

        Assert.Equal(["남을 것"], events.List(new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 30)).Select(o => o.Event.Title));
    }

    [Fact]
    public async Task OccurrenceDoneDoesNotOverwriteTimeMovedElsewhere()
    {
        using var db = new TestDatabase();
        var events = new EventRepository(db.Database, Seoul);
        var series = events.Create([new EventInput("운동", "2026-11-02") { StartTime = "09:00", EndTime = "10:00",
            Repeat = new RecurrenceInput("weekly", Weekdays: [DayOfWeek.Monday]) }])[0];
        var remote = new FakeCalendar();
        await Sync(db, remote);

        // 휴대폰에서 11월 9일 회차를 11시로 옮겼고, 이 PC는 아직 받지 않은 채 완료 표시를 했다.
        var instanceId = series.EventUid + "_20261109T000000Z";
        remote.Put(new Event { Id = instanceId, RecurringEventId = series.EventUid, Summary = "운동",
            OriginalStartTime = new EventDateTime { DateTimeRaw = "2026-11-09T00:00:00Z" },
            Start = new EventDateTime { DateTimeRaw = "2026-11-09T11:00:00+09:00" }, End = new EventDateTime { DateTimeRaw = "2026-11-09T12:00:00+09:00" } });
        var occurrence = events.List(new DateOnly(2026, 11, 9), new DateOnly(2026, 11, 9)).Single();
        events.SetOccurrenceDone(occurrence.OccurrenceId!, true);
        await Sync(db, remote);

        Assert.Equal("2026-11-09T11:00:00+09:00", remote.Items[instanceId].Start.DateTimeRaw);
        Assert.Equal(new DateTime(2026, 11, 9, 11, 0, 0), events.List(new DateOnly(2026, 11, 9), new DateOnly(2026, 11, 9)).Single().Start);
    }

    [Fact]
    public async Task DeletingEventWhoseCreateResponseWasLostDeletesItOnGoogle()
    {
        using var db = new TestDatabase();
        var events = new EventRepository(db.Database, Seoul);
        var created = events.Create([new EventInput("응답이 사라진 일정", "2026-11-04")])[0];
        var remote = new FakeCalendar { LoseInsertResponse = true };
        await Assert.ThrowsAsync<HttpRequestException>(() => Sync(db, remote));
        remote.LoseInsertResponse = false;

        events.Delete(created.EventUid);
        await Sync(db, remote);
        await Sync(db, remote);

        Assert.Equal("cancelled", remote.Items[created.EventUid].Status);
        Assert.Empty(events.List(new DateOnly(2026, 11, 4), new DateOnly(2026, 11, 4)));
    }

    [Fact]
    public async Task EventRemovedFromGoogleDisappearsAfterNotFound()
    {
        using var db = new TestDatabase();
        var events = new EventRepository(db.Database, Seoul);
        var remote = new FakeCalendar();
        remote.Put(new Event { Id = "gone1", Summary = "없어질 일정", Start = new EventDateTime { Date = "2026-11-06" }, End = new EventDateTime { Date = "2026-11-07" } });
        await Sync(db, remote);

        var local = events.List(new DateOnly(2026, 11, 6), new DateOnly(2026, 11, 6)).Single().Event;
        events.Update(local.EventUid, new EventPatch { Title = "이 PC에서 고침" });
        remote.Items.Remove("gone1");
        await Sync(db, remote);

        Assert.Empty(events.List(new DateOnly(2026, 11, 6), new DateOnly(2026, 11, 6)));
    }

    private static Task Sync(TestDatabase db, FakeCalendar remote) => new EventSync(db.Database, remote, Seoul).SyncAsync(CancellationToken.None);

    [Fact]
    public async Task CompletingAndReopeningPushesOnlyTheEventAndSelectedInstance()
    {
        using var db = new TestDatabase();
        var (events, occurrenceId, series) = WeeklySeries(db);
        var single = events.Create([new EventInput("단일 일정", "2026-11-15")])[0];
        var remote = new FakeCalendar();
        await Sync(db, remote);

        var instanceId = series.EventUid + "_20261115T063000Z";
        foreach (var done in new[] { true, false })
        {
            events.Update(single.EventUid, new EventPatch { IsDone = done });
            events.SetOccurrenceDone(occurrenceId, done);
            remote.PatchAttempts.Clear();
            await Sync(db, remote);
            var expected = done ? "1" : "0";
            Assert.Equal(expected, remote.Items[single.EventUid].ExtendedProperties.Private__["deskcalDone"]);
            Assert.Equal(expected, remote.Items[instanceId].ExtendedProperties.Private__["deskcalDone"]);
            Assert.Equal("0", remote.Items[series.EventUid].ExtendedProperties.Private__["deskcalDone"]);
            Assert.Equal(new[] { instanceId, single.EventUid }.Order(), remote.PatchAttempts.Select(p => p.Id).Order());
        }
    }

    private static string InstanceId(FakeCalendar remote) => remote.Items.Keys.Single(id => id.Contains('_'));

    private static (EventRepository Events, string OccurrenceId, EventRecord Series) WeeklySeries(TestDatabase db)
    {
        var events = new EventRepository(db.Database, Seoul);
        var series = events.Create([new EventInput("카공", "2026-11-08") { StartTime = "15:30", EndTime = "19:00", Repeat = new RecurrenceInput("weekly", Weekdays: [DayOfWeek.Sunday]) }])[0];
        var occurrenceId = events.List(new DateOnly(2026, 11, 15), new DateOnly(2026, 11, 15))[0].OccurrenceId!;
        return (events, occurrenceId, series);
    }

    [Fact]
    public async Task ChecklistRidesOnTheOccurrenceAndReachesAnotherPc()
    {
        using var desktop = new TestDatabase();
        using var laptop = new TestDatabase();
        var remote = new FakeCalendar();
        var (events, occurrenceId, _) = WeeklySeries(desktop);
        await Sync(desktop, remote);

        events.AddChecklistItems(occurrenceId, true, ["자료구조 3장", "설계패턴 과제"]);
        await Sync(desktop, remote);
        await Sync(laptop, remote);

        var other = new EventRepository(laptop.Database, Seoul);
        var occurrences = other.List(new DateOnly(2026, 11, 8), new DateOnly(2026, 11, 22));
        Assert.Equal(["자료구조 3장", "설계패턴 과제"], occurrences.Single(o => o.Event.StartDate == "2026-11-15").Event.Checklist.Select(i => i.Text));
        Assert.All(occurrences.Where(o => o.Event.StartDate != "2026-11-15"), o => Assert.Empty(o.Event.Checklist));
        Assert.All(remote.Items[InstanceId(remote)].ExtendedProperties.Private__, p => Assert.True(p.Key.Length <= 44 && p.Value.Length <= 1024));
    }

    [Fact]
    public async Task EditsOfDifferentItemsOnTwoPcsAreBothKept()
    {
        using var desktop = new TestDatabase();
        using var laptop = new TestDatabase();
        var remote = new FakeCalendar();
        var (events, occurrenceId, _) = WeeklySeries(desktop);
        var items = events.AddChecklistItems(occurrenceId, true, ["하나", "둘"]).Checklist;
        await Sync(desktop, remote); // 시리즈가 먼저 올라가고 회차는 다음 동기화에서 올라간다.
        await Sync(desktop, remote);
        await Sync(laptop, remote);
        var other = new EventRepository(laptop.Database, Seoul);
        var otherId = other.List(new DateOnly(2026, 11, 15), new DateOnly(2026, 11, 15))[0].OccurrenceId!;

        events.UpdateChecklistItem(occurrenceId, true, items[0].ItemId, isDone: true);
        other.UpdateChecklistItem(otherId, true, items[1].ItemId, isDone: true);
        await Sync(desktop, remote);
        await Sync(laptop, remote);
        await Sync(desktop, remote);
        await Sync(laptop, remote);

        foreach (var repository in new[] { events, other })
        {
            var value = repository.List(new DateOnly(2026, 11, 15), new DateOnly(2026, 11, 15))[0].Event;
            Assert.All(value.Checklist, item => Assert.True(item.IsDone));
            Assert.False(value.IsDirty);
        }
    }

    [Fact]
    public async Task SameItemEditedOnTwoPcsFollowsTheLastToReachGoogle()
    {
        using var desktop = new TestDatabase();
        using var laptop = new TestDatabase();
        var remote = new FakeCalendar();
        var (events, occurrenceId, _) = WeeklySeries(desktop);
        var item = events.AddChecklistItems(occurrenceId, true, ["원래"]).Checklist[0];
        await Sync(desktop, remote); // 시리즈가 먼저 올라가고 회차는 다음 동기화에서 올라간다.
        await Sync(desktop, remote);
        await Sync(laptop, remote);
        var other = new EventRepository(laptop.Database, Seoul);
        var otherId = other.List(new DateOnly(2026, 11, 15), new DateOnly(2026, 11, 15))[0].OccurrenceId!;

        events.UpdateChecklistItem(occurrenceId, true, item.ItemId, text: "데스크탑");
        other.UpdateChecklistItem(otherId, true, item.ItemId, text: "노트북");
        await Sync(desktop, remote);
        await Sync(laptop, remote);
        await Sync(desktop, remote);

        Assert.Equal("노트북", events.List(new DateOnly(2026, 11, 15), new DateOnly(2026, 11, 15))[0].Event.Checklist[0].Text);
        Assert.Equal("노트북", other.List(new DateOnly(2026, 11, 15), new DateOnly(2026, 11, 15))[0].Event.Checklist[0].Text);
    }

    [Fact]
    public async Task ChecklistSurvivesRemoteEditOfTheSameEvent()
    {
        using var db = new TestDatabase();
        var remote = new FakeCalendar();
        var events = new EventRepository(db.Database, Seoul);
        var created = events.Create([new EventInput("회의", "2026-11-02")])[0];
        await Sync(db, remote);

        remote.Edit(created.EventUid, e => e.Summary = "폰에서 고침");
        events.AddChecklistItems(created.EventUid, false, ["안건 정리"]);
        await Sync(db, remote);
        await Sync(db, remote);

        var value = events.Get(created.EventUid);
        Assert.Equal("폰에서 고침", value.Title);
        Assert.Equal("안건 정리", Assert.Single(value.Checklist).Text);
        Assert.False(value.IsDirty);
    }

    [Fact]
    public async Task RetryAfterLostInsertResponseKeepsTheOtherPcsCheck()
    {
        using var desktop = new TestDatabase();
        using var laptop = new TestDatabase();
        var events = new EventRepository(desktop.Database, Seoul);
        var created = events.Create([new EventInput("회의", "2026-11-02")])[0];
        var item = events.AddChecklistItems(created.EventUid, false, ["안건"]).Checklist[0];
        var remote = new FakeCalendar { LoseInsertResponse = true };
        await Assert.ThrowsAsync<HttpRequestException>(() => Sync(desktop, remote));
        remote.LoseInsertResponse = false;

        await Sync(laptop, remote);
        new EventRepository(laptop.Database, Seoul).UpdateChecklistItem(created.EventUid, false, item.ItemId, isDone: true);
        await Sync(laptop, remote);
        await Sync(desktop, remote);
        await Sync(desktop, remote);

        Assert.True(events.Get(created.EventUid).Checklist[0].IsDone);
        Assert.Contains("\"d\":true", remote.Items[created.EventUid].ExtendedProperties.Private__["deskcalCheck" + item.ItemId]);
    }

    [Fact]
    public async Task EditMadeWhileMergingIsNotLost()
    {
        using var db = new TestDatabase();
        var remote = new FakeCalendar();
        var events = new EventRepository(db.Database, Seoul);
        var created = events.Create([new EventInput("회의", "2026-11-02")])[0];
        var item = events.AddChecklistItems(created.EventUid, false, ["안건"]).Checklist[0];
        await Sync(db, remote);

        remote.Edit(created.EventUid, e => e.Summary = "폰에서 고침");
        events.UpdateChecklistItem(created.EventUid, false, item.ItemId, text: "바뀐 안건");
        var patches = 0;
        remote.BeforePatch = () => { if (++patches == 2) events.UpdateChecklistItem(created.EventUid, false, item.ItemId, isDone: true); };
        await Sync(db, remote);
        await Sync(db, remote);

        var value = events.Get(created.EventUid);
        Assert.Equal("폰에서 고침", value.Title);
        Assert.True(value.Checklist[0].IsDone);
        Assert.Equal("바뀐 안건", value.Checklist[0].Text);
        Assert.False(value.IsDirty);
    }

    [Fact]
    public async Task ItemLeftOnGoogleByAPatchIsPickedUpFromTheResponse()
    {
        using var db = new TestDatabase();
        var remote = new FakeCalendar();
        var events = new EventRepository(db.Database, Seoul);
        var created = events.Create([new EventInput("회의", "2026-11-02")])[0];
        var item = events.AddChecklistItems(created.EventUid, false, ["안건"]).Checklist[0];
        await Sync(db, remote);

        // etag가 그대로인 채 다른 PC의 항목이 있는 상황: patch는 성공하고 pull은 etag가 같아 건너뛴다.
        Checklist.WriteProperties([new ChecklistItem("other", "다른 PC 항목", false, 9, 1)], remote.Items[created.EventUid].ExtendedProperties.Private__);
        events.UpdateChecklistItem(created.EventUid, false, item.ItemId, isDone: true);
        await Sync(db, remote);

        Assert.Equal(["안건", "다른 PC 항목"], events.Get(created.EventUid).Checklist.Select(i => i.Text));
    }

    [Fact]
    public async Task DeletedItemStaysDeletedOnAnotherPcAndCanBeRestored()
    {
        using var desktop = new TestDatabase();
        using var laptop = new TestDatabase();
        var remote = new FakeCalendar();
        var events = new EventRepository(desktop.Database, Seoul);
        var created = events.Create([new EventInput("회의", "2026-11-02")])[0];
        var item = events.AddChecklistItems(created.EventUid, false, ["안건"]).Checklist[0];
        await Sync(desktop, remote);
        await Sync(laptop, remote);

        events.UpdateChecklistItem(created.EventUid, false, item.ItemId, isDeleted: true);
        await Sync(desktop, remote);
        await Sync(laptop, remote);
        var other = new EventRepository(laptop.Database, Seoul);
        Assert.Empty(Checklist.Visible(other.Get(created.EventUid).Checklist));

        other.UpdateChecklistItem(created.EventUid, false, item.ItemId, isDeleted: false);
        await Sync(laptop, remote);
        await Sync(desktop, remote);
        Assert.Single(Checklist.Visible(events.Get(created.EventUid).Checklist));
    }

    internal sealed class FakeCalendar : ICalendarRemote
    {
        private int _version;
        private readonly List<(int Version, string Id)> _changes = [];
        public Dictionary<string, Event> Items { get; } = [];
        public List<string> InsertAttempts { get; } = [];
        public List<(string Id, string? Status)> PatchAttempts { get; } = [];
        public List<string> DeleteAttempts { get; } = [];
        public HashSet<string> MissingInstances { get; } = [];
        public string? OmitFromNextFullList { get; set; }
        public bool ExpireTokens { get; set; }
        public bool LoseInsertResponse { get; set; }
        public Action? BeforeListReturns { get; set; }
        public Action? BeforePatch { get; set; }
        public List<string?> ListTokens { get; } = [];
        // Google은 만들 때 created를 붙인다. 시험마다 같은 값이 나오도록 만든 순서로 1초씩 늘린다.
        public DateTimeOffset NextCreated { get; set; } = DateTimeOffset.Parse("2026-11-20T00:00:00Z", CultureInfo.InvariantCulture);

        public void Put(Event value)
        {
            value.ETag = "e" + ++_version;
            value.UpdatedDateTimeOffset = DateTimeOffset.Now;
            Items[value.Id] = value;
            _changes.Add((_version, value.Id));
        }

        public void Edit(string id, Action<Event> change)
        {
            change(Items[id]);
            Put(Items[id]);
        }

        public void Cancel(string id) => Edit(id, e => e.Status = "cancelled");

        public Task<(IReadOnlyList<Event> Items, string? NextSyncToken)> ListAsync(string? syncToken, CancellationToken cancellationToken)
        {
            ListTokens.Add(syncToken);
            if (syncToken is not null && ExpireTokens)
            {
                ExpireTokens = false;
                throw new GoogleApiException("calendar", "gone") { HttpStatusCode = HttpStatusCode.Gone };
            }
            var since = syncToken is null ? -1 : int.Parse(syncToken, CultureInfo.InvariantCulture);
            var ids = syncToken is null ? Items.Keys.ToList() : _changes.Where(c => c.Version > since).Select(c => c.Id).Distinct().ToList();
            if (syncToken is null && OmitFromNextFullList is { } omittedId)
            {
                ids.Remove(omittedId);
                OmitFromNextFullList = null;
            }
            IReadOnlyList<Event> result = ids.Where(Items.ContainsKey).Select(id => Copy(Items[id])).ToList();
            BeforeListReturns?.Invoke();
            return Task.FromResult((result, (string?)_version.ToString(CultureInfo.InvariantCulture)));
        }

        // 고치지 않은 회차는 저장하지 않고, 물으면 시리즈 ID와 꼬리로 만들어 준다(Google도 회차 ID로 조회된다).
        public HashSet<string> FailGet { get; } = [];
        public List<string> GetAttempts { get; } = [];

        public Task<Event> GetAsync(string eventId, CancellationToken cancellationToken)
        {
            GetAttempts.Add(eventId);
            if (FailGet.Contains(eventId)) throw new HttpRequestException("조회 실패");
            return Task.FromResult(Items.TryGetValue(eventId, out var value) ? Copy(value) : Instance(eventId));
        }

        // 고치지 않은 회차는 시리즈 값을 물려받는다(제목·메모·색·DeskCal 값). 시각은 회차 키에 시리즈 길이를 더한다.
        private Event Instance(string eventId)
        {
            var split = eventId.LastIndexOf('_');
            if (split < 0 || !Items.TryGetValue(eventId[..split], out var series) || MissingInstances.Contains(eventId)) throw NotFound();
            var suffix = eventId[(split + 1)..];
            EventDateTime start, end, original;
            if (suffix.Length == 8)
            {
                var day = DateOnly.ParseExact(suffix, "yyyyMMdd", CultureInfo.InvariantCulture);
                var days = series.Start?.Date is { } s && series.End?.Date is { } e ? DateOnly.Parse(e, CultureInfo.InvariantCulture).DayNumber - DateOnly.Parse(s, CultureInfo.InvariantCulture).DayNumber : 1;
                original = new EventDateTime { Date = day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) };
                start = original;
                end = new EventDateTime { Date = day.AddDays(days).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) };
            }
            else
            {
                var at = DateTime.SpecifyKind(DateTime.ParseExact(suffix, "yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture), DateTimeKind.Utc);
                var length = series.Start?.DateTimeRaw is { } s && series.End?.DateTimeRaw is { } e
                    ? DateTimeOffset.Parse(e, CultureInfo.InvariantCulture) - DateTimeOffset.Parse(s, CultureInfo.InvariantCulture) : TimeSpan.FromHours(1);
                original = new EventDateTime { DateTimeRaw = at.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture) };
                start = new EventDateTime { DateTimeRaw = original.DateTimeRaw, TimeZone = series.Start?.TimeZone };
                end = new EventDateTime { DateTimeRaw = (at + length).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture), TimeZone = series.Start?.TimeZone };
            }
            return new Event
            {
                Id = eventId, RecurringEventId = eventId[..split], OriginalStartTime = original, Start = start, End = end, Status = "confirmed",
                Summary = series.Summary, Description = series.Description, ColorId = series.ColorId, Transparency = series.Transparency,
                ExtendedProperties = series.ExtendedProperties?.Private__ is { } p
                    ? new Event.ExtendedPropertiesData { Private__ = new Dictionary<string, string>(p) } : null,
            };
        }

        public Task<Event> InsertAsync(Event value, CancellationToken cancellationToken)
        {
            InsertAttempts.Add(value.Id);
            if (Items.ContainsKey(value.Id)) throw new GoogleApiException("calendar", "conflict") { HttpStatusCode = HttpStatusCode.Conflict };
            var stored = Copy(value);
            stored.Status ??= "confirmed";
            stored.CreatedDateTimeOffset ??= NextCreated;
            NextCreated = NextCreated.AddSeconds(1);
            Put(stored);
            if (LoseInsertResponse) throw new HttpRequestException("connection reset");
            return Task.FromResult(Copy(stored));
        }

        public Task<Event> PatchAsync(string eventId, Event value, string? etag, CancellationToken cancellationToken)
        {
            PatchAttempts.Add((eventId, value.Status));
            BeforePatch?.Invoke();
            if (!Items.TryGetValue(eventId, out var stored))
            {
                stored = Instance(eventId);
            }
            else if (etag is not null && etag != stored.ETag)
            {
                throw new GoogleApiException("calendar", "precondition") { HttpStatusCode = HttpStatusCode.PreconditionFailed };
            }
            var timeBefore = TimeKey(stored);
            stored.Summary = value.Summary ?? stored.Summary;
            stored.Description = value.Description ?? stored.Description;
            stored.Status = value.Status ?? stored.Status;
            stored.Start = value.Start ?? stored.Start;
            stored.End = value.End ?? stored.End;
            stored.Recurrence = value.Recurrence ?? stored.Recurrence;
            stored.ColorId = value.ColorId ?? stored.ColorId;
            stored.Transparency = value.Transparency ?? stored.Transparency;
            // Google은 예외가 있는 시리즈의 시각·반복을 바꾸면 예외를 모두 버리고, 옛 인스턴스 ID를 ID와 취소 상태만으로 보낸다(M0.5).
            if (stored.Recurrence is { Count: > 0 } && TimeKey(stored) != timeBefore)
                foreach (var instance in Items.Keys.Where(id => id.StartsWith(eventId + "_", StringComparison.Ordinal)).ToList())
                    Put(new Event { Id = instance, Status = "cancelled" });
            // Google patch는 보내지 않은 키를 그대로 둔다.
            if (value.ExtendedProperties?.Private__ is { } properties)
            {
                stored.ExtendedProperties ??= new Event.ExtendedPropertiesData { Private__ = new Dictionary<string, string>() };
                foreach (var (key, text) in properties) stored.ExtendedProperties.Private__[key] = text;
            }
            Put(stored);
            return Task.FromResult(Copy(stored));
        }

        public Task DeleteAsync(string eventId, string? etag, CancellationToken cancellationToken)
        {
            DeleteAttempts.Add(eventId);
            if (!Items.TryGetValue(eventId, out var stored)) throw NotFound();
            if (etag is not null && etag != stored.ETag) throw new GoogleApiException("calendar", "precondition") { HttpStatusCode = HttpStatusCode.PreconditionFailed };
            Cancel(eventId);
            return Task.CompletedTask;
        }

        private static GoogleApiException NotFound() => new("calendar", "not found") { HttpStatusCode = HttpStatusCode.NotFound };

        private static string TimeKey(Event e) =>
            $"{e.Start?.Date}{e.Start?.DateTimeRaw}|{e.End?.Date}{e.End?.DateTimeRaw}|{string.Join(';', e.Recurrence ?? [])}";

        // 폰에서 시리즈 시각·반복을 바꾼 것을 흉내 낸다. 그 시리즈의 예외는 모두 취소 표시(ID와 상태만)가 된다.
        public void ShiftSeries(string id, Action<Event> change)
        {
            var before = TimeKey(Items[id]);
            Edit(id, change);
            if (TimeKey(Items[id]) == before) return;
            foreach (var instance in Items.Keys.Where(key => key.StartsWith(id + "_", StringComparison.Ordinal)).ToList())
                Put(new Event { Id = instance, Status = "cancelled" });
        }

        private static Event Copy(Event e) => new()
        {
            Id = e.Id, ETag = e.ETag, Summary = e.Summary, Description = e.Description, Status = e.Status, Start = e.Start, End = e.End,
            Recurrence = e.Recurrence, RecurringEventId = e.RecurringEventId, OriginalStartTime = e.OriginalStartTime, ColorId = e.ColorId,
            Transparency = e.Transparency, ExtendedProperties = e.ExtendedProperties, UpdatedRaw = e.UpdatedRaw, CreatedRaw = e.CreatedRaw,
        };
    }
}
