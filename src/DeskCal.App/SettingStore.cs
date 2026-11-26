using DeskCal.Core;

namespace DeskCal.App;

// 이 PC 전용 설정(§4.2 setting 테이블). 보던 보기·날짜, 창 위치, 투명도처럼 동기화하지 않는 값.
// 학기(학생 모드)만 Google 연결 시 다른 기기와 맞춘다(S7, Core SemesterSettings).
public sealed class SettingStore(Database database)
{
    public Semester Semester
    {
        get => SemesterSettings.Read(database);
        set => SemesterSettings.Write(database, value);
    }

    internal void SaveSemesterEdit(Semester opened, Semester submitted)
    {
        if (submitted != opened) Semester = submitted;
    }

    public bool DetailedBars
    {
        get => Get("detailed_period_bars") == "1";
        set => Set("detailed_period_bars", value ? "1" : "0");
    }

    public int TextSizePercent
    {
        get => Get("text_size_percent") switch { "115" => 115, "130" => 130, _ => 100 };
        set
        {
            if (value is not (100 or 115 or 130)) throw new ArgumentOutOfRangeException(nameof(value));
            Set("text_size_percent", value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
    }

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
