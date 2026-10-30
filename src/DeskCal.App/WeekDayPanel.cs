using System.Windows;
using System.Windows.Controls;
using DeskCal.App.Calendar;

namespace DeskCal.App;

// 주간 시간표의 하루 열. 자식의 DataContext(WeekBlockModel)로 세로 위치·높이와 겹침 열을 정한다.
public sealed class WeekDayPanel : Panel
{
    public const double HOUR_HEIGHT = 32;
    private const double GAP = 2;

    protected override Size MeasureOverride(Size availableSize)
    {
        foreach (UIElement child in InternalChildren) child.Measure(availableSize);
        return new Size(0, HOUR_HEIGHT * 24);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        foreach (UIElement child in InternalChildren)
        {
            if ((child as FrameworkElement)?.DataContext is not WeekBlockModel block) continue;
            var width = (finalSize.Width - GAP) / block.ColumnCount;
            var top = block.StartMinute / 60.0 * HOUR_HEIGHT;
            var minutes = Math.Max(block.EndMinute - block.StartMinute, CalendarModelBuilder.MIN_BLOCK_MINUTES);
            var height = minutes / 60.0 * HOUR_HEIGHT - 1;
            child.Arrange(new Rect(GAP + block.Column * width, top, Math.Max(0, width - GAP), height));
        }
        return finalSize;
    }
}
