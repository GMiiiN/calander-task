using System.Security.Cryptography;
using System.Text;
using Google.Apis.Json;
using Google.Apis.Util.Store;
using Microsoft.Data.Sqlite;

namespace DeskCal.Core;

/// <summary>
/// Google 토큰을 DPAPI(CurrentUser)로 암호화해 DB의 `setting` 행 하나에 둔다(§5.1). 계정은 하나뿐이라 키는 구분하지 않는다.
/// DB에 두는 이유: 계정 결합 정보와 같은 DB·같은 트랜잭션에 묶이고, 앱과 MCP 서버가 함께 갱신해도 SQLite가 직렬화한다.
/// </summary>
public sealed class ProtectedTokenStore(Database database) : IDataStore
{
    public const string SettingKey = "google_token";

    public Task StoreAsync<T>(string key, T value)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        Write(command, value);
        return Task.CompletedTask;
    }

    public Task<T> GetAsync<T>(string key)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT setting_value FROM setting WHERE setting_key = $key;";
        command.Parameters.AddWithValue("$key", SettingKey);
        if (command.ExecuteScalar() is not string stored) return Task.FromResult(default(T)!);
        var json = Encoding.UTF8.GetString(Unprotect(Convert.FromBase64String(stored)));
        return Task.FromResult(NewtonsoftJsonSerializer.Instance.Deserialize<T>(json));
    }

    public Task DeleteAsync<T>(string key) => ClearAsync();

    public Task ClearAsync()
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM setting WHERE setting_key = $key;";
        command.Parameters.AddWithValue("$key", SettingKey);
        command.ExecuteNonQuery();
        return Task.CompletedTask;
    }

    // 로그인 때 계정 결합 정보와 한 트랜잭션으로 쓰려고 명령을 받는다.
    internal static void Write<T>(SqliteCommand command, T value)
    {
        var json = NewtonsoftJsonSerializer.Instance.Serialize(value);
        command.CommandText = "INSERT INTO setting(setting_key, setting_value) VALUES ($key, $value) " +
            "ON CONFLICT(setting_key) DO UPDATE SET setting_value = excluded.setting_value;";
        command.Parameters.Clear();
        command.Parameters.AddWithValue("$key", SettingKey);
        command.Parameters.AddWithValue("$value", Convert.ToBase64String(Protect(Encoding.UTF8.GetBytes(json))));
        command.ExecuteNonQuery();
    }

    private static byte[] Protect(byte[] data) => OperatingSystem.IsWindows()
        ? ProtectedData.Protect(data, null, DataProtectionScope.CurrentUser)
        : throw new PlatformNotSupportedException();

    private static byte[] Unprotect(byte[] data) => OperatingSystem.IsWindows()
        ? ProtectedData.Unprotect(data, null, DataProtectionScope.CurrentUser)
        : throw new PlatformNotSupportedException();
}
