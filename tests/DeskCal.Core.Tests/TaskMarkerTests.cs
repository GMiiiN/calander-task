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
    }

    private sealed class EchoTasks(bool loseResponse = false) : HttpMessageHandler
    {
        public int Posts { get; private set; }
        private JsonObject? _item;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            JsonNode body;
            if (request.Method == HttpMethod.Post)
            {
                var content = request.Content!;
                using var input = await content.ReadAsStreamAsync(ct);
                using var gzip = content.Headers.ContentEncoding.Contains("gzip") ? new GZipStream(input, CompressionMode.Decompress, true) : null;
                _item = (await JsonNode.ParseAsync(gzip ?? input, cancellationToken: ct))!.AsObject();
                _item["id"] = "remote1";
                Posts++;
                if (loseResponse) return new(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("{}") };
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
