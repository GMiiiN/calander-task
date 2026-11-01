using Google.Apis.Auth;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Flows;
using Google.Apis.Auth.OAuth2.Responses;
using Google.Apis.Calendar.v3;
using Google.Apis.Drive.v3;
using Google.Apis.Tasks.v1;
using Google.Apis.Util.Store;

namespace DeskCal.Core;

/// <summary>
/// Google 로그인과 토큰(§5.1). 데스크톱 앱 OAuth 클라이언트 + 루프백 + PKCE.
/// 클라이언트 파일(client_secret.json)은 사용자가 자기 Cloud 프로젝트에서 받아 둔다. 저장소에는 넣지 않는다.
/// </summary>
public sealed class GoogleAccount(Database database, string clientSecretPath, string tokenPath)
{
    private const string User = "user";

    // openid·email은 계정 결합(sub)과 트레이 표시(email)에만 쓴다.
    public static readonly string[] Scopes =
        ["openid", "email", CalendarService.Scope.CalendarEvents, TasksService.Scope.Tasks, DriveService.Scope.DriveAppdata];

    public bool HasClientSecret => File.Exists(clientSecretPath);

    public bool IsSignedIn => File.Exists(tokenPath);

    public string? Email => ReadSetting("google_account_email");

    // 브라우저로 로그인한다. 처음 연결한 계정과 다른 계정이면 토큰을 저장하지 않고 거부한다(계정 결합).
    public async Task<string> SignInAsync(string closePageHtml, CancellationToken cancellationToken)
    {
        var secrets = GoogleClientSecrets.FromFile(clientSecretPath).Secrets;
        var credential = await GoogleWebAuthorizationBroker.AuthorizeAsync(
            new GoogleAuthorizationCodeFlow.Initializer { ClientSecrets = secrets }, Scopes, User, usePkce: true,
            cancellationToken, new NullDataStore(), new LocalServerCodeReceiver(closePageHtml));
        var payload = await GoogleJsonWebSignature.ValidateAsync(credential.Token.IdToken,
            new GoogleJsonWebSignature.ValidationSettings { Audience = [secrets.ClientId] });
        var boundId = ReadSetting("google_account_id");
        if (boundId is not null && boundId != payload.Subject)
            throw new InvalidOperationException("Signed in with a different Google account.");
        await new ProtectedTokenStore(tokenPath).StoreAsync(User, credential.Token);
        WriteSetting("google_account_id", payload.Subject);
        WriteSetting("google_account_email", payload.Email);
        return payload.Email;
    }

    // 저장된 토큰으로 자격 증명을 만든다. 로그인 창은 띄우지 않는다(MCP 서버는 로그인하지 않음, §2.4).
    public async Task<UserCredential?> LoadAsync()
    {
        if (!HasClientSecret || !IsSignedIn) return null;
        var store = new ProtectedTokenStore(tokenPath);
        var token = await store.GetAsync<TokenResponse>(User);
        if (token is null) return null;
        var flow = new GoogleAuthorizationCodeFlow(new GoogleAuthorizationCodeFlow.Initializer
        {
            ClientSecrets = GoogleClientSecrets.FromFile(clientSecretPath).Secrets, Scopes = Scopes, DataStore = store
        });
        return new UserCredential(flow, User, token);
    }

    private string? ReadSetting(string key)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT setting_value FROM setting WHERE setting_key = $key;";
        command.Parameters.AddWithValue("$key", key);
        return command.ExecuteScalar() as string;
    }

    private void WriteSetting(string key, string value)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO setting(setting_key, setting_value) VALUES ($key, $value) " +
            "ON CONFLICT(setting_key) DO UPDATE SET setting_value = excluded.setting_value;";
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        command.ExecuteNonQuery();
    }
}
