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

// 여러 날 일정 막대: 이어지는 쪽은 칸의 안쪽 여백만큼 밖으로 늘리고(옆 칸 막대와 맞닿게) 모서리를 각지게 한다.
// ConverterParameter = "왼쪽,오른쪽" 늘릴 폭(칸마다 여백이 다르다). 대상이 CornerRadius면 모서리를 돌려준다.
public sealed class BarShapeConverter : IValueConverter
{
    private const double RADIUS = 4;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var chip = value as Calendar.ChipModel;
        var left = chip?.JoinsLeft == true;
        var right = chip?.JoinsRight == true;
        if (targetType == typeof(CornerRadius))
            return new CornerRadius(left ? 0 : RADIUS, right ? 0 : RADIUS, right ? 0 : RADIUS, left ? 0 : RADIUS);
        var extend = ((string)parameter!).Split(',').Select(s => double.Parse(s, CultureInfo.InvariantCulture)).ToArray();
        return new Thickness(left ? -extend[0] : 0, 0, right ? -extend[1] : 0, 0);
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
