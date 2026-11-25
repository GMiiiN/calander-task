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
/// 토큰은 계정 결합 정보와 같은 DB에 둔다. 그래서 다른 DB를 열면 그 DB의 계정·토큰만 쓴다.
/// </summary>
public sealed class GoogleAccount(Database database, string clientSecretPath)
{
    private const string User = "user";

    // openid·email은 계정 결합(sub)과 트레이 표시(email)에만 쓴다.
    // drive.appdata는 기기 사이에 맞추는 설정(카테고리 별칭 등, S6)을 앱 전용 숨김 폴더에 둘 때 쓴다. 처음 연결(M4)부터 요청해 왔다.
    public static readonly string[] Scopes =
        ["openid", "email", CalendarService.Scope.CalendarEvents, TasksService.Scope.Tasks, DriveService.Scope.DriveAppdata];

    /// <summary>
    /// 저장된 토큰이 그 권한을 받았는지. Google은 동의 화면에서 일부 권한을 빼고 승인할 수 있다.
    /// 토큰에 받은 권한 목록이 없으면(알 수 없음) null이다. 그때는 호출해 보고 403이면 그때 안다.
    /// </summary>
    public async Task<bool?> HasScopeAsync(string scope)
    {
        var token = await new ProtectedTokenStore(database).GetAsync<TokenResponse>(User);
        if (token?.Scope is not { Length: > 0 } granted) return null;
        return granted.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(scope);
    }

    // 연결은 됐지만 Drive 앱 전용 공간 권한만 빠졌다. 트레이에서 같은 계정으로 다시 연결해 권한을 더한다.
    public bool MissingDriveScope => IsSignedIn && HasScopeAsync(DriveService.Scope.DriveAppdata).GetAwaiter().GetResult() == false;

    // 앱과 MCP는 같은 DB(같은 토큰)를 쓰므로 OAuth 클라이언트 파일도 DB 옆의 같은 파일을 쓴다.
    // 토큰은 발급한 클라이언트로만 갱신할 수 있어서, 한쪽만 다른 파일을 쓰면 그쪽 갱신이 4xx로 실패한다.
    public static string ClientSecretPathFor(string databasePath) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(databasePath))!, "client_secret.json");

    // 테스트에서만 바꾼다. 토큰 갱신과 Google API 호출이 이 HTTP 처리기를 쓴다.
    internal Google.Apis.Http.IHttpClientFactory? HttpClientFactory { get; init; }

    public bool HasClientSecret => File.Exists(clientSecretPath);

    // 다른 PC에서 가져온 클라이언트 파일을 제자리에 복사한다. 클라이언트 ID가 없는 파일은 거부한다.
    public void ImportClientSecret(string sourcePath)
    {
        using (var stream = File.OpenRead(sourcePath))
        {
            ClientSecrets? secrets;
            try
            {
                secrets = GoogleClientSecrets.FromStream(stream).Secrets;
            }
            catch (InvalidOperationException)
            {
                // JSON이지만 "installed"·"web" 항목이 없는 파일.
                secrets = null;
            }
            if (secrets?.ClientId is not { Length: > 0 })
                throw new InvalidDataException("Not an OAuth client file.");
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(clientSecretPath))!);
        File.Copy(sourcePath, clientSecretPath, overwrite: true);
    }

    public bool IsSignedIn => ReadSetting(ProtectedTokenStore.SettingKey) is not null;

    public string? Email => ReadSetting("google_account_email");

    // 연결한 적은 있는데 토큰이 없다 = 만료·취소로 끊겼다. 다시 연결해야 한다.
    public bool NeedsReconnect => !IsSignedIn && ReadSetting("google_account_id") is not null;

    // Google이 저장된 토큰을 더는 받지 않는다(만료·취소). 테스트 상태 OAuth 앱은 7일마다 이렇게 된다.
    public static bool IsRevoked(Exception exception) => exception switch
    {
        TokenResponseException { Error.Error: "invalid_grant" } => true,
        AggregateException aggregate => aggregate.InnerExceptions.Any(IsRevoked),
        _ => exception.InnerException is { } inner && IsRevoked(inner)
    };

    public const string AuthErrorKey = "google_auth_error";

    // 토큰은 남았지만 갱신이 invalid_grant가 아닌 4xx(invalid_client·unauthorized_client 등)로 계속 실패한다(S4). 다시 연결해야 한다.
    public string? AuthError => ReadSetting(AuthErrorKey);

    public static string? ReadAuthError(Database database) => new GoogleAccount(database, "").AuthError;

    // 일시 오류(429·5xx·네트워크)는 인증 문제로 보지 않는다.
    internal static string? AuthErrorOf(Exception exception) => exception switch
    {
        TokenResponseException { Error.Error: not "invalid_grant" } token
            when token.StatusCode is { } status && (int)status is >= 400 and < 500 && (int)status != 429 => token.Error.Error ?? "token_error",
        AggregateException aggregate => aggregate.InnerExceptions.Select(AuthErrorOf).FirstOrDefault(e => e is not null),
        _ => exception.InnerException is { } inner ? AuthErrorOf(inner) : null
    };

    internal void RecordAuthError(string? error)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = error is null ? "DELETE FROM setting WHERE setting_key=$key;"
            : "INSERT INTO setting(setting_key, setting_value) VALUES ($key, $value) ON CONFLICT(setting_key) DO UPDATE SET setting_value=excluded.setting_value;";
        command.Parameters.AddWithValue("$key", AuthErrorKey);
        command.Parameters.AddWithValue("$value", error ?? "");
        command.ExecuteNonQuery();
    }

    // 쓸 수 없는 토큰만 지운다. 그래야 "연결됨"으로 남지 않고 트레이에서 다시 연결할 수 있다.
    // 계정 결합 정보는 남겨 같은 계정으로만 다시 연결하게 한다. 앱의 동기화(invalid_grant)만 부른다(SyncCoordinator).
    public Task ForgetTokenAsync() => new ProtectedTokenStore(database).ClearAsync();

    // 브라우저로 로그인한다. 처음 연결한 계정과 다른 계정이면 토큰을 저장하지 않고 거부한다(계정 결합).
    // 계정 ID·이메일·토큰은 한 트랜잭션으로 쓴다. 중간에 실패하면 아무것도 남지 않아 다시 로그인할 수 있다.
    public async Task<string> SignInAsync(string closePageHtml, CancellationToken cancellationToken)
    {
        var secrets = GoogleClientSecrets.FromFile(clientSecretPath).Secrets;
        var credential = await GoogleWebAuthorizationBroker.AuthorizeAsync(
            new GoogleAuthorizationCodeFlow.Initializer { ClientSecrets = secrets }, Scopes, User, usePkce: true,
            cancellationToken, new NullDataStore(), new LocalServerCodeReceiver(closePageHtml));
        var payload = await GoogleJsonWebSignature.ValidateAsync(credential.Token.IdToken,
            new GoogleJsonWebSignature.ValidationSettings { Audience = [secrets.ClientId] });

        using var connection = database.OpenConnection();
        using var transaction = connection.BeginTransaction(deferred: false);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT setting_value FROM setting WHERE setting_key = 'google_account_id';";
        if (command.ExecuteScalar() is string boundId && boundId != payload.Subject)
            throw new InvalidOperationException("Signed in with a different Google account.");
        command.CommandText = "INSERT INTO setting(setting_key, setting_value) VALUES ('google_account_id', $id), ('google_account_email', $email) " +
            "ON CONFLICT(setting_key) DO UPDATE SET setting_value = excluded.setting_value;";
        command.Parameters.AddWithValue("$id", payload.Subject);
        command.Parameters.AddWithValue("$email", payload.Email);
        command.ExecuteNonQuery();
        command.CommandText = "DELETE FROM setting WHERE setting_key = $error;"; // 다시 연결했으니 지난 인증 오류는 지운다
        command.Parameters.AddWithValue("$error", AuthErrorKey);
        command.ExecuteNonQuery();
        ProtectedTokenStore.Write(command, credential.Token);
        transaction.Commit();
        return payload.Email;
    }

    // 저장된 토큰으로 자격 증명을 만든다. 로그인 창은 띄우지 않는다(MCP 서버는 로그인하지 않음, §2.4).
    public async Task<UserCredential?> LoadAsync()
    {
        if (!HasClientSecret) return null;
        var store = new ProtectedTokenStore(database);
        var token = await store.GetAsync<TokenResponse>(User);
        if (token is null) return null;
        var initializer = new GoogleAuthorizationCodeFlow.Initializer
        {
            ClientSecrets = GoogleClientSecrets.FromFile(clientSecretPath).Secrets, Scopes = Scopes, DataStore = store
        };
        if (HttpClientFactory is not null) initializer.HttpClientFactory = HttpClientFactory;
        return new UserCredential(new GoogleAuthorizationCodeFlow(initializer), User, token);
    }

    private string? ReadSetting(string key)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT setting_value FROM setting WHERE setting_key = $key;";
        command.Parameters.AddWithValue("$key", key);
        return command.ExecuteScalar() as string;
    }
}
