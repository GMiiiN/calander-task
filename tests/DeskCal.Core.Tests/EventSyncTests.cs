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
            stored.ExtendedProperties = value.ExtendedProperties ?? stored.ExtendedProperties;
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
