using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace DeskCal.App;

// One timer for all sidebar rows, including transitions between keyboard and mouse.
internal sealed class SidebarTitleToolTip
{
    private readonly DispatcherTimer _delay = new() { Interval = TimeSpan.FromSeconds(1) };
    private TextBlock? _target;
    private readonly ToolTip _tip;

    public SidebarTitleToolTip(ToolTip tip)
    {
        _tip = tip;
        _delay.Tick += (_, _) => Show();
    }

    public void OnRowFocusChanged(object sender, KeyboardFocusChangedEventArgs args)
    {
        if (sender is not FrameworkElement { Tag: TextBlock title }) return;
        if (args.NewFocus == sender) Enter(title);
        else if (args.OldFocus == sender) Leave(title);
    }

    public void Enter(TextBlock title)
    {
        Dismiss();
        _target = title;
        title.SizeChanged += OnResize;
        title.Unloaded += OnUnload;
        title.DataContextChanged += OnRebound;
        _delay.Start();
    }

    public void Leave(TextBlock title)
    {
        if (_target == title) Dismiss();
    }

    public bool Dismiss()
    {
        if (_target is null) return false;
        _delay.Stop();
        _tip.IsOpen = false;
        _tip.PlacementTarget = null;
        _tip.Content = null;
        _target.SizeChanged -= OnResize;
        _target.Unloaded -= OnUnload;
        _target.DataContextChanged -= OnRebound;
        _target = null;
        return true;
    }

    private void OnResize(object sender, SizeChangedEventArgs args)
    {
        _tip.IsOpen = false;
        _delay.Stop();
        _delay.Start();
    }

    private void OnUnload(object sender, RoutedEventArgs args) => Dismiss();
    private void OnRebound(object sender, DependencyPropertyChangedEventArgs args) => Dismiss();

    private void Show()
    {
        _delay.Stop();
        if (_target is not { IsVisible: true } title || !TextTrim.IsTrimmed(title)) return;
        _tip.PlacementTarget = title;
        _tip.Content = title.Text;
        _tip.IsOpen = true;
    }
}
