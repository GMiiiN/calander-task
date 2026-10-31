using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DeskCal.App.Calendar;
using DeskCal.Core;

namespace DeskCal.App;

public partial class MainWindow : Window
{
    private const double SHADOW_MARGIN = 24;
    private const int FIRST_VISIBLE_HOUR = 8;
    private static readonly string[] WeekdayNames = ["일", "월", "화", "수", "목", "금", "토"];

    private readonly MainViewModel _viewModel;
    private readonly Database _database;
    private readonly EventRepository _events;
    private readonly TodoRepository _todos;
    private readonly DesktopPinning _pinning;
    private readonly WallpaperTone _wallpaperTone;
    private readonly DataVersionWatcher _watcher;
    private readonly ChangeSignal _signal = new();
    private readonly RegisteredWaitHandle _signalRegistration;
    private readonly DispatcherTimer _fallbackTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    private readonly DispatcherTimer _clockTimer = new() { Interval = TimeSpan.FromMinutes(1) };
    private bool _isClosed;

    /// <summary>사용자가 ⋯ 메뉴로 종료했는지. 아니면 창이 닫혀도 앱이 다시 띄운다.</summary>
    public bool ExitRequested { get; private set; }

    public MainWindow(Database database)
    {
        InitializeComponent();
        _database = database;
        _events = new EventRepository(database);
        _todos = new TodoRepository(database);
        _viewModel = new MainViewModel(database);
        DataContext = _viewModel;
        PlaceWindow();
        BuildWeekdayHeader();
        BuildHourGrid();
        UpdateViewToggle();
        _viewModel.PropertyChanged += OnViewModelChanged;

        _pinning = new DesktopPinning(this, LogPath("pinning.log"));
        // 카드는 투명하다. 뒤 배경화면이 어두우면 밝은 글자, 밝으면 어두운 글자를 쓴다.
        _wallpaperTone = new WallpaperTone(this);
        _wallpaperTone.DarkChanged += _ => ApplyTheme();
        ApplyTheme();

        // 변경 알림(§2.3): MCP가 쓰면 신호로 즉시, 신호를 놓쳐도 data_version으로 30초 안에 다시 읽는다.
        _watcher = new DataVersionWatcher(database);
        _watcher.HasChanged();
        _signalRegistration = ThreadPool.RegisterWaitForSingleObject(_signal.Handle,
            (_, _) => Dispatcher.BeginInvoke(RefreshFromOutside), null, Timeout.Infinite, false);
        _fallbackTimer.Tick += (_, _) => { if (_watcher.HasChanged()) _viewModel.Refresh(); };
        _fallbackTimer.Start();
        Activated += (_, _) => { if (_watcher.HasChanged()) _viewModel.Refresh(); };

        // 날짜가 바뀌면 오늘 표시를, 주간 보기에서는 현재 시각 선을 옮긴다.
        _clockTimer.Tick += (_, _) => { _viewModel.Refresh(); UpdateNowLine(); };
        _clockTimer.Start();

        Loaded += (_, _) => { TimeScroll.ScrollToVerticalOffset(FIRST_VISIBLE_HOUR * WeekDayPanel.HOUR_HEIGHT); UpdateNowLine(); };        NowLayer.SizeChanged += (_, _) => UpdateNowLine();
        // 창이 닫혀도 앱은 새 창을 띄우며 계속 살아 있으므로(App.ShowCalendar) 타이머까지 모두 멈춘다.
        Closed += (_, _) =>
        {
            _isClosed = true;
            _fallbackTimer.Stop();
            _clockTimer.Stop();
            _signalRegistration.Unregister(null);
            _signal.Dispose();
            _watcher.Dispose();
            _pinning.Dispose();
        };
    }

    private void RefreshFromOutside()
    {
        if (_isClosed) return; // 닫히기 직전에 들어온 신호
        _watcher.HasChanged(); // 신호로 이미 반영한 변경을 30초 확인에서 다시 세지 않도록 기준값을 옮긴다.
        _viewModel.Refresh();
    }

    // 더블클릭으로 추가·수정(§6.5). 한 번 클릭은 아무 동작 없음. 누른 곳에서 위로 올라가며 처음 만나는 항목을 연다.
    protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseLeftButtonDown(e);
        OpenEditorAt(e);
    }

    private void OpenEditorAt(MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2) return;
        for (var node = e.OriginalSource as DependencyObject; node is not null; node = Parent(node))
        {
            if (node is ButtonBase or TextBoxBase) return; // 체크박스·버튼·입력란은 그대로 둔다
            if (node is not FrameworkElement { DataContext: var data } element) continue;
            var editor = data switch
            {
                ChipModel chip => Open(chip.EventId, chip.OccurrenceId, chip.TodoId),
                WeekBlockModel block => Open(block.Chip.EventId, block.Chip.OccurrenceId, block.Chip.TodoId),
                SidebarItemViewModel item => Open(item.EventId, item.OccurrenceId, item.TodoId),
                DayCellModel cell => EditorModel.NewEvent(cell.Date, null),
                // 시간표 열이면 누른 위치의 30분 단위 시각으로, 종일 줄이면 종일 일정으로 만든다.
                WeekDayModel day when IsInside(element, TimeScroll) => EditorModel.NewEvent(day.Date, SlotAt(e.GetPosition(element).Y)),
                WeekDayModel day => EditorModel.NewEvent(day.Date, null),
                _ => null
            };
            if (editor is null) continue;
            e.Handled = true;
            ShowEditor(editor);
            return;
        }

        static DependencyObject? Parent(DependencyObject node) =>
            node is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
        static bool IsInside(DependencyObject node, DependencyObject ancestor) =>
            node is Visual visual && ancestor is Visual target && visual.IsDescendantOf(target);
        static TimeOnly SlotAt(double y)
        {
            var slot = Math.Clamp((int)(y / WeekDayPanel.HOUR_HEIGHT * 2), 0, 47);
            return new TimeOnly(slot / 2, slot % 2 * 30);
        }
    }

    private EditorModel? Open(string? eventId, string? occurrenceId, string? todoId) =>
        todoId is not null ? EditorModel.FromTodo(_todos.Get(todoId))
        : eventId is not null ? EditorModel.FromEvent(_events.Get(eventId), occurrenceId)
        : null;

    // 편집 창이 떠 있는 동안은 Z순서 고정을 멈춘다(§3.3 Editing). 저장·삭제했으면 바로 다시 그린다.
    private void ShowEditor(EditorModel editor)
    {
        _pinning.BeginEditing();
        try
        {
            var dialog = new EditDialog(editor, _events, _todos, ReadCategories());
            dialog.CenterOver(this);
            if (dialog.ShowDialog() != true) return;
            _watcher.HasChanged();
            _viewModel.Refresh();
        }
        finally
        {
            _pinning.EndEditing();
        }
    }

    private IReadOnlyList<CategoryOption> ReadCategories()
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT color_id, name, color_hex FROM category ORDER BY CAST(color_id AS INTEGER), color_id;";
        using var reader = command.ExecuteReader();
        var result = new List<CategoryOption>();
        while (reader.Read()) result.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        return result;
    }

    // 주 모니터 작업 영역 가운데(§3.3). 위치 조정·저장은 M3.
    private void PlaceWindow()
    {
        var workArea = SystemParameters.WorkArea;
        // 작은 화면(노트북)에서는 작업 영역 안에 들어가도록 줄인다.
        Width = Math.Max(MinWidth, Math.Min(Width, workArea.Width));
        Height = Math.Max(MinHeight, Math.Min(Height, workArea.Height));
        Left =workArea.Left + (workArea.Width - Width) / 2;
        Top = workArea.Top + (workArea.Height - Height) / 2;
    }

    private void BuildWeekdayHeader()
    {
        for (var day = 0; day < 7; day++)
        {
            var label = new TextBlock
            {
                Text = WeekdayNames[day], Style = (Style)FindResource("BaseText"), FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
            };
            label.SetResourceReference(TextBlock.ForegroundProperty, day == 0 ? "SundayBrush" : day == 6 ? "SaturdayBrush" : "SubtleBrush");
            var cell = new Border { Child = label, BorderThickness = new Thickness(0, 0, day == 6 ? 0 : 1, 1) };
            cell.SetResourceReference(Border.BorderBrushProperty, "GridLineBrush");
            WeekdayHeader.Children.Add(cell);
        }
    }

    private void BuildHourGrid()
    {
        var hourHeight = WeekDayPanel.HOUR_HEIGHT;
        HourLabels.Height = HourLines.Height = hourHeight * 24;
        for (var hour = 0; hour < 24; hour++)
        {
            var label = new TextBlock
            {
                Text = $"{hour:00}:00", Style = (Style)FindResource("BaseText"), FontSize = 9,
                Width = 44, TextAlignment = TextAlignment.Center
            };
            label.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
            Canvas.SetTop(label, hour * hourHeight + 3);
            HourLabels.Children.Add(label);
            var line = new Border { Height = 1 };
            line.SetResourceReference(Border.BackgroundProperty, "GridLineBrush");
            line.SetBinding(WidthProperty, new System.Windows.Data.Binding(nameof(ActualWidth)) { Source = HourLines });
            Canvas.SetTop(line, hour * hourHeight);
            HourLines.Children.Add(line);
        }
    }

    private void UpdateNowLine()
    {
        var week = _viewModel.Week;
        var todayIndex = week?.Days.ToList().FindIndex(d => d.IsToday) ?? -1;
        if (todayIndex < 0)
        {
            NowLine.Visibility = Visibility.Collapsed;
            return;
        }
        var now = DateTime.Now;
        var columnWidth = NowLayer.ActualWidth / 7;
        NowLine.Width = columnWidth;
        Canvas.SetLeft(NowLine, columnWidth * todayIndex);
        Canvas.SetTop(NowLine, (now.Hour + now.Minute / 60.0) * WeekDayPanel.HOUR_HEIGHT - NowLine.Height / 2);
        NowLine.Visibility = Visibility.Visible;
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.IsMonthView)) UpdateViewToggle();
        if (e.PropertyName == nameof(MainViewModel.Week)) UpdateNowLine();
    }

    private void UpdateViewToggle()
    {
        Style(MonthTabBox, MonthTabText, _viewModel.IsMonthView);
        Style(WeekTabBox, WeekTabText, !_viewModel.IsMonthView);

        void Style(Border box, TextBlock text, bool selected)
        {
            if (selected) box.SetResourceReference(Border.BackgroundProperty, "ToggleThumbBrush");
            else box.Background = Brushes.Transparent;
            box.Effect = selected ? new System.Windows.Media.Effects.DropShadowEffect
                { Color = Color.FromRgb(0x26, 0x33, 0x59), Opacity = 0.12, BlurRadius = 3, ShadowDepth = 1, Direction = 270 } : null;
            text.FontWeight = selected ? FontWeights.Bold : FontWeights.Medium;
            text.SetResourceReference(TextBlock.ForegroundProperty, selected ? "TextBrush" : "SubtleBrush");
        }
    }

    // +N: 그날 항목 전체를 작은 목록으로 보여준다(§6.3).
    private void OnOverflowClick(object sender, RoutedEventArgs e)
    {
        var cell = (DayCellModel)((FrameworkElement)sender).DataContext;
        var list = new ItemsControl { ItemsSource = cell.AllItems, ItemTemplate = (DataTemplate)FindResource("ChipTemplate") };
        var card = new Border
        {
            Width = 180, Padding = new Thickness(10, 6, 10, 10), CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1),
            Child = new StackPanel
            {
                Children =
                {
                    new TextBlock { Text = $"{cell.Date.Month}월 {cell.Date.Day}일", Style = (Style)FindResource("BaseText"),
                        FontWeight = FontWeights.Bold, FontSize = 11 },
                    list
                }
            }
        };
        card.SetResourceReference(Border.BackgroundProperty, "PopupBrush");
        card.SetResourceReference(Border.BorderBrushProperty, "ControlBorderBrush");
        var popup = new Popup { Child = card, PlacementTarget = (UIElement)sender, Placement = PlacementMode.Bottom, StaysOpen = false };
        // 팝업은 창과 다른 시각 트리라 창의 더블클릭 처리가 닿지 않는다. 목록의 칩도 더블클릭으로 연다.
        card.PreviewMouseLeftButtonDown += (_, args) =>
        {
            if (args.ClickCount != 2) return;
            popup.IsOpen = false;
            OpenEditorAt(args);
        };
        popup.IsOpen = true;
    }

    private void OnMenuClick(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = (UIElement)sender, Placement = PlacementMode.Top };
        menu.Items.Add(MenuItem("종료", () => { ExitRequested = true; Close(); }));
        // 메뉴가 열린 동안에는 창이 활성화되므로 Z순서 고정을 잠시 멈춘다.
        menu.Opened += (_, _) => _pinning.BeginEditing();
        menu.Closed += (_, _) => _pinning.EndEditing();
        menu.IsOpen = true;

        static MenuItem MenuItem(string header, Action action)
        {
            var item = new MenuItem { Header = header };
            item.Click += (_, _) => action();
            return item;
        }
    }

    // 색 사전(App.xaml의 첫 번째 병합 사전)을 배경화면 밝기에 맞춰 갈아 끼우고 유리 막을 다시 칠한다.
    private void ApplyTheme()
    {
        var colors = new ResourceDictionary { Source = new Uri(_wallpaperTone.IsDark ? "Colors.Dark.xaml" : "Colors.Light.xaml", UriKind.Relative) };
        Application.Current.Resources.MergedDictionaries[0] = colors;
        ApplyGlass();
    }

    private void ApplyGlass()
    {
        // 바탕화면이 흐릿하게 비치는 반투명 유리 막(아이콘이 뒤에 있으면 어둡게 비친다). 위쪽 광택과 아래쪽 반사광으로 유리를 씌운 느낌을 낸다.
        var tint = (Color)FindResource("GlassTintColor");
        const byte alpha = 0xA6;
        var plain = Color.FromArgb(alpha, tint.R, tint.G, tint.B);
        var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        brush.GradientStops.Add(new GradientStop(Blend(tint, alpha, 0x2E), 0));
        brush.GradientStops.Add(new GradientStop(plain, 0.3));
        brush.GradientStops.Add(new GradientStop(plain, 0.9));
        brush.GradientStops.Add(new GradientStop(Blend(tint, alpha, 0x14), 1));
        CalendarCard.Background = brush;
        SidebarCard.Background = brush;

        // 막 색 위에 흰 광택(gloss 알파)을 얹은 색
        static Color Blend(Color tint, byte alpha, byte gloss)
        {
            var total = alpha + gloss * (255 - alpha) / 255.0;
            byte Mix(byte channel) => (byte)((channel * alpha + 255 * gloss * (255 - alpha) / 255.0) / total);
            return Color.FromArgb((byte)total, Mix(tint.R), Mix(tint.G), Mix(tint.B));
        }
    }

    // 창은 평소 활성화되지 않으므로(WS_EX_NOACTIVATE) 입력란을 누를 때만 편집 상태로 바꿔 키보드를 받는다.
    private void OnInputMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_pinning.State == PinState.Editing) return;
        _pinning.BeginEditing();
        Activate();
        NewTodoBox.Focus();
    }

    private void OnInputBlur(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (_pinning.State == PinState.Editing) _pinning.EndEditing();
    }

    private static string LogPath(string name)
    {
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeskCal", "logs");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, name);
    }
}
