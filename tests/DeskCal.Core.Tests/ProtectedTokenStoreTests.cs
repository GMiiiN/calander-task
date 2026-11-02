using Google.Apis.Auth.OAuth2.Responses;

namespace DeskCal.Core.Tests;

public sealed class ProtectedTokenStoreTests
{
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
