using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace DeskCal.App;

// "#RRGGBB" → 브러시. ConverterParameter는 불투명도(예: 0.14 = 칩 배경 14%).
public sealed class HexBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var color = (Color)ColorConverter.ConvertFromString(value as string ?? "#246BDE");
        if (parameter is string alpha) color.A = (byte)Math.Round(255 * double.Parse(alpha, CultureInfo.InvariantCulture));
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class VisibilityConverter : IValueConverter
{
    // bool이면 true일 때, 숫자면 0보다 클 때, 그 밖에는 null이 아닐 때 보인다. ConverterParameter="Invert"로 반전.
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var visible = value switch { bool b => b, int n => n > 0, string s => s.Length > 0, null => false, _ => true };
        if (parameter as string == "Invert") visible = !visible;
        return visible ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class KoreanWeekdayConverter : IValueConverter
{
    public static readonly KoreanWeekdayConverter Instance = new();
    private static readonly string[] Names = ["일", "월", "화", "수", "목", "금", "토"];

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is DateOnly date ? Names[(int)date.DayOfWeek] : "";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

// 주간 블록의 "09:00–10:00". 그날로 잘린 범위를 보여준다(자정을 넘는 일정은 날마다 자기 구간).
public sealed class WeekBlockTimeConverter : IValueConverter
{
    public static readonly WeekBlockTimeConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is Calendar.WeekBlockModel block ? $"{Clock(block.StartMinute)}–{Clock(block.EndMinute)}" : "";

    private static string Clock(int minute) => $"{minute / 60:00}:{minute % 60:00}";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
