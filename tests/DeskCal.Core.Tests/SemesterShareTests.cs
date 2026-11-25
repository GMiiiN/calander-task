using System.Text.Json.Nodes;
using static DeskCal.Core.Tests.SharedSettingsTests;

namespace DeskCal.Core.Tests;

// S7: 학생 모드의 학기(개강일·총 주차·모드)를 Drive 앱 전용 공간으로 다른 기기와 맞춘다. 기본 꺼짐 값은 다른 기기의 학기를 덮지 않는다.
public sealed class SemesterShareTests
{
    private static readonly Semester Fall = new(true, new DateOnly(2026, 9, 2), 16);

    [Fact]
    public async Task ExplicitSemesterPropagatesAndADefaultOffDeviceDoesNotOverwriteIt()
    {
        using var a = new TestDatabase();
        using var b = new TestDatabase();
        var drive = new FakeAppData();
        // B는 학기를 정한 적이 없다(기본 꺼짐). 먼저 동기화해도 아무것도 올리지 않는다.
        await Share(b, drive);
        // 켠 적 없는 기기가 꺼진 값을 저장해도(예: 자정을 넘겨 기본 시작일이 바뀜) 보내지 않는다.
        SemesterSettings.Write(b.Database, new Semester(false, new DateOnly(2026, 1, 5), 16));
        Assert.False(PendingChanges.HasUnsent(b.Database));
        SemesterSettings.Write(a.Database, Fall);
        await Share(a, drive);
        await Share(b, drive);
        Assert.Equal(Fall, SemesterSettings.Read(b.Database));

        // B에서 설정 창을 학기 그대로 저장해도(다른 설정만 바꿈) 새 버전을 올리지 않는다.
        SemesterSettings.Write(b.Database, SemesterSettings.Read(b.Database));
        Assert.False(PendingChanges.HasUnsent(b.Database));

        // 켜 둔 학기를 끄는 것은 뜻이 있는 변경이라 다른 기기도 끈다.
        SemesterSettings.Write(b.Database, Fall with { Enabled = false });
        await Share(b, drive);
        await Share(a, drive);
        Assert.False(SemesterSettings.Read(a.Database).Enabled);
        Assert.Equal(Fall.Start, SemesterSettings.Read(a.Database).Start);
    }

    // 두 DB: 처음 기본 꺼짐으로 저장한 PC가 이미 켠 PC의 학기를 덮지 않는다. S7 이전에 켜 둔 학기는 첫 동기화 때 올린다.
    [Fact]
    public async Task PreS7SemesterIsSeededAndDefaultOffIsNeverPublished()
    {
        using var a = new TestDatabase();
        using var b = new TestDatabase();
        var drive = new FakeAppData();
        a.Execute("INSERT INTO setting(setting_key, setting_value) VALUES ('semester', $json)",
            ("$json", System.Text.Json.JsonSerializer.Serialize(Fall)));
        b.Execute("INSERT INTO setting(setting_key, setting_value) VALUES ('semester', $json)",
            ("$json", System.Text.Json.JsonSerializer.Serialize(new Semester(false, new DateOnly(2026, 1, 5), 16))));

        await Share(b, drive);
        await Share(a, drive);
        await Share(b, drive);

        Assert.Equal(Fall, SemesterSettings.Read(a.Database));
        Assert.Equal(Fall, SemesterSettings.Read(b.Database));
        Assert.Equal(0, a.Scalar("SELECT COUNT(*) FROM sync_conflict") + b.Scalar("SELECT COUNT(*) FROM sync_conflict"));
    }

    // 다른 버전이 쓴 값: 총 주차가 빠지면 16주, 시작일이 없거나 틀리면 쓰지 않는다. 모르는 필드는 상관없다.
    [Theory]
    [InlineData("{\"enabled\":true,\"start\":\"2026-03-02\",\"exam_weeks\":[8,16]}", true, "2026-03-02", 16)]
    [InlineData("{\"enabled\":true,\"start\":\"2026-03-02\",\"weeks\":15}", true, "2026-03-02", 15)]
    [InlineData("{\"enabled\":true,\"weeks\":15}", false, null, 0)]
    [InlineData("{\"enabled\":true,\"start\":\"03/02/2026\",\"weeks\":15}", false, null, 0)]
    [InlineData("{\"start\":\"2026-03-02\",\"weeks\":0}", true, "2026-03-02", 16)]
    public async Task OtherVersionsSemesterValuesAreReadCarefully(string value, bool applied, string? start, int weeks)
    {
        using var a = new TestDatabase();
        var drive = new FakeAppData();
        var other = Guid.NewGuid().ToString("N");
        drive.Put($"deskcal-shared-{other}.json", Document(other, ("semester", value, 1, other)));
        await Share(a, drive);

        var read = SemesterSettings.Read(a.Database);
        if (!applied)
        {
            Assert.False(read.Enabled);
            Assert.Equal(0, a.Scalar("SELECT COUNT(*) FROM setting WHERE setting_key='semester'"));
            return;
        }
        Assert.Equal(DateOnly.Parse(start!, System.Globalization.CultureInfo.InvariantCulture), read.Start);
        Assert.Equal(weeks, read.Weeks);
        Assert.Equal(JsonNode.Parse(value)!["enabled"]?.GetValue<bool>() ?? false, read.Enabled);
    }

    // 오프라인에서 바꾼 학기는 남았다가 연결되면 올라간다.
    [Fact]
    public async Task OfflineSemesterChangeIsSentLater()
    {
        using var a = new TestDatabase();
        using var b = new TestDatabase();
        var drive = new FakeAppData { FailNextWrite = true };
        SemesterSettings.Write(a.Database, Fall);
        await Assert.ThrowsAsync<HttpRequestException>(() => Share(a, drive));
        Assert.Contains(PendingChanges.List(a.Database), p => p.Title == "학기 설정");

        await Share(a, drive);
        await Share(b, drive);
        Assert.Equal(Fall, SemesterSettings.Read(b.Database));
    }

    // 주차 경계는 앱 상태줄과 같다(개강일이 1주차 첫날, 7일씩, 마지막 주 다음 날부터는 없음, 꺼지면 없음).
    [Theory]
    [InlineData(-1, null)]
    [InlineData(0, 1)]
    [InlineData(6, 1)]
    [InlineData(7, 2)]
    [InlineData(111, 16)]
    [InlineData(112, null)]
    public void WeekOfUsesTheSameBoundariesAsTheApp(int offset, int? expected)
    {
        using var db = new TestDatabase();
        SemesterSettings.Write(db.Database, Fall);
        var today = Fall.Start.AddDays(offset);
        Assert.Equal(expected, SemesterSettings.WeekOf(db.Database, today)?.Current);
        if (expected is not null) Assert.Equal(16, SemesterSettings.WeekOf(db.Database, today)!.Value.Total);
        SemesterSettings.Write(db.Database, Fall with { Enabled = false });
        Assert.Null(SemesterSettings.WeekOf(db.Database, today));
    }

    private static Task Share(TestDatabase db, FakeAppData drive) =>
        new SharedSettingsSync(db.Database, drive, db.Clock).SyncAsync(CancellationToken.None);
}
