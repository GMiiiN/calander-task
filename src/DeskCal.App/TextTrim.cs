using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace DeskCal.App;

// 말줄임(…)으로 잘린 글자인지 판단한다. 잘린 제목에만 전체 제목 툴팁을 띄우는 데 쓴다(feedback 3-2).
public static class TextTrim
{
    public static bool IsTrimmed(TextBlock text)
    {
        var full = new FormattedText(text.Text, CultureInfo.CurrentUICulture, text.FlowDirection,
            new Typeface(text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch),
            text.FontSize, Brushes.Black, VisualTreeHelper.GetDpi(text).PixelsPerDip);
        return full.WidthIncludingTrailingWhitespace > text.ActualWidth;
    }
}
