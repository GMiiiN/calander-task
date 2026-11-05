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

    private sealed class FakeCalendar : ICalendarRemote
    {
        private int _version;
        private readonly List<(int Version, string Id)> _changes = [];
        public Dictionary<string, Event> Items { get; } = [];
        public bool ExpireTokens { get; set; }
        public bool LoseInsertResponse { get; set; }

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
            if (syncToken is not null && ExpireTokens)
            {
                ExpireTokens = false;
                throw new GoogleApiException("calendar", "gone") { HttpStatusCode = HttpStatusCode.Gone };
            }
            var since = syncToken is null ? -1 : int.Parse(syncToken, CultureInfo.InvariantCulture);
            var ids = syncToken is null ? Items.Keys.ToList() : _changes.Where(c => c.Version > since).Select(c => c.Id).Distinct().ToList();
            IReadOnlyList<Event> result = ids.Where(Items.ContainsKey).Select(id => Copy(Items[id])).ToList();
            return Task.FromResult((result, (string?)_version.ToString(CultureInfo.InvariantCulture)));
        }

        // 고치지 않은 회차는 저장하지 않고, 물으면 시리즈 ID와 꼬리로 만들어 준다(Google도 회차 ID로 조회된다).
        public Task<Event> GetAsync(string eventId, CancellationToken cancellationToken) =>
            Task.FromResult(Items.TryGetValue(eventId, out var value) ? Copy(value) : Instance(eventId));

        private Event Instance(string eventId)
        {
            var split = eventId.LastIndexOf('_');
            if (split < 0 || !Items.ContainsKey(eventId[..split])) throw NotFound();
            var suffix = eventId[(split + 1)..];
            return new Event
            {
                Id = eventId, RecurringEventId = eventId[..split],
                OriginalStartTime = suffix.Length == 8
                    ? new EventDateTime { Date = DateOnly.ParseExact(suffix, "yyyyMMdd", CultureInfo.InvariantCulture).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) }
                    : new EventDateTime { DateTimeRaw = DateTime.ParseExact(suffix, "yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture) },
            };
        }

        public Task<Event> InsertAsync(Event value, CancellationToken cancellationToken)
        {
            if (Items.ContainsKey(value.Id)) throw new GoogleApiException("calendar", "conflict") { HttpStatusCode = HttpStatusCode.Conflict };
            var stored = Copy(value);
            stored.Status ??= "confirmed";
            Put(stored);
            if (LoseInsertResponse) throw new HttpRequestException("connection reset");
            return Task.FromResult(Copy(stored));
        }

        public Task<Event> PatchAsync(string eventId, Event value, string? etag, CancellationToken cancellationToken)
        {
            if (!Items.TryGetValue(eventId, out var stored))
            {
                stored = Instance(eventId);
            }
            else if (etag is not null && etag != stored.ETag)
            {
                throw new GoogleApiException("calendar", "precondition") { HttpStatusCode = HttpStatusCode.PreconditionFailed };
            }
            stored.Summary = value.Summary ?? stored.Summary;
            stored.Description = value.Description ?? stored.Description;
            stored.Status = value.Status ?? stored.Status;
            stored.Start = value.Start ?? stored.Start;
            stored.End = value.End ?? stored.End;
            stored.Recurrence = value.Recurrence ?? stored.Recurrence;
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
            if (!Items.TryGetValue(eventId, out var stored)) throw NotFound();
            if (etag is not null && etag != stored.ETag) throw new GoogleApiException("calendar", "precondition") { HttpStatusCode = HttpStatusCode.PreconditionFailed };
            Cancel(eventId);
            return Task.CompletedTask;
        }

        private static GoogleApiException NotFound() => new("calendar", "not found") { HttpStatusCode = HttpStatusCode.NotFound };

        private static Event Copy(Event e) => new()
        {
            Id = e.Id, ETag = e.ETag, Summary = e.Summary, Description = e.Description, Status = e.Status, Start = e.Start, End = e.End,
            Recurrence = e.Recurrence, RecurringEventId = e.RecurringEventId, OriginalStartTime = e.OriginalStartTime, ColorId = e.ColorId,
            Transparency = e.Transparency, ExtendedProperties = e.ExtendedProperties, UpdatedRaw = e.UpdatedRaw,
        };
    }
}
