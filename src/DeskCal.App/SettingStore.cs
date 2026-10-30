using DeskCal.Core;

namespace DeskCal.App;

// 이 PC 전용 설정(§4.2 setting 테이블). 보던 보기·날짜, 창 위치, 투명도처럼 동기화하지 않는 값.
public sealed class SettingStore(Database database)
{
    public string? Get(string key)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT setting_value FROM setting WHERE setting_key = $key;";
        command.Parameters.AddWithValue("$key", key);
        return command.ExecuteScalar() as string;
    }

    public void Set(string key, string value)
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
