using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Google;
using Google.Apis.Auth.OAuth2.Responses;
using Google.Apis.Calendar.v3.Data;
using Google.Apis.Requests;

namespace DeskCal.Core.Tests;

// S6: 공유 설정 전송기. 두 DB(두 PC)가 가짜 Drive 앱 전용 공간 하나를 함께 쓴다.
public sealed class SharedSettingsTests
{
    [Fact]
    public async Task RenamePublishesAliasAndOtherDeviceAppliesIt()
    {
        using var a = new TestDatabase();
        using var b = new TestDatabase();
        var drive = new FakeAppData();

        Categories(a).Rename("1", " 업무 ");
        await Sync(a, drive);
        await Sync(b, drive);

        Assert.Equal("업무", Name(b, "1"));
        Assert.Equal("세이지", Name(b, "2"));
        Assert.Equal(2, drive.Files.Count); // 기기마다 자기 파일 하나
        Assert.Contains(drive.Files.Values, f => f.Name == $"deskcal-shared-{Device(a)}.json");
        Assert.Contains(drive.Files.Values, f => f.Name == $"deskcal-shared-{Device(b)}.json");
        Assert.False(PendingChanges.HasUnsent(a.Database));

        // 바뀐 것이 없으면 다시 올리지 않고, 지난번 이후 그대로인 파일은 다시 내려받지 않는다.
        await Sync(a, drive); // a가 b의 파일을 처음 읽는다(내용은 같다)
        var (updates, creates, reads) = (drive.Updates, drive.Creates, drive.Reads);
        await Sync(a, drive);
        await Sync(b, drive);
        Assert.Equal((updates, creates, reads), (drive.Updates, drive.Creates, drive.Reads));
        Assert.Equal(0, Conflicts(a) + Conflicts(b));
    }

    // 두 DB 첫 업로드 경합: S6 이전에 각자 바꿔 둔 같은 색 이름을 서로 못 본 채 올린다. 어느 파일도 덮이지 않고 한 값으로 모인다.
    [Fact]
    public async Task FirstUploadRaceConvergesOnOneValueWithoutOverwritingFiles()
    {
        using var a = new TestDatabase();
        using var b = new TestDatabase();
        var drive = new FakeAppData();
        a.Execute("UPDATE category SET name='회사' WHERE color_id='1'");
        b.Execute("UPDATE category SET name='학교' WHERE color_id='1'");

        await Sync(a, drive);
        drive.Hidden.UnionWith(drive.Files.Keys); // b는 a의 파일이 목록에 오기 전에 본다
        await Sync(b, drive);
        drive.Hidden.Clear();
        Assert.Equal(2, drive.Files.Count);
        Assert.Contains("회사", drive.Files.Values.Single(f => f.Name.Contains(Device(a))).Content);
        Assert.Contains("학교", drive.Files.Values.Single(f => f.Name.Contains(Device(b))).Content);

        await Sync(a, drive);
        await Sync(b, drive);
        await Sync(a, drive);
        var winner = string.CompareOrdinal(Device(a), Device(b)) > 0 ? "회사" : "학교";
        Assert.Equal(winner, Name(a, "1"));
        Assert.Equal(winner, Name(b, "1"));
        Assert.Equal(1, Conflicts(a) + Conflicts(b)); // 진 쪽에만 남는다
        Assert.False(PendingChanges.HasUnsent(a.Database) || PendingChanges.HasUnsent(b.Database));
    }

    [Fact]
    public async Task DifferentColorsChangedConcurrentlyBothSurvive()
    {
        using var a = new TestDatabase();
        using var b = new TestDatabase();
        var drive = new FakeAppData();
        Categories(a).Rename("1", "A의 색");
        Categories(b).Rename("2", "B의 색");

        await Sync(b, drive);
        await Sync(a, drive);
        await Sync(b, drive);

        foreach (var db in new[] { a, b })
        {
            Assert.Equal("A의 색", Name(db, "1"));
            Assert.Equal("B의 색", Name(db, "2"));
        }
        Assert.Equal(0, Conflicts(a) + Conflicts(b));
    }

    // 오프라인 복귀: B가 오프라인에서 바꾸는 사이 A의 변경이 먼저 서버에 확정됐다. 먼저 확정된 값이 이기고 B의 값은 충돌 기록에 남는다.
    [Fact]
    public async Task OfflineEditLosesToTheVersionConfirmedFirst()
    {
        using var a = new TestDatabase();
        using var b = new TestDatabase();
        var drive = new FakeAppData();
        Categories(a).Rename("1", "처음");
        await Sync(a, drive);
        await Sync(b, drive);

        Categories(b).Rename("1", "B안"); // 오프라인
        Categories(a).Rename("1", "A안");
        await Sync(a, drive);
        await Sync(b, drive);
        await Sync(a, drive);

        Assert.Equal("A안", Name(a, "1"));
        Assert.Equal("A안", Name(b, "1"));
        Assert.Equal(1, Conflicts(b));
        Assert.Contains("B안", Scalar(b, "SELECT discarded_json FROM sync_conflict WHERE item_kind='settings'"));
        Assert.DoesNotContain(drive.Files.Values, f => f.Content.Contains("B안"));
        Assert.False(PendingChanges.HasUnsent(b.Database));

        // 다시 온라인에서 바꾸면 그 값이 이긴다(rev가 올라간다).
        Categories(b).Rename("1", "B안");
        await Sync(b, drive);
        await Sync(a, drive);
        Assert.Equal("B안", Name(a, "1"));
    }

    // 같은 이름 중복: 두 기기가 서로 다른 색에 같은 이름을 붙였다. 두 기기 모두 같은 규칙으로 하나만 그 이름을 갖는다.
    [Fact]
    public async Task DuplicateNamesResolveTheSameWayOnBothDevices()
    {
        using var a = new TestDatabase();
        using var b = new TestDatabase();
        var drive = new FakeAppData();
        Categories(a).Rename("1", "공부");
        Categories(b).Rename("2", "공부");

        await Sync(a, drive);
        await Sync(b, drive);
        await Sync(a, drive);

        var namesA = Categories(a).List().Select(c => c.Name).ToArray();
        Assert.Equal(namesA, Categories(b).List().Select(c => c.Name).ToArray());
        Assert.Equal(namesA.Length, namesA.Distinct().Count());
        Assert.Single(namesA, n => n == "공부");
        Assert.True(Name(a, "1") == "라벤더" || Name(a, "2") == "세이지"); // 진 색은 기본 이름
        Categories(a).Rename("3", "운동"); // 겹친 이름이 남아 있지 않아 설정 저장이 막히지 않는다
    }

    // 오프라인·재시도: 올리지 못한 이름은 이 PC에 남았다가 다음 동기화에 한 번 올라간다.
    [Fact]
    public async Task FailedUploadStaysPendingAndRetriesWithoutDuplicates()
    {
        using var a = new TestDatabase();
        var drive = new FakeAppData { FailNextWrite = true };
        Categories(a).Rename("4", "약속");

        await Assert.ThrowsAsync<HttpRequestException>(() => Sync(a, drive));
        Assert.True(PendingChanges.HasUnsent(a.Database));
        Assert.Equal("HttpRequestException", SharedSettingsSync.LastError(a.Database));
        Assert.Empty(drive.Files);

        await Sync(a, drive);
        Assert.False(PendingChanges.HasUnsent(a.Database));
        Assert.Null(SharedSettingsSync.LastError(a.Database));
        Assert.Contains("약속", Assert.Single(drive.Files.Values).Content);
    }

    // 생성 응답 유실: 파일은 만들어졌는데 응답을 못 받았다. 다음에 이름으로 찾아 고친다. 이 기기 파일이 둘이면 먼저 만든 것이 정본이다.
    [Fact]
    public async Task LostCreateResponseReusesTheFileAndDuplicatesKeepTheOldest()
    {
        using var a = new TestDatabase();
        using var b = new TestDatabase();
        var drive = new FakeAppData { LoseNextCreateResponse = true };
        Categories(a).Rename("5", "읽기");

        await Assert.ThrowsAsync<HttpRequestException>(() => Sync(a, drive));
        Assert.True(PendingChanges.HasUnsent(a.Database));
        await Sync(a, drive);
        Assert.Equal(1, drive.Creates);
        Assert.False(PendingChanges.HasUnsent(a.Database));
        var canonical = Assert.Single(drive.Files).Key;

        // 같은 기기 이름의 파일이 뒤늦게 하나 더 생겼다(다른 값 하나 포함). 합친 뒤 정본만 남긴다.
        var later = drive.Put($"deskcal-shared-{Device(a)}.json", Document(Device(a), ("category_name:6", "\"요리\"", 1, Device(a))));
        await Sync(a, drive);
        Assert.Equal("요리", Name(a, "6"));
        Assert.Equal(new[] { canonical }, drive.Files.Keys);
        Assert.False(drive.Files.ContainsKey(later));
        await Sync(b, drive);
        Assert.Equal("읽기", Name(b, "5"));
        Assert.Equal("요리", Name(b, "6"));
    }

    // 구버전에 없는 키·필드를 지우지 않는다. 읽을 수 없는 파일과 다른 계정의 파일은 건너뛴다. 옛 형식(at 없음)도 읽는다.
    [Fact]
    public async Task UnknownKeysAndFieldsArePreservedAndBadFilesAreSkipped()
    {
        using var a = new TestDatabase();
        a.Execute("INSERT INTO setting(setting_key, setting_value) VALUES ('google_account_id', 'sub-a')");
        var drive = new FakeAppData();
        var other = Guid.NewGuid().ToString("N");
        var newer = JsonNode.Parse(Document(other, ("category_name:3", "\"독서\"", 2, other), ("semester_v9", "{\"x\":1}", 4, other)))!.AsObject();
        newer["format"] = 2;
        newer["account"] = "sub-a";
        newer["future"] = new JsonObject { ["kept"] = true };
        ((JsonObject)newer["entries"]!["category_name:3"]!).Remove("at");
        drive.Put($"deskcal-shared-{other}.json", newer.ToJsonString());
        drive.Put("deskcal-shared-broken.json", "{ not json");
        drive.Put($"deskcal-shared-{Guid.NewGuid():N}.json", Document("someone", ("category_name:3", "\"남의 계정\"", 9, "someone")).Replace("\"account\":\"\"", "\"account\":\"sub-b\""));
        drive.Put("notes.txt", "다른 이름의 파일");

        await Sync(a, drive);
        Assert.Equal("독서", Name(a, "3"));
        var mine = JsonNode.Parse(drive.Files.Values.Single(f => f.Name.Contains(Device(a))).Content)!;
        Assert.Equal(4, (long)mine["entries"]!["semester_v9"]!["rev"]!);
        Assert.Equal(1, (int)mine["entries"]!["semester_v9"]!["value"]!["x"]!);
        Assert.Equal("sub-a", (string)mine["account"]!);

        // 이 기기 파일에 새 버전이 넣은 최상위 필드가 있으면 다시 올릴 때도 남긴다.
        var ownId = drive.Files.Single(f => f.Value.Name.Contains(Device(a))).Key;
        var own = JsonNode.Parse(drive.Files[ownId].Content)!.AsObject();
        own["added_by_newer_version"] = "keep";
        drive.Replace(ownId, own.ToJsonString());
        Categories(a).Rename("7", "산책");
        await Sync(a, drive);
        var republished = JsonNode.Parse(drive.Files[ownId].Content)!;
        Assert.Equal("keep", (string)republished["added_by_newer_version"]!);
        Assert.Equal("산책", (string)republished["entries"]!["category_name:7"]!["value"]!);
    }

    [Theory]
    [InlineData("truncated", false)]
    [InlineData("truncated", true)]
    [InlineData("missing", false)]
    [InlineData("missing", true)]
    [InlineData("transport", false)]
    [InlineData("future", false)]
    [InlineData("account", true)]
    [InlineData("entry", true)]
    public async Task UnreadableOwnFilePreservesBothCopiesAndRetries(string damage, bool duplicate)
    {
        using var db = new TestDatabase();
        var drive = new FakeAppData();
        Categories(db).Rename("1", "보낼 별칭");
        var me = Device(db);
        var good = Document(me);
        var root = JsonNode.Parse(good)!.AsObject();
        if (damage == "future") root["format"] = 2;
        if (damage == "account") root["account"] = "다른 계정";
        if (damage == "entry") root["entries"]!["unknown"] = new JsonObject { ["rev"] = 1 };
        var damaged = damage == "truncated" ? "{broken" : root.ToJsonString();
        var name = $"deskcal-shared-{me}.json";
        var first = drive.Put(name, duplicate ? good : damaged);
        var second = drive.Put(name, duplicate ? damaged : good);
        var id = duplicate ? second : first;
        if (damage == "missing") drive.Unreadable.Add(id);
        if (damage == "transport") drive.FailedRead = id;

        for (var attempt = 0; attempt < 2; attempt++)
        {
            await Sync(db, drive);
            if (duplicate) Assert.Contains("보낼 별칭", drive.Files[first].Content);
            else Assert.Equal(damaged, drive.Files[first].Content);
            Assert.Equal(duplicate ? damaged : good, drive.Files[second].Content);
            Assert.Equal(duplicate ? 1 : 0, drive.Updates);
            Assert.Equal(!duplicate, PendingChanges.HasUnsent(db.Database));
            Assert.Equal(SharedSettingsSync.UnreadableDeviceFile, SharedSettingsSync.LastError(db.Database));
        }
        drive.Unreadable.Clear();
        drive.FailedRead = null;
        drive.Replace(id, good);
        await Sync(db, drive);
        Assert.Equal(first, Assert.Single(drive.Files).Key);
        Assert.Contains("보낼 별칭", drive.Files[first].Content);
        Assert.False(PendingChanges.HasUnsent(db.Database));
        Assert.Null(SharedSettingsSync.LastError(db.Database));
    }

    [Fact]
    public async Task CachedOwnFileMustBeReadableBeforeUpdating()
    {
        using var db = new TestDatabase();
        var drive = new FakeAppData();
        await Sync(db, drive);
        var original = Assert.Single(drive.Files);
        Categories(db).Rename("1", "보낼 별칭");
        drive.Unreadable.Add(original.Key);
        await Sync(db, drive);
        Assert.Equal(original.Value.Content, drive.Files[original.Key].Content);
        Assert.Equal(0, drive.Updates);
        Assert.True(PendingChanges.HasUnsent(db.Database));
        drive.Unreadable.Clear();
        db.Clock.Now = db.Clock.Now.AddMinutes(16);
        await Sync(db, drive);
        Assert.Equal(1, drive.Updates);
        Assert.False(PendingChanges.HasUnsent(db.Database));
    }

    [Theory]
    [InlineData("category_name:1", "null")]
    [InlineData("day_order:2026-11-26", "false")]
    [InlineData("day_order:2026-11-26", "[null]")]
    [InlineData("semester", """{"enabled":true,"weeks":16}""")]
    public async Task InvalidKnownValueCannotDisplaceAValidSetting(string key, string malformed)
    {
        using var a = new TestDatabase();
        using var b = new TestDatabase();
        var drive = new FakeAppData();
        Categories(a).Rename("1", "업무");
        SemesterSettings.Write(a.Database, new(true, new(2026, 9, 2), 16));
        DayEventOrder.Reorder(a.Events, new(2026, 11, 26), []);
        await Sync(a, drive);
        var canonical = Assert.Single(drive.Files);
        var writer = Device(a);
        // 구버전이 로컬에 이미 채택한 잘못된 높은 revision도 정상 정본을 읽으면 복구된다.
        a.Execute("UPDATE shared_entry SET value_json=$value, rev=99 WHERE entry_key=$key", ("$key", key), ("$value", malformed));
        var bad = Document(writer, (key, malformed, 99, writer));
        var duplicate = drive.Put(canonical.Value.Name, bad);
        await Sync(a, drive);
        await Sync(b, drive);
        Assert.Equal(bad, drive.Files[duplicate].Content);
        Assert.Equal(canonical.Value.Content, drive.Files[canonical.Key].Content);
        foreach (var db in new[] { a, b })
        {
            Assert.Equal("업무", Name(db, "1"));
            Assert.True(SemesterSettings.Read(db.Database).Enabled);
            Assert.Equal(0, db.Scalar("SELECT COUNT(*) FROM shared_entry WHERE rev=99"));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedFileWaitsWhileHealthyFilesContinueToSync(bool canonicalFails)
    {
        using var a = new TestDatabase();
        using var b = new TestDatabase();
        var drive = new FakeAppData();
        await Sync(a, drive);
        var canonical = Assert.Single(drive.Files);
        var duplicate = drive.Put(canonical.Value.Name, Document(Device(a)));
        drive.FailedRead = canonicalFails ? canonical.Key : duplicate;
        Categories(a).Rename("1", "보낼 별칭");
        Categories(b).Rename("2", "다른 PC 별칭");
        await Sync(b, drive);
        drive.ReadIds.Clear();
        for (var n = 0; n < 6; n++)
        {
            await Sync(a, drive);
            Assert.Equal("다른 PC 별칭", Name(a, "2"));
            Assert.Equal(canonicalFails, PendingChanges.HasUnsent(a.Database));
            Assert.True(drive.Files.ContainsKey(duplicate));
            a.Clock.Now = a.Clock.Now.AddMinutes(2);
        }
        Assert.Single(drive.ReadIds, id => id == drive.FailedRead);
        drive.FailedRead = null;
        a.Clock.Now = a.Clock.Now.AddMinutes(5);
        await Sync(a, drive);
        Assert.False(drive.Files.ContainsKey(duplicate));
        Assert.Contains("보낼 별칭", drive.Files[canonical.Key].Content);
        Assert.Null(SharedSettingsSync.LastError(a.Database));
    }

    // 색 ID 계약: 별칭이 바뀌어도 일정의 color_id와 Google에 보내는 colorId는 그대로이고 일정이 다시 올라가지 않는다.
    [Fact]
    public async Task AliasChangeKeepsEventColorIdsAndTheGoogleContract()
    {
        using var a = new TestDatabase();
        using var b = new TestDatabase();
        var drive = new FakeAppData();
        var calendar = new EventSyncTests.FakeCalendar();
        var created = a.Events.Create([new EventInput("회의", "2026-11-26") { ColorId = "1" }]).Single();
        await new EventSync(a.Database, calendar, TestDatabase.Seoul).SyncAsync(CancellationToken.None);
        var etag = calendar.Items[created.EventUid].ETag;

        Categories(b).Rename("1", "업무");
        await Sync(b, drive);
        await Sync(a, drive);
        await new EventSync(a.Database, calendar, TestDatabase.Seoul).SyncAsync(CancellationToken.None);

        Assert.Equal("업무", Name(a, "1"));
        Assert.Equal("1", a.Events.Get(created.EventUid).ColorId);
        Assert.False(a.Events.Get(created.EventUid).IsDirty);
        Assert.Empty(calendar.PatchAttempts);
        Assert.Equal(etag, calendar.Items[created.EventUid].ETag);
        Assert.Equal("1", calendar.Items[created.EventUid].ColorId);
    }

    // Drive 앱 전용 공간 권한을 받지 않은 토큰, 또는 Drive가 403(권한·API 꺼짐)을 주면 공유하지 않고 이 PC 변경은 남긴다.
    [Fact]
    public async Task MissingDriveAccessKeepsAliasesLocalWithAReason()
    {
        using var a = new TestDatabase();
        var drive = new FakeAppData();
        Categories(a).Rename("1", "업무");
        await new ProtectedTokenStore(a.Database).StoreAsync("user", new TokenResponse
        {
            AccessToken = "a", RefreshToken = "r",
            Scope = "openid email https://www.googleapis.com/auth/calendar.events https://www.googleapis.com/auth/tasks"
        });
        var account = new GoogleAccount(a.Database, "missing-client-secret.json");
        Assert.Contains(Google.Apis.Drive.v3.DriveService.Scope.DriveAppdata, GoogleAccount.Scopes); // 처음 연결부터 요청한다(새 동의 불필요)
        Assert.False(await account.HasScopeAsync(Google.Apis.Drive.v3.DriveService.Scope.DriveAppdata));
        Assert.True(account.MissingDriveScope);

        await new SyncCoordinator(a.Database, account, _ => { }).SyncSettingsAsync(drive, CancellationToken.None);
        Assert.Equal(0, drive.Calls);
        Assert.Equal(SharedSettingsSync.ScopeMissing, SharedSettingsSync.LastError(a.Database));
        var pending = Assert.Single(PendingChanges.List(a.Database));
        Assert.Equal(("설정", "카테고리 이름 ‘업무’", SharedSettingsSync.ScopeMissing), (pending.Kind, pending.Title, pending.Reason));

        // 권한 목록이 없는 토큰은 불러 보고, Drive API가 꺼져 있으면 그 이유를 남긴다.
        await new ProtectedTokenStore(a.Database).StoreAsync("user", new TokenResponse { AccessToken = "a", RefreshToken = "r" });
        drive.Fail = Forbidden("accessNotConfigured", "Google Drive API has not been used in project 1 before or it is disabled.");
        await new SyncCoordinator(a.Database, account, _ => { }).SyncSettingsAsync(drive, CancellationToken.None);
        Assert.Equal(SharedSettingsSync.ApiDisabled, SharedSettingsSync.LastError(a.Database));
        drive.Fail = Forbidden("insufficientPermissions", "Request had insufficient authentication scopes.");
        await new SyncCoordinator(a.Database, account, _ => { }).SyncSettingsAsync(drive, CancellationToken.None);
        Assert.Equal(SharedSettingsSync.ScopeMissing, SharedSettingsSync.LastError(a.Database));
        Assert.True(PendingChanges.HasUnsent(a.Database));

        drive.Fail = null;
        await new SyncCoordinator(a.Database, account, _ => { }).SyncSettingsAsync(drive, CancellationToken.None);
        Assert.Null(SharedSettingsSync.LastError(a.Database));
        Assert.False(PendingChanges.HasUnsent(a.Database));
    }

    [Fact]
    public void UnsentAliasCountsForQuickSyncAndShowsInDetails()
    {
        using var a = new TestDatabase();
        var before = DateTimeOffset.UtcNow.AddMinutes(-1).ToString("O", System.Globalization.CultureInfo.InvariantCulture);
        Assert.False(PendingChanges.HasUnsent(a.Database));
        Categories(a).Rename("2", "가족");
        Assert.True(PendingChanges.HasUnsent(a.Database));
        Assert.True(PendingChanges.ExistSince(a.Database, before));
        var pending = Assert.Single(PendingChanges.List(a.Database));
        Assert.Equal(("설정", "category_name:2", "not_signed_in"), (pending.Kind, pending.Uid, pending.Reason));
        // 같은 이름으로 다시 저장하면 새 변경이 아니다.
        Categories(a).Rename("3", "포도");
        Assert.Single(PendingChanges.List(a.Database));
    }

    private static Task Sync(TestDatabase db, FakeAppData drive) =>
        new SharedSettingsSync(db.Database, drive, db.Clock).SyncAsync(CancellationToken.None);

    private static CategoryRepository Categories(TestDatabase db) => new(db.Database);
    private static string Name(TestDatabase db, string colorId) => Categories(db).List().Single(c => c.ColorId == colorId).Name;
    private static long Conflicts(TestDatabase db) => db.Scalar("SELECT COUNT(*) FROM sync_conflict WHERE item_kind='settings'");
    private static string Device(TestDatabase db) => Scalar(db, "SELECT setting_value FROM setting WHERE setting_key='shared_device_id'");

    private static string Scalar(TestDatabase db, string sql)
    {
        using var connection = db.Database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (string)command.ExecuteScalar()!;
    }

    internal static string Document(string device, params (string Key, string ValueJson, long Rev, string By)[] entries)
    {
        var items = new JsonObject();
        foreach (var (key, value, rev, by) in entries)
            items[key] = new JsonObject { ["value"] = JsonNode.Parse(value), ["rev"] = rev, ["by"] = by, ["at"] = "2026-11-25T00:00:00.0000000+00:00" };
        return new JsonObject { ["format"] = 1, ["app"] = "DeskCal", ["device"] = device, ["account"] = "", ["entries"] = items }.ToJsonString();
    }

    private static GoogleApiException Forbidden(string reason, string message) => new("drive", message)
    {
        HttpStatusCode = HttpStatusCode.Forbidden,
        Error = new RequestError { Code = 403, Message = message, Errors = [new SingleError { Reason = reason, Message = message }] }
    };

    // Drive 앱 전용 공간 흉내. 파일 ID는 만든 순서, 시각은 부를 때마다 1초씩 간다.
    internal sealed class FakeAppData : IAppDataStore
    {
        private int _next;
        private int _tick;
        public Dictionary<string, (string Name, string Content, DateTimeOffset Created, DateTimeOffset Modified)> Files { get; } = [];
        public List<string> ReadIds { get; } = [];
        public string? FailedRead { get; set; }
        public HashSet<string> Unreadable { get; } = [];
        public HashSet<string> Hidden { get; } = [];
        public int Creates { get; private set; }
        public int Updates { get; private set; }
        public int Reads { get; private set; }
        public int Calls { get; private set; }
        public bool FailNextWrite { get; set; }
        public bool LoseNextCreateResponse { get; set; }
        public GoogleApiException? Fail { get; set; }

        public string Put(string name, string content)
        {
            var id = "file" + (++_next).ToString("D3", System.Globalization.CultureInfo.InvariantCulture);
            var now = Tick();
            Files[id] = (name, content, now, now);
            return id;
        }

        public void Replace(string id, string content) => Files[id] = Files[id] with { Content = content, Modified = Tick() };

        public Task<IReadOnlyList<AppDataFile>> ListAsync(CancellationToken cancellationToken)
        {
            Call();
            return Task.FromResult<IReadOnlyList<AppDataFile>>(Files.Keys.Where(id => !Hidden.Contains(id)).Select(ToFile).ToList());
        }

        public Task<string?> ReadAsync(string fileId, CancellationToken cancellationToken)
        {
            Call();
            Reads++;
            ReadIds.Add(fileId);
            if (fileId == FailedRead) throw new HttpRequestException("download failed");
            return Task.FromResult(!Unreadable.Contains(fileId) && Files.TryGetValue(fileId, out var file) ? file.Content : null);
        }

        public Task<AppDataFile> CreateAsync(string name, string content, CancellationToken cancellationToken)
        {
            Call();
            Write();
            Creates++;
            var id = Put(name, content);
            if (LoseNextCreateResponse)
            {
                LoseNextCreateResponse = false;
                throw new HttpRequestException("response lost");
            }
            return Task.FromResult(ToFile(id));
        }

        public Task<AppDataFile> UpdateAsync(string fileId, string content, CancellationToken cancellationToken)
        {
            Call();
            Write();
            Updates++;
            Replace(fileId, content);
            return Task.FromResult(ToFile(fileId));
        }

        public Task DeleteAsync(string fileId, CancellationToken cancellationToken)
        {
            Call();
            Files.Remove(fileId);
            return Task.CompletedTask;
        }

        private void Call()
        {
            Calls++;
            if (Fail is { } error) throw error;
        }

        private void Write()
        {
            if (!FailNextWrite) return;
            FailNextWrite = false;
            throw new HttpRequestException("offline");
        }

        private DateTimeOffset Tick() => DateTimeOffset.Parse("2026-11-25T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture).AddSeconds(++_tick);

        private AppDataFile ToFile(string id)
        {
            var file = Files[id];
            return new(id, file.Name, file.Created, file.Modified, Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(file.Content))));
        }
    }
}
