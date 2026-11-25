using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace DeskCal.App;

internal static class TextSize
{
    public static void Apply(ResourceDictionary resources, int percent)
    {
        var scale = new ScaleTransform(percent / 100.0, percent / 100.0);
        scale.Freeze();
        resources["TextLayoutScale"] = scale;
    }

    // Dialog content uses the same layout transform as the calendar. Allow taller forms to scroll.
    public static void PlaceDialog(Window dialog, Window owner, double topOffset, double expectedHeight)
    {
        var scale = ((ScaleTransform)dialog.FindResource("TextLayoutScale")).ScaleX;
        var screen = System.Windows.Forms.Screen.FromHandle(new WindowInteropHelper(owner).Handle).WorkingArea;
        var fromPixels = PresentationSource.FromVisual(owner)!.CompositionTarget!.TransformFromDevice;
        var area = Rect.Transform(new Rect(screen.X, screen.Y, screen.Width, screen.Height), fromPixels);
        dialog.WindowStartupLocation = WindowStartupLocation.Manual;
        dialog.Width = Math.Min(area.Width, (dialog.Width - 48) * scale + 48);
        dialog.Left = Math.Clamp(owner.Left + (owner.ActualWidth - dialog.Width) / 2, area.Left, area.Right - dialog.Width);
        dialog.Top = Math.Clamp(owner.Top + topOffset, area.Top, Math.Max(area.Top, area.Bottom - expectedHeight * scale));
        dialog.MaxHeight = area.Bottom - dialog.Top;
    }
}
