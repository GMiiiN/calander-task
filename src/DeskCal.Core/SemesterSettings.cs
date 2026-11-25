using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace DeskCal.Core;

/// <summary>
/// 학생 모드의 학기(U5)를 기기 사이에 맞춘다(S7). 이 PC의 값은 setting 표 "semester"(U5와 같은 형식)에 두고,
/// 개강일·총 주차·모드를 공유 설정 항목 "semester" 하나로 함께 보낸다(S6 전송기). 글자 크기·창 위치 등은 PC 전용으로 남는다.
/// 기본값(꺼짐)이 다른 기기의 학기를 덮지 않도록, 켜짐과 관련된 변경(켜기, 켠 상태에서 바꾸기, 켠 것을 끄기)만 보낸다.
/// </summary>
public static class SemesterSettings
{
    public const string SettingKey = "semester";
    public const string SharedKey = "semester";
    public const int DefaultWeeks = 16;

    public static Semester Read(Database database)
    {
        using var connection = database.OpenConnection();
        return Read(connection, null);
    }

    private static Semester Read(SqliteConnection connection, SqliteTransaction? transaction) =>
        Stored(connection, transaction) ?? new(false, DateOnly.FromDateTime(DateTime.Today), DefaultWeeks);

    // 이 PC에서 학기를 정한 적이 있는지(없으면 기본 꺼짐).
    private static Semester? Stored(SqliteConnection connection, SqliteTransaction? transaction)
    {
        using var command = Sql.Command(connection, transaction, "SELECT setting_value FROM setting WHERE setting_key=$key;", ("$key", SettingKey));
        if (command.ExecuteScalar() is not string json) return null;
        try { return JsonSerializer.Deserialize<Semester>(json) is { Weeks: > 0 } term ? term : null; }
        catch (JsonException) { return null; }
    }

    public static void Write(Database database, Semester value)
    {
        if (value.Weeks < 1) throw new ArgumentOutOfRangeException(nameof(value));
        using var connection = database.OpenConnection();
        using var transaction = connection.BeginTransaction(deferred: false);
        var before = Read(connection, transaction);
        if (before == value)
        {
            transaction.Rollback();
            return;
        }
        Save(connection, transaction, value);
        if (value.Enabled || before.Enabled)
            SharedSettings.Edit(connection, transaction, SharedKey, ToShared(value), Validation.Timestamp(TimeProvider.System));
        transaction.Commit();
    }

    private static void Save(SqliteConnection connection, SqliteTransaction transaction, Semester value)
    {
        using var command = Sql.Command(connection, transaction, """
            INSERT INTO setting(setting_key, setting_value) VALUES ($key, $value)
            ON CONFLICT(setting_key) DO UPDATE SET setting_value=excluded.setting_value;
            """, ("$key", SettingKey), ("$value", JsonSerializer.Serialize(value)));
        command.ExecuteNonQuery();
    }

    internal static JsonObject ToShared(Semester value) => new()
    {
        ["enabled"] = value.Enabled, ["start"] = value.Start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), ["weeks"] = value.Weeks
    };

    // 다른 버전이 쓴 값도 읽는다. 시작일이 없거나 틀리면 쓰지 않고, 총 주차가 없거나 틀리면 기본 16주다.
    internal static Semester? FromShared(string json)
    {
        try
        {
            if (JsonNode.Parse(json) is not JsonObject value || value["start"] is not JsonValue start || !start.TryGetValue<string>(out var text)
                || !DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)) return null;
            var enabled = value["enabled"] is JsonValue on && on.TryGetValue<bool>(out var flag) && flag;
            var weeks = value["weeks"] is JsonValue count && count.TryGetValue<int>(out var n) && n > 0 ? n : DefaultWeeks;
            return new(enabled, day, weeks);
        }
        catch (JsonException) { return null; }
    }

    // S7 이전에 이 PC에서 켜 둔 학기를 처음 동기화 때 올린다. 꺼진 기본값은 올리지 않는다(다른 기기의 학기를 덮지 않게).
    internal static void Seed(SqliteConnection connection, SqliteTransaction transaction, string now)
    {
        if (SharedSettings.Read(connection, transaction, SharedKey) is not null || Stored(connection, transaction) is not { Enabled: true } term) return;
        SharedSettings.Edit(connection, transaction, SharedKey, ToShared(term), now);
    }

    // 합친 공유 값을 이 PC 설정에 반영한다.
    internal static void Apply(SqliteConnection connection, SqliteTransaction transaction)
    {
        if (SharedSettings.Read(connection, transaction, SharedKey) is not { } row || FromShared(row.ValueJson) is not { } shared) return;
        if (Stored(connection, transaction) != shared) Save(connection, transaction, shared);
    }

    // 앱 상태줄과 같은 현지 오늘 기준의 주차. 학생 모드가 꺼졌거나 학기 밖이면 null이다.
    public static (int Current, int Total)? WeekOf(Database database, DateOnly today) =>
        Read(database) is { } term && term.WeekOn(today) is { } week ? (week, term.Weeks) : null;
}
