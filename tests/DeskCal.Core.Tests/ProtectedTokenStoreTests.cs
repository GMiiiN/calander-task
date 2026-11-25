using System.Net;
using System.Text;
using Google.Apis.Auth.OAuth2.Responses;
using Google.Apis.Http;

namespace DeskCal.Core.Tests;

public sealed class ProtectedTokenStoreTests
{
    // 리뷰 A-6: 토큰 갱신이 5xx가 아닌 이유로 실패하면 Google 라이브러리가 공유 토큰 행을 지워 앱 로그인까지 끊었다.
    // 행을 지우는 경로는 invalid_grant를 받은 앱 동기화 하나뿐이다. MCP(forgetRevokedToken=false)는 어떤 실패에도 지우지 않는다.
    // 실제 Google 인증 라이브러리의 갱신 경로를 오프라인 토큰 엔드포인트로 돌린다. API 요청은 하나도 나가지 않는다.
    [Theory]
    [InlineData(400, "invalid_grant", true, false)]
    [InlineData(400, "invalid_grant", false, true)]
    [InlineData(400, "unauthorized_client", true, true)]
    [InlineData(400, "unauthorized_client", false, true)]
    [InlineData(401, "invalid_client", true, true)]
    [InlineData(401, "invalid_client", false, true)]
    [InlineData(429, "rate_limit_exceeded", true, true)]
    [InlineData(429, "rate_limit_exceeded", false, true)]
    [InlineData(500, "internal_failure", true, true)]
    [InlineData(0, "network", true, true)]
    [InlineData(0, "network", false, true)]
    public async Task OnlyTheAppForgetsTheSharedTokenAndOnlyOnInvalidGrant(int status, string error, bool app, bool kept)
    {
        using var db = new TestDatabase();
        var folder = Path.Combine(Path.GetTempPath(), "deskcal-token-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var secret = Path.Combine(folder, "client_secret.json");
            File.WriteAllText(secret, """{"installed":{"client_id":"test.apps.googleusercontent.com","client_secret":"test"}}""");
            var endpoint = new TokenEndpoint(status, error);
            var account = new GoogleAccount(db.Database, secret)
            {
                HttpClientFactory = new HttpClientFromMessageHandlerFactory(_ => new(endpoint, false, false))
            };
            await new ProtectedTokenStore(db.Database).StoreAsync("user", new TokenResponse
            {
                AccessToken = "expired", RefreshToken = "refresh", ExpiresInSeconds = 3600, IssuedUtc = DateTime.UtcNow.AddHours(-2)
            });
            var log = new List<string>();

            var run = new SyncCoordinator(db.Database, account, log.Add, forgetRevokedToken: app).RunAsync(false, default);
            if (error == "invalid_grant") Assert.True(GoogleAccount.IsRevoked(await Assert.ThrowsAsync<TokenResponseException>(() => run)));
            else Assert.True(await run);

            Assert.Equal(kept, PendingChanges.HasToken(db.Database));
            Assert.NotEmpty(endpoint.Requests);
            Assert.All(endpoint.Requests, uri => Assert.EndsWith("/token", uri.AbsolutePath));
            if (error != "invalid_grant") Assert.Contains(log, line => line.StartsWith("events failed"));
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public async Task LibraryDeleteRequestKeepsTheSharedToken()
    {
        using var db = new TestDatabase();
        var store = new ProtectedTokenStore(db.Database);
        await store.StoreAsync("user", new TokenResponse { AccessToken = "a", RefreshToken = "r" });
        await store.DeleteAsync<TokenResponse>("user");
        Assert.True(PendingChanges.HasToken(db.Database));
    }

    // 앱과 MCP는 같은 DB 옆의 같은 클라이언트 파일을 쓴다. 기본 DB면 예전과 같은 %LOCALAPPDATA%\DeskCal\client_secret.json이다.
    [Fact]
    public void AppAndMcpResolveTheClientFileNextToTheSharedDatabase()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        Assert.Equal(Path.Combine(local, "DeskCal", "client_secret.json"), GoogleAccount.ClientSecretPathFor(Path.Combine(local, "DeskCal", "deskcal.db")));
        var copy = Path.Combine(Path.GetTempPath(), "deskcal-copy", "deskcal.db");
        Assert.Equal(Path.Combine(Path.GetTempPath(), "deskcal-copy", "client_secret.json"), GoogleAccount.ClientSecretPathFor(copy));
    }

    // 토큰 엔드포인트만 흉내 낸다. 다른 주소로 가는 요청은 실패시킨다(실제 Google로 나가지 않음).
    private sealed class TokenEndpoint(int status, string error) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            if (!request.RequestUri!.AbsolutePath.EndsWith("/token")) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden));
            if (status == 0) throw new HttpRequestException("network down");
            return Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)
            {
                Content = new StringContent($$"""{"error":"{{error}}","error_description":"test"}""", Encoding.UTF8, "application/json")
            });
        }
    }

    [Fact]
    public async Task StoresEncryptedTokenInDatabaseAndReadsItBack()
    {
        using var db = new TestDatabase();
        var store = new ProtectedTokenStore(db.Database);
        await store.StoreAsync("user", new TokenResponse { AccessToken = "access-1", RefreshToken = "refresh-1", ExpiresInSeconds = 3599 });

        var token = await store.GetAsync<TokenResponse>("user");
        Assert.Equal("access-1", token.AccessToken);
        Assert.Equal("refresh-1", token.RefreshToken);

        // 평문이 DB에 남지 않는다.
        using var connection = db.Database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT setting_value FROM setting WHERE setting_key = 'google_token';";
        var stored = (string)command.ExecuteScalar()!;
        Assert.DoesNotContain("refresh-1", stored);
        Assert.DoesNotContain("refresh-1", System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(stored)));
    }

    [Fact]
    public async Task ClearRemovesTokenSoAccountIsSignedOut()
    {
        using var db = new TestDatabase();
        var store = new ProtectedTokenStore(db.Database);
        var account = new GoogleAccount(db.Database, "missing-client-secret.json");
        await store.StoreAsync("user", new TokenResponse { AccessToken = "a", RefreshToken = "r" });
        Assert.True(account.IsSignedIn);

        await store.ClearAsync();
        Assert.False(account.IsSignedIn);
        Assert.Null(await store.GetAsync<TokenResponse>("user"));
    }

    // 만료·취소된 토큰(invalid_grant)은 지워서 "연결됨"으로 남지 않게 한다. 계정 결합은 남겨 같은 계정으로만 다시 연결한다.
    [Fact]
    public async Task ForgettingARevokedTokenAsksForReconnectAndKeepsTheBoundAccount()
    {
        using var db = new TestDatabase();
        var account = new GoogleAccount(db.Database, "missing-client-secret.json");
        Assert.False(account.NeedsReconnect);
        using (var connection = db.Database.OpenConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "INSERT INTO setting(setting_key, setting_value) VALUES ('google_account_id', 'sub'), ('google_account_email', 'me@example.com');";
            command.ExecuteNonQuery();
        }
        await new ProtectedTokenStore(db.Database).StoreAsync("user", new TokenResponse { AccessToken = "a", RefreshToken = "r" });
        Assert.False(account.NeedsReconnect);

        await account.ForgetTokenAsync();
        Assert.False(account.IsSignedIn);
        Assert.True(account.NeedsReconnect);
        Assert.Equal("me@example.com", account.Email);
    }

    [Fact]
    public void OnlyInvalidGrantCountsAsRevoked()
    {
        Assert.True(GoogleAccount.IsRevoked(new TokenResponseException(new TokenErrorResponse { Error = "invalid_grant" })));
        Assert.True(GoogleAccount.IsRevoked(new AggregateException(new TokenResponseException(new TokenErrorResponse { Error = "invalid_grant" }))));
        Assert.False(GoogleAccount.IsRevoked(new TokenResponseException(new TokenErrorResponse { Error = "temporarily_unavailable" })));
        Assert.False(GoogleAccount.IsRevoked(new HttpRequestException("no network")));
    }
}
