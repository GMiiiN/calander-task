using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Google.Apis.Http;
using Google.Apis.Services;
using Google.Apis.Tasks.v1;

namespace DeskCal.Core.Tests;

public class TaskMarkerTests
{
    [Theory]
    [InlineData("")]
    [InlineData("한글\r\n🙂\n\n")]
    public async Task GoogleTasksHttpRoundTripPreservesMemoAndAttempt(string memo)
    {
        var handler = new EchoTasks();
        using var service = GoogleTasksRemote.CreateService(new BaseClientService.Initializer
        {
            HttpClientFactory = new HttpClientFromMessageHandlerFactory(_ => new(handler, false, false))
        });
        var remote = new GoogleTasksRemote(service);
        var token = Guid.NewGuid().ToString("N");
        await remote.InsertAsync(new() { Title = "수업", Notes = TaskMarker.Add(memo, token) }, default);
        var item = Assert.Single(await remote.ListAllAsync(default));
        Assert.Equal((memo, token), TaskMarker.Read(item.Notes));
        var full = new string('가', 8192);
        Assert.Equal(full, TaskMarker.Add(full, token));
    }

    [Fact]
    public async Task ServiceUnavailableAfterRemoteCreateDoesNotRepeatPost()
    {
        using var db = new TestDatabase();
        var created = db.Todos.Create([new("불확실한 응답")]).Single();
        var handler = new EchoTasks(true);
        using var service = GoogleTasksRemote.CreateService(new BaseClientService.Initializer
        {
            HttpClientFactory = new HttpClientFromMessageHandlerFactory(_ => new(handler, false, false))
        });
        var sync = new TaskSync(db.Database, new GoogleTasksRemote(service));
        await sync.SyncAsync(default);
        await sync.SyncAsync(default);
        Assert.Equal(1, handler.Posts);
        Assert.Equal("synced", db.Todos.Get(created.TodoUid).PushState);
        Assert.Single(db.Todos.List());
        Assert.Equal("", (string?)handler.Item!["notes"]); // 표식으로 연결한 뒤 메모에서 지웠다.
    }

    // 리뷰 A-4: 실제 Google 클라이언트 경로에서 생성이 확인되면 같은 동기화에서 If-Match PUT으로 메모의 표식을 지운다.
    [Fact]
    public async Task ConfirmedCreateRemovesTheMarkerWithIfMatchOverHttp()
    {
        using var db = new TestDatabase();
        var created = db.Todos.Create([new("수업 준비", Memo: "교재\r\n챙기기")]).Single();
        var handler = new EchoTasks();
        using var service = GoogleTasksRemote.CreateService(new BaseClientService.Initializer
        {
            HttpClientFactory = new HttpClientFromMessageHandlerFactory(_ => new(handler, false, false))
        });
        var sync = new TaskSync(db.Database, new GoogleTasksRemote(service));
        await sync.SyncAsync(default);
        Assert.Equal(1, handler.Posts);
        Assert.Equal(["\"1\""], handler.IfMatch);
        Assert.Equal("교재\r\n챙기기", (string?)handler.Item!["notes"]);
        await sync.SyncAsync(default);
        Assert.Single(handler.IfMatch);
        Assert.Equal("synced", db.Todos.Get(created.TodoUid).PushState);
        Assert.Equal(0, db.Scalar("SELECT COUNT(*) FROM task_attempt"));
    }

    private const string Token = "0123456789abcdef0123456789abcdef";

    // 리뷰 A-4: 폰·다른 앱이 줄바꿈을 CRLF로 바꾸거나 앞뒤 공백을 정리해도 끝의 표식을 읽는다. 원래 메모의 끝 줄바꿈은 보존한다.
    [Theory]
    [InlineData("메모\n\n[DeskCal:{0}]", "메모")]
    [InlineData("메모\r\n\r\n[DeskCal:{0}]", "메모")]
    [InlineData("메모\n\n\n[DeskCal:{0}]", "메모\n")]
    [InlineData("메모\r\n\r\n\r\n[DeskCal:{0}]", "메모\r\n")]
    [InlineData("메모\n\n[DeskCal:{0}]\r\n  ", "메모")]
    [InlineData("[DeskCal:{0}]", "")]
    [InlineData(" \n[DeskCal:{0}]", "")]
    [InlineData("메모 [DeskCal:{0}]", "메모")]
    [InlineData("메모\n[DeskCal:{0}]", "메모")]
    public void ReadsTrailingMarkerAfterLineEndingAndWhitespaceChanges(string format, string memo)
    {
        Assert.Equal((memo, Token), TaskMarker.Read(string.Format(format, Token)));
    }

    [Theory]
    [InlineData("메모[DeskCal:{0}]")]
    [InlineData("메모\n\n[DeskCal:{0}] 덧붙임")]
    [InlineData("메모\n\n[DeskCal:{1}]")]
    [InlineData("메모\n\n[DeskCal:{0}x]")]
    public void TextThatIsNotATrailingMarkerStaysAsIs(string format)
    {
        var notes = string.Format(format, Token, Token.ToUpperInvariant());
        Assert.Equal((notes, (string?)null), TaskMarker.Read(notes));
    }

    // 리뷰 B-L3: 이미 표식이 있는 메모에 붙여도 표식은 하나다.
    [Fact]
    public void AddKeepsExactlyOneMarker()
    {
        var first = TaskMarker.Add("메모", Token);
        Assert.Equal(first, TaskMarker.Add(first, Token));
        var other = new string('b', 32);
        Assert.Equal(TaskMarker.Add("메모", other), TaskMarker.Add(first, other));
        Assert.Equal(first, TaskMarker.Add(first, null));
        Assert.Equal(("메모", Token), TaskMarker.Read(TaskMarker.Add(first.Replace("\n", "\r\n"), Token)));
    }

    private sealed class EchoTasks(bool loseResponse = false) : HttpMessageHandler
    {
        public int Posts { get; private set; }
        public List<string?> IfMatch { get; } = [];
        public JsonObject? Item => _item;
        private JsonObject? _item;
        private int _version;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            JsonNode body;
            if (request.Method == HttpMethod.Post || request.Method == HttpMethod.Put)
            {
                if (request.Method == HttpMethod.Put)
                {
                    // Google 클라이언트는 본문 ETag로 If-Match를 스스로 붙이고 GoogleTasksRemote도 한 번 더 붙인다(같은 값).
                    var ifMatch = request.Headers.TryGetValues("If-Match", out var values) ? string.Join(",", values.Distinct()) : null;
                    IfMatch.Add(ifMatch);
                    if (ifMatch != (string?)_item!["etag"]) return new(HttpStatusCode.PreconditionFailed) { Content = new StringContent("{}") };
                }
                else Posts++;
                var content = request.Content!;
                using var input = await content.ReadAsStreamAsync(ct);
                using var gzip = content.Headers.ContentEncoding.Contains("gzip") ? new GZipStream(input, CompressionMode.Decompress, true) : null;
                _item = (await JsonNode.ParseAsync(gzip ?? input, cancellationToken: ct))!.AsObject();
                _item["id"] = "remote1";
                _item["etag"] = $"\"{++_version}\"";
                if (loseResponse && request.Method == HttpMethod.Post) return new(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("{}") };
                body = _item;
            }
            else
            {
                Assert.Equal(HttpMethod.Get, request.Method);
                body = new JsonObject { ["items"] = new JsonArray(_item!.DeepClone()) };
            }
            return new(HttpStatusCode.OK) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
        }
    }
}
