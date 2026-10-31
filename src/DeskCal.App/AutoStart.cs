using Microsoft.Win32;

namespace DeskCal.App;

// 로그인할 때 실행(§8). 현재 사용자 Run 키에 실행 파일 경로를 넣고 뺀다.
public static class AutoStart
{
    private const string RUN_KEY = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string VALUE_NAME = "DeskCal";

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RUN_KEY);
            return key?.GetValue(VALUE_NAME) is string;
        }
    }

    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RUN_KEY);
        if (enabled) key.SetValue(VALUE_NAME, $"\"{Environment.ProcessPath}\"");
        else key.DeleteValue(VALUE_NAME, throwOnMissingValue: false);
    }
}
