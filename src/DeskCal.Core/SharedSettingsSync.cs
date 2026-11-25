using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Google;
using Google.Apis.Drive.v3;
using Google.Apis.Upload;
using Microsoft.Data.Sqlite;
using DriveFile = Google.Apis.Drive.v3.Data.File;

namespace DeskCal.Core;

public sealed record AppDataFile(string Id, string Name, DateTimeOffset? CreatedTime, DateTimeOffset? ModifiedTime, string? Md5Checksum);

/// <summary>Google Drive 앱 전용 숨김 폴더(appDataFolder). 이 OAuth 클라이언트(앱)만 읽고 쓴다. 테스트에서는 가짜로 바꾼다.</summary>
public interface IAppDataStore
{
    Task<IReadOnlyList<AppDataFile>> ListAsync(CancellationToken cancellationToken);
    // 목록을 받은 뒤 지워졌으면 null
    Task<string?> ReadAsync(string fileId, CancellationToken cancellationToken);
    Task<AppDataFile> CreateAsync(string name, string content, CancellationToken cancellationToken);
    Task<AppDataFile> UpdateAsync(string fileId, string content, CancellationToken cancellationToken);
    Task DeleteAsync(string fileId, CancellationToken cancellationToken);
}

public sealed class GoogleAppDataStore(DriveService service) : IAppDataStore
{
    private const string SPACE = "appDataFolder";
    private const string FIELDS = "id,name,createdTime,modifiedTime,md5Checksum";
    private const string JSON = "application/json";

    internal static DriveService CreateService(Google.Apis.Services.BaseClientService.Initializer initializer)
    {
        // 503 뒤 생성 요청을 SDK가 몰래 되풀이하지 않게 한다. 다시 보내기는 다음 동기화가 한다(중복 파일은 정본만 남긴다).
        initializer.DefaultExponentialBackOffPolicy = Google.Apis.Http.ExponentialBackOffPolicy.None;
        return new DriveService(initializer);
    }

    public async Task<IReadOnlyList<AppDataFile>> ListAsync(CancellationToken cancellationToken)
    {
        var files = new List<AppDataFile>();
        string? page = null;
        do
        {
            var request = service.Files.List();
            request.Spaces = SPACE;
            request.Q = "trashed = false";
            request.Fields = $"nextPageToken,files({FIELDS})";
            request.PageSize = 100;
            request.PageToken = page;
            var result = await request.ExecuteAsync(cancellationToken);
            files.AddRange((result.Files ?? []).Select(ToFile));
            page = result.NextPageToken;
        } while (page is not null);
        return files;
    }

    public async Task<string?> ReadAsync(string fileId, CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream();
        var progress = await service.Files.Get(fileId).DownloadAsync(stream, cancellationToken);
        if (progress.Status == Google.Apis.Download.DownloadStatus.Completed) return Encoding.UTF8.GetString(stream.ToArray());
        if (progress.Exception is GoogleApiException { HttpStatusCode: HttpStatusCode.NotFound }) return null;
        throw progress.Exception ?? new IOException("Drive download did not complete.");
    }

    public async Task<AppDataFile> CreateAsync(string name, string content, CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));
        var upload = service.Files.Create(new DriveFile { Name = name, Parents = [SPACE], MimeType = JSON }, stream, JSON);
        upload.Fields = FIELDS;
        await Complete(upload, cancellationToken);
        return ToFile(upload.ResponseBody ?? throw new IOException("Drive returned no file."));
    }

    public async Task<AppDataFile> UpdateAsync(string fileId, string content, CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));
        var upload = service.Files.Update(new DriveFile(), fileId, stream, JSON);
        upload.Fields = FIELDS;
        await Complete(upload, cancellationToken);
        return ToFile(upload.ResponseBody ?? throw new IOException("Drive returned no file."));
    }

    public async Task DeleteAsync(string fileId, CancellationToken cancellationToken)
    {
        try { await service.Files.Delete(fileId).ExecuteAsync(cancellationToken); }
        catch (GoogleApiException error) when (error.HttpStatusCode is HttpStatusCode.NotFound) { }
    }

    private static async Task Complete(ResumableUpload upload, CancellationToken cancellationToken)
    {
        var progress = await upload.UploadAsync(cancellationToken);
        if (progress.Status != UploadStatus.Completed) throw progress.Exception ?? new IOException("Drive upload did not complete.");
    }

    private static AppDataFile ToFile(DriveFile file) =>
        new(file.Id, file.Name ?? "", file.CreatedTimeDateTimeOffset, file.ModifiedTimeDateTimeOffset, file.Md5Checksum);
}

/// <summary>
/// 공유 설정 전송기(S6). Drive 앱 전용 공간에 기기(DB)마다 파일 하나(deskcal-shared-&lt;기기 ID&gt;.json)를 둔다.
/// 각 기기는 자기 파일만 고치고, 읽을 때 모든 기기 파일의 항목을 키마다 가장 큰 버전으로 합친다(SharedSettings.Merge).
/// Drive v3 파일 갱신에는 조건부 쓰기(If-Match)가 없어, 한 파일을 여러 기기가 덮어쓰면 다른 기기의 변경을 잃을 수 있기 때문이다.
/// 자기 파일이 여럿이면(생성 응답 유실 뒤 다시 만듦) 만든 시각, 같으면 ID가 가장 앞선 것이 정본이고 나머지는 합친 뒤 지운다.
/// 모르는 키(새 버전이 쓴 항목)와 모르는 최상위 필드도 그대로 다시 올린다. 다른 Google 계정이 쓴 파일은 읽지 않는다.
/// </summary>
public sealed class SharedSettingsSync(Database database, IAppDataStore store, TimeProvider? clock = null, Action<string>? log = null,
    TimeZoneInfo? localTimeZone = null)
{
    internal const string FilePrefix = "deskcal-shared-";
    internal const string SyncKey = "settings";
    private const string CachePrefix = "shared_file:";
    internal const int Format = 1;
    public const string ScopeMissing = "drive_scope_missing";
    public const string ApiDisabled = "drive_api_disabled";
    public const string UnreadableDeviceFile = "unreadable_device_file";

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    internal static string FileName(string device) => $"{FilePrefix}{device}.json";
    private static bool IsDeviceFile(string name) => name.StartsWith(FilePrefix, StringComparison.Ordinal) && name.EndsWith(".json", StringComparison.Ordinal);

    public async Task SyncAsync(CancellationToken cancellationToken)
    {
        try
        {
            await RunAsync(cancellationToken);
        }
        catch (GoogleApiException error) when (Unavailable(error) is { } reason)
        {
            // 이 PC의 변경은 그대로 두고(dirty) 상태 상세에 이유를 보인다. Calendar·Tasks 동기화와는 상관없다.
            RecordError(database, reason);
            log?.Invoke($"settings: {reason} ({error.HttpStatusCode})");
        }
        catch (Exception error) when (!cancellationToken.IsCancellationRequested)
        {
            RecordError(database, error.GetType().Name);
            throw;
        }
    }

    internal static string? Unavailable(GoogleApiException error)
    {
        if (error.HttpStatusCode != HttpStatusCode.Forbidden) return null;
        var reasons = error.Error?.Errors?.Select(e => e.Reason).ToArray() ?? [];
        var message = error.Error?.Message ?? error.Message;
        if (reasons.Contains("insufficientPermissions") || message.Contains("insufficient authentication scopes", StringComparison.OrdinalIgnoreCase))
            return ScopeMissing;
        if (reasons.Contains("accessNotConfigured") || message.Contains("has not been used in project", StringComparison.OrdinalIgnoreCase)
            || message.Contains("is disabled", StringComparison.OrdinalIgnoreCase))
            return ApiDisabled;
        return null;
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        string me, account;
        using (var connection = database.OpenConnection())
        {
            me = SharedSettings.DeviceId(connection, null);
            using var bound = Sql.Command(connection, null, "SELECT setting_value FROM setting WHERE setting_key='google_account_id'");
            account = bound.ExecuteScalar() as string ?? "";
        }
        var files = (await store.ListAsync(cancellationToken)).Where(f => IsDeviceFile(f.Name)).ToList();
        var own = files.Where(f => f.Name == FileName(me))
            .OrderBy(f => f.CreatedTime ?? DateTimeOffset.MaxValue).ThenBy(f => f.Id, StringComparer.Ordinal).ToList();
        var cache = ReadCache();
        var read = new Dictionary<string, Document?>();
        var ownName = FileName(me);
        var holdWrites = false;
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // 지난번 이후 바뀌지 않은 파일은 이미 합쳤다(합치기는 여러 번 해도 같다).
            var isOwn = file.Name == ownName;
            if ((!isOwn || own.Count == 1) && (file.Md5Checksum is not null || file.ModifiedTime is not null)
                && cache.TryGetValue(file.Id, out var seen) && seen == Token(file, isOwn)) continue;
            var text = await store.ReadAsync(file.Id, cancellationToken);
            var document = text is null ? null : Document.Parse(text);
            if (isOwn && (document?.CanRewrite(me, account) != true || file != own[0] && document.Extra.Count > 0))
            {
                holdWrites = true;
                continue;
            }
            if (document is null) log?.Invoke($"settings: 읽을 수 없는 공유 설정 파일을 건너뛴다 ({file.Name})");
            else if (document.Account is { } other && account.Length > 0 && other != account)
            {
                log?.Invoke($"settings: 다른 Google 계정의 공유 설정 파일을 건너뛴다 ({file.Name})");
                document = null;
            }
            read[file.Id] = document;
        }

        var now = Validation.Timestamp(_clock);
        using (var connection = database.OpenConnection())
        using (var transaction = connection.BeginTransaction(deferred: false))
        {
            SharedSettings.SeedCategoryNames(connection, transaction, now);
            DayEventOrder.SeedLegacy(connection, transaction, new EventRepository(database, localTimeZone));
            SemesterSettings.Seed(connection, transaction, now);
            SharedSettings.Merge(connection, transaction, read.Values.OfType<Document>().SelectMany(d => d.Entries), now);
            SharedSettings.ApplyCategoryNames(connection, transaction);
            SemesterSettings.Apply(connection, transaction);
            foreach (var file in files.Where(f => f.Name != ownName && read.GetValueOrDefault(f.Id) is not null))
                WriteCache(connection, transaction, file);
            var listed = files.Select(f => CachePrefix + f.Id).ToHashSet();
            foreach (var stale in cache.Keys.Select(id => CachePrefix + id).Where(key => !listed.Contains(key)))
            {
                using var forget = Sql.Command(connection, transaction, "DELETE FROM sync_state WHERE sync_key=$key", ("$key", stale));
                forget.ExecuteNonQuery();
            }
            transaction.Commit();
        }

        if (holdWrites)
        {
            HoldDeviceFile();
            return;
        }
        await PublishAsync(me, account, own, read, cancellationToken);
    }

    // 이 기기 파일을 지금 합친 내용으로 맞춘다. 올린 내용이 그대로면 올리지 않는다.
    private async Task PublishAsync(string me, string account, IReadOnlyList<AppDataFile> own, Dictionary<string, Document?> read,
        CancellationToken cancellationToken)
    {
        List<SharedSettings.Row> rows;
        using (var connection = database.OpenConnection()) rows = SharedSettings.ReadAll(connection, null);
        var canonical = own.FirstOrDefault();
        var entries = new JsonObject();
        foreach (var row in rows)
            entries[row.Key] = new JsonObject
            {
                ["value"] = JsonNode.Parse(row.ValueJson), ["rev"] = row.Version.Rev, ["by"] = row.Version.Writer, ["at"] = row.WrittenAt
            };
        var content = new JsonObject { ["format"] = Format, ["app"] = "DeskCal", ["device"] = me, ["account"] = account, ["entries"] = entries };
        var hash = Hash(content);
        var state = ReadState();
        // 이번에 정본을 읽었으면 그 내용과, 읽지 않았으면(지난번 이후 그대로) 지난번 올린 내용과 비교한다.
        Document? current = null;
        var wasRead = canonical is not null && read.TryGetValue(canonical.Id, out current);
        var remoteHash = canonical is null ? null : current is not null ? Hash(current.ToContent()) : !wasRead && state.FileId == canonical.Id ? state.Hash : null;

        AppDataFile? uploaded = null;
        if (canonical is null || remoteHash != hash)
        {
            // 새 버전이 넣은 모르는 최상위 필드는 그대로 둔다. 이번에 읽지 않았으면 덮어쓰기 전에 한 번 읽는다.
            if (canonical is not null && !wasRead)
            {
                var existing = await store.ReadAsync(canonical.Id, cancellationToken);
                current = existing is null ? null : Document.Parse(existing);
                if (current?.CanRewrite(me, account) != true || Hash(current.Raw) != remoteHash)
                {
                    // 목록의 버전과 실제 다운로드가 엇갈렸다. 다음 차례에 원문을 다시 합치도록 캐시를 비운다.
                    using var connection = database.OpenConnection();
                    using var forget = Sql.Command(connection, null, "DELETE FROM sync_state WHERE sync_key=$key", ("$key", CachePrefix + canonical.Id));
                    forget.ExecuteNonQuery();
                    HoldDeviceFile();
                    return;
                }
            }
            foreach (var (name, value) in current?.Extra ?? []) content[name] = value?.DeepClone();
            var text = content.ToJsonString(new JsonSerializerOptions { Encoder = TaskSync.ConflictJson.Encoder });
            uploaded = canonical is null
                ? await store.CreateAsync(FileName(me), text, cancellationToken)
                : await store.UpdateAsync(canonical.Id, text, cancellationToken);
        }
        using (var connection = database.OpenConnection())
        using (var transaction = connection.BeginTransaction(deferred: false))
        {
            SharedSettings.MarkPublished(connection, transaction, rows);
            if ((uploaded ?? canonical) is { } verified) WriteCache(connection, transaction, verified, own: true);
            using var save = Sql.Command(connection, transaction, """
                INSERT INTO sync_state(sync_key, remote_id, sync_token, last_synced_at, last_error) VALUES ($key, $file, $hash, $now, NULL)
                ON CONFLICT(sync_key) DO UPDATE SET remote_id=excluded.remote_id, sync_token=excluded.sync_token,
                    last_synced_at=excluded.last_synced_at, last_error=NULL
                """, ("$key", SyncKey), ("$file", uploaded?.Id ?? canonical?.Id), ("$hash", hash), ("$now", Validation.Timestamp(_clock)));
            save.ExecuteNonQuery();
            transaction.Commit();
        }
        // 정본이 아닌 이 기기 파일은 이미 합쳤으므로 지운다. 실패하면 다음 동기화에서 다시 한다.
        foreach (var duplicate in own.Skip(1))
        {
            try { await store.DeleteAsync(duplicate.Id, cancellationToken); }
            catch (Exception error) when (error is not OperationCanceledException) { log?.Invoke($"settings: 중복 파일 정리 보류 ({error.GetType().Name})"); }
        }
    }

    private static string Hash(JsonObject content)
    {
        var canonical = new JsonObject();
        foreach (var name in new[] { "format", "device", "account" }) canonical[name] = content[name]?.DeepClone();
        var entries = new JsonObject();
        if (content["entries"] is JsonObject source)
            foreach (var (key, value) in source.OrderBy(pair => pair.Key, StringComparer.Ordinal)) entries[key] = value?.DeepClone();
        canonical["entries"] = entries;
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToJsonString())));
    }

    private void HoldDeviceFile()
    {
        RecordError(database, UnreadableDeviceFile);
        log?.Invoke("settings: 이 기기의 공유 파일을 확인할 수 없어 업로드와 중복 파일 정리를 보류합니다.");
    }

    // 예전 버전은 읽지 못한 파일도 캐시했다. 자기 파일은 검증을 거친 토큰만 재사용한다.
    private static string Token(AppDataFile file, bool own) => $"{(own ? "verified:" : "")}{file.Md5Checksum}|{file.ModifiedTime?.UtcDateTime:O}";

    private Dictionary<string, string> ReadCache()
    {
        using var connection = database.OpenConnection();
        using var command = Sql.Command(connection, null, "SELECT sync_key, sync_token FROM sync_state WHERE sync_key LIKE 'shared_file:%'");
        using var reader = command.ExecuteReader();
        var cache = new Dictionary<string, string>();
        while (reader.Read()) cache[reader.GetString(0)[CachePrefix.Length..]] = reader.IsDBNull(1) ? "" : reader.GetString(1);
        return cache;
    }

    private static void WriteCache(SqliteConnection connection, SqliteTransaction transaction, AppDataFile file, bool own = false)
    {
        using var command = Sql.Command(connection, transaction, """
            INSERT INTO sync_state(sync_key, sync_token, remote_id) VALUES ($key, $token, $id)
            ON CONFLICT(sync_key) DO UPDATE SET sync_token=excluded.sync_token, remote_id=excluded.remote_id
            """, ("$key", CachePrefix + file.Id), ("$token", Token(file, own)), ("$id", file.Id));
        command.ExecuteNonQuery();
    }

    private (string? FileId, string? Hash) ReadState()
    {
        using var connection = database.OpenConnection();
        using var command = Sql.Command(connection, null, "SELECT remote_id, sync_token FROM sync_state WHERE sync_key=$key", ("$key", SyncKey));
        using var reader = command.ExecuteReader();
        return reader.Read() ? (reader.IsDBNull(0) ? null : reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1)) : (null, null);
    }

    public static void RecordError(Database database, string error)
    {
        using var connection = database.OpenConnection();
        using var command = Sql.Command(connection, null, """
            INSERT INTO sync_state(sync_key, last_error) VALUES ($key, $error)
            ON CONFLICT(sync_key) DO UPDATE SET last_error=excluded.last_error
            """, ("$key", SyncKey), ("$error", error));
        command.ExecuteNonQuery();
    }

    public static string? LastError(Database database)
    {
        using var connection = database.OpenConnection();
        using var command = Sql.Command(connection, null, "SELECT last_error FROM sync_state WHERE sync_key=$key", ("$key", SyncKey));
        return command.ExecuteScalar() as string;
    }

    // 상태줄과 미전송 상세에 표시할 이유.
    public static string? UnavailableReason(Database database) =>
        LastError(database) is (ScopeMissing or ApiDisabled or UnreadableDeviceFile) and var reason ? reason : null;

    // 기기 파일. 항목: { "<키>": { "value": …, "rev": n, "by": "<기기>", "at": "<UTC 시각>" } }
    internal sealed record Document(string? Device, string? Account, IReadOnlyList<SharedSettings.Entry> Entries, JsonObject Extra, JsonObject Raw)
    {
        private static readonly HashSet<string> Known = ["format", "app", "device", "account", "entries"];

        public bool CanRewrite(string device, string account) =>
            Device == device && Account == account && Text(Raw["app"]) == "DeskCal"
            && Raw["format"] is JsonValue format && format.TryGetValue<int>(out var version) && version == Format
            && Raw["entries"] is JsonObject entries && entries.Count == Entries.Count
            && entries.All(pair => pair.Value is JsonObject item && item.ContainsKey("value")
                && item.All(p => p.Key is "value" or "rev" or "by" or "at")
                && (!item.ContainsKey("at") || Text(item["at"]) is not null));

        public JsonObject ToContent() => (JsonObject)Raw.DeepClone();

        public static Document? Parse(string text)
        {
            try
            {
                if (JsonNode.Parse(text) is not JsonObject root || root["entries"] is not JsonObject entries) return null;
                var list = new List<SharedSettings.Entry>();
                foreach (var (key, node) in entries)
                {
                    if (node is not JsonObject item || item["rev"] is not JsonValue rev || !rev.TryGetValue<long>(out var number) || number < 1
                        || item["by"] is not JsonValue by || !by.TryGetValue<string>(out var writer) || writer.Length == 0) continue;
                    var at = item["at"] is JsonValue written && written.TryGetValue<string>(out var time) ? time : "";
                    list.Add(new(key, item["value"]?.DeepClone(), new(number, writer), at));
                }
                var extra = new JsonObject();
                foreach (var (name, value) in root)
                    if (!Known.Contains(name)) extra[name] = value?.DeepClone();
                return new(Text(root["device"]), Text(root["account"]), list, extra, root);
            }
            catch (Exception error) when (error is JsonException or InvalidOperationException or ArgumentException or FormatException)
            {
                return null;
            }
        }

        private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
    }
}
