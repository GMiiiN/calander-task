using System.Security.Cryptography;
using System.Text;
using Google.Apis.Json;
using Google.Apis.Util.Store;

namespace DeskCal.Core;

/// <summary>
/// Google 토큰을 DPAPI(CurrentUser)로 암호화해 파일 하나에 둔다(§5.1). 계정은 하나뿐이라 키는 구분하지 않는다.
/// 앱과 MCP 서버가 함께 토큰을 갱신할 수 있으므로 임시 파일에 쓴 뒤 바꿔 끼운다.
/// </summary>
public sealed class ProtectedTokenStore(string path) : IDataStore
{
    public Task StoreAsync<T>(string key, T value)
    {
        var json = NewtonsoftJsonSerializer.Instance.Serialize(value);
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, Protect(Encoding.UTF8.GetBytes(json)));
        File.Move(temp, path, overwrite: true);
        return Task.CompletedTask;
    }

    public Task<T> GetAsync<T>(string key)
    {
        if (!File.Exists(path)) return Task.FromResult(default(T)!);
        var json = Encoding.UTF8.GetString(Unprotect(File.ReadAllBytes(path)));
        return Task.FromResult(NewtonsoftJsonSerializer.Instance.Deserialize<T>(json));
    }

    public Task DeleteAsync<T>(string key) => ClearAsync();

    public Task ClearAsync()
    {
        File.Delete(path);
        return Task.CompletedTask;
    }

    private static byte[] Protect(byte[] data) => OperatingSystem.IsWindows()
        ? ProtectedData.Protect(data, null, DataProtectionScope.CurrentUser)
        : throw new PlatformNotSupportedException();

    private static byte[] Unprotect(byte[] data) => OperatingSystem.IsWindows()
        ? ProtectedData.Unprotect(data, null, DataProtectionScope.CurrentUser)
        : throw new PlatformNotSupportedException();
}
