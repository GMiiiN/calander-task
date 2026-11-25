using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Google.Apis.Http;
using Google.Apis.Services;

namespace DeskCal.Core.Tests;

// S6: 실제 Google Drive .NET 클라이언트(목록·재개 업로드·alt=media 내려받기·삭제)를 오프라인 HTTP 처리기로 돌린다. 실제 Drive에는 가지 않는다.
public sealed class GoogleAppDataStoreTests
{
    [Fact]
    public async Task StoreUsesOnlyTheAppDataFolderOverHttp()
    {
        var handler = new DriveHandler();
        using var service = Service(handler);
        var store = new GoogleAppDataStore(service);

        var created = await store.CreateAsync("deskcal-shared-abc.json", "{\"name\":\"가\"}", CancellationToken.None);
        Assert.Equal(new[] { "appDataFolder" }, handler.Files[created.Id].Parents);
        Assert.Equal("deskcal-shared-abc.json", created.Name);
        Assert.Equal("{\"name\":\"가\"}", await store.ReadAsync(created.Id, CancellationToken.None));
        var listed = Assert.Single(await store.ListAsync(CancellationToken.None));
        Assert.Equal((created.Id, created.Md5Checksum, created.CreatedTime), (listed.Id, listed.Md5Checksum, listed.CreatedTime));

        var updated = await store.UpdateAsync(created.Id, "{\"name\":\"나\"}", CancellationToken.None);
        Assert.Equal(created.Id, updated.Id);
        Assert.NotEqual(created.Md5Checksum, updated.Md5Checksum);
        Assert.Equal("{\"name\":\"나\"}", await store.ReadAsync(created.Id, CancellationToken.None));

        await store.DeleteAsync(created.Id, CancellationToken.None);
        Assert.Null(await store.ReadAsync(created.Id, CancellationToken.None));
        await store.DeleteAsync(created.Id, CancellationToken.None); // 이미 없으면 그냥 끝난다
        Assert.Empty(await store.ListAsync(CancellationToken.None));
        Assert.All(handler.Lists, query => Assert.Contains("spaces=appDataFolder", query));
    }

    // 두 PC(두 DB)가 같은 가짜 Drive에 실제 클라이언트로 붙어 별칭을 주고받는다.
    [Fact]
    public async Task TwoDatabasesShareAnAliasThroughTheDriveClient()
    {
        using var a = new TestDatabase();
        using var b = new TestDatabase();
        var handler = new DriveHandler();
        using var service = Service(handler);
        var store = new GoogleAppDataStore(service);

        new CategoryRepository(a.Database).Rename("7", "동아리");
        await new SharedSettingsSync(a.Database, store).SyncAsync(CancellationToken.None);
        await new SharedSettingsSync(b.Database, store).SyncAsync(CancellationToken.None);

        Assert.Equal("동아리", new CategoryRepository(b.Database).List().Single(c => c.ColorId == "7").Name);
        Assert.Equal(2, handler.Files.Count);
        Assert.All(handler.Files.Values, f => Assert.StartsWith("deskcal-shared-", f.Name));
    }

    private static Google.Apis.Drive.v3.DriveService Service(DriveHandler handler) => GoogleAppDataStore.CreateService(new BaseClientService.Initializer
    {
        HttpClientFactory = new HttpClientFromMessageHandlerFactory(_ => new(handler, false, false)), ApplicationName = "DeskCal"
    });

    // Drive v3의 appDataFolder 파일 목록·재개 업로드(POST/PATCH → Location → PUT)·alt=media·DELETE를 흉내 낸다.
    private sealed class DriveHandler : HttpMessageHandler
    {
        private const string Root = "https://www.googleapis.com";
        private readonly Dictionary<string, (string? FileId, JsonObject Metadata)> _sessions = [];
        private int _next;
        private int _tick;
        public Dictionary<string, (string Name, string Content, string[] Parents, string Created, string Modified)> Files { get; } = [];
        public List<string> Lists { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            Assert.Equal("www.googleapis.com", uri.Host);
            var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
            var path = uri.AbsolutePath;
            if (path.StartsWith("/upload/drive/v3/files", StringComparison.Ordinal))
            {
                if (query["upload_id"] is { } session) return Finish(session, await Body(request, cancellationToken));
                Assert.Equal("resumable", query["uploadType"]);
                var id = path.Length > "/upload/drive/v3/files/".Length ? path["/upload/drive/v3/files/".Length..] : null;
                Assert.Equal(id is null ? HttpMethod.Post : HttpMethod.Patch, request.Method);
                var metadata = JsonNode.Parse(await Body(request, cancellationToken))!.AsObject();
                var name = "s" + ++_next;
                _sessions[name] = (id, metadata);
                var response = new HttpResponseMessage(HttpStatusCode.OK);
                response.Headers.Location = new Uri($"{Root}/upload/drive/v3/files?uploadType=resumable&upload_id={name}");
                return response;
            }
            if (path == "/drive/v3/files" && request.Method == HttpMethod.Get)
            {
                Lists.Add(uri.Query);
                Assert.Equal("appDataFolder", query["spaces"]);
                return Json(HttpStatusCode.OK, new JsonObject { ["files"] = new JsonArray(Files.Keys.Select(f => (JsonNode)Resource(f)).ToArray()) });
            }
            var fileId = path["/drive/v3/files/".Length..];
            if (request.Method == HttpMethod.Delete)
                return Files.Remove(fileId) ? new HttpResponseMessage(HttpStatusCode.NoContent) : NotFound();
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("media", query["alt"]);
            return Files.TryGetValue(fileId, out var file)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes(file.Content)) }
                : NotFound();
        }

        private HttpResponseMessage Finish(string session, string content)
        {
            var (fileId, metadata) = _sessions[session];
            var now = $"2026-11-25T00:00:{++_tick:00}.000Z";
            if (fileId is null)
            {
                fileId = "id" + _next;
                var parents = (metadata["parents"] as JsonArray)?.Select(p => (string)p!).ToArray() ?? [];
                Files[fileId] = ((string)metadata["name"]!, content, parents, now, now);
            }
            else
            {
                Files[fileId] = Files[fileId] with { Content = content, Modified = now };
            }
            return Json(HttpStatusCode.OK, Resource(fileId));
        }

        private JsonObject Resource(string id)
        {
            var file = Files[id];
            return new JsonObject
            {
                ["id"] = id, ["name"] = file.Name, ["createdTime"] = file.Created, ["modifiedTime"] = file.Modified,
                ["md5Checksum"] = Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(file.Content)))
            };
        }

        private static async Task<string> Body(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Content is null) return "";
            using var stream = await request.Content.ReadAsStreamAsync(cancellationToken);
            using var gzip = request.Content.Headers.ContentEncoding.Contains("gzip") ? new GZipStream(stream, CompressionMode.Decompress) : null;
            using var reader = new StreamReader(gzip ?? stream, Encoding.UTF8);
            return await reader.ReadToEndAsync(cancellationToken);
        }

        private static HttpResponseMessage NotFound() => Json(HttpStatusCode.NotFound,
            new JsonObject { ["error"] = new JsonObject { ["code"] = 404, ["message"] = "File not found." } });

        private static HttpResponseMessage Json(HttpStatusCode status, JsonObject body) => new(status)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
        };
    }
}
