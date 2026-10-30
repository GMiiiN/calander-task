using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace DeskCal.Spike;

public partial class MainWindow : Window
{
    private const double ShadowMargin = 24;

    private readonly DesktopPinning _pinning;
    private bool _isOpaque = true;
    private bool _isAdjusting;

    public MainWindow()
    {
        InitializeComponent();
        // CenterScreen은 주 모니터가 아닌 곳에 띄울 수 있어(M-1 실측) 주 모니터 작업 영역 가운데에 직접 둔다.
        var workArea = SystemParameters.WorkArea;
        Left = workArea.Left + (workArea.Width - Width) / 2;
        Top = workArea.Top + (workArea.Height - Height) / 2;
        _pinning = new DesktopPinning(this, Path.Combine(Path.GetTempPath(), "deskcal-spike.log"));
        _pinning.StateChanged += state => Dispatcher.Invoke(() => StateText.Text = state.ToString());
        StateText.Text = _pinning.State.ToString();
        SizeChanged += (_, _) => UpdateSizeText();
        LocationChanged += (_, _) => UpdateSizeText();
        Closed += (_, _) => _pinning.Dispose();
        ListenForTestToggle();
    }

    // spike 전용: 검증 스크립트가 조정 모드를 켜고 끌 수 있게 한다.
    private void ListenForTestToggle()
    {
        var signal = new EventWaitHandle(false, EventResetMode.AutoReset, "DeskCalSpike.ToggleAdjust");
        new Thread(() =>
        {
            while (signal.WaitOne()) Dispatcher.Invoke(() => OnToggleAdjust(this, new RoutedEventArgs()));
        }) { IsBackground = true }.Start();
    }

    private void OnToggleAdjust(object sender, RoutedEventArgs e)
    {
        _isAdjusting = !_isAdjusting;
        AdjustOverlay.Visibility = _isAdjusting ? Visibility.Visible : Visibility.Collapsed;
        AdjustButton.Content = _isAdjusting ? "완료" : "크기·위치 조정";
        foreach (var grip in ((Grid)Card.Parent).Children.OfType<Thumb>())
            grip.Visibility = _isAdjusting ? Visibility.Visible : Visibility.Collapsed;
        if (_isAdjusting) _pinning.BeginEditing(bringToFront: true);
        else _pinning.EndEditing();
    }

    private void OnCardMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_isAdjusting) DragMove();
    }

    // 크기 조절은 Win32 테두리 스타일(WS_THICKFRAME)에 기대지 않고 WPF 손잡이로 직접 한다.
    // 투명 layered 창은 시스템 테두리가 없어 HTBOTTOMRIGHT를 돌려줘도 크기가 바뀌지 않았다(M-1 실측).
    private void OnGripDrag(object sender, DragDeltaEventArgs e)
    {
        var edge = (string)((FrameworkElement)sender).Tag;
        if (edge.Contains('R')) Width = Math.Max(MinWidth, Width + e.HorizontalChange);
        if (edge.Contains('B')) Height = Math.Max(MinHeight, Height + e.VerticalChange);
        if (edge.Contains('L'))
        {
            var newWidth = Math.Max(MinWidth, Width - e.HorizontalChange);
            Left += Width - newWidth;
            Width = newWidth;
        }
        if (edge.Contains('T'))
        {
            var newHeight = Math.Max(MinHeight, Height - e.VerticalChange);
            Top += Height - newHeight;
            Height = newHeight;
        }
    }

    private void UpdateSizeText() =>
        SizeText.Text = $"카드 {ActualWidth - ShadowMargin * 2:0} × {ActualHeight - ShadowMargin * 2:0} · 위치 {Left:0}, {Top:0}";

    private void OnToggleOpacity(object sender, RoutedEventArgs e)
    {
        _isOpaque = !_isOpaque;
        Card.Background = new SolidColorBrush(Color.FromArgb(_isOpaque ? (byte)0xED : (byte)0x80, 0xFF, 0xFF, 0xFF));
    }

    private void OnOpenEditor(object sender, RoutedEventArgs e)
    {
        _pinning.BeginEditing();
        var editor = new Window
        {
            Title = "편집 (시험)", Width = 380, Height = 300, Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new TextBox { Margin = new Thickness(20), Text = "치과" },
        };
        editor.ShowDialog();
        _pinning.EndEditing();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
