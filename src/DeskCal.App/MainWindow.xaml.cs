using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using DeskCal.App.Calendar;
using DeskCal.Core;

namespace DeskCal.App;

public partial class MainWindow : Window
{
    private const double SHADOW_MARGIN = 24;
    private const int FIRST_VISIBLE_HOUR = 8;
    private const double ADJUST_EDGE = 10;
    private const int WM_NCHITTEST = 0x0084;
    private const int HTCAPTION = 2, HTLEFT = 10, HTRIGHT = 11, HTTOP = 12, HTTOPLEFT = 13, HTTOPRIGHT = 14,
        HTBOTTOM = 15, HTBOTTOMLEFT = 16, HTBOTTOMRIGHT = 17;
    private static readonly string[] WeekdayNames = ["일", "월", "화", "수", "목", "금", "토"];

    private readonly MainViewModel _viewModel;
    private readonly Database _database;
    private readonly EventRepository _events;
    private readonly TodoRepository _todos;
    private readonly SettingStore _settings;
    private readonly DesktopPinning _pinning;
    private readonly WallpaperTone _wallpaperTone;
    private readonly DataVersionWatcher _watcher;
    private readonly ChangeSignal _signal = new();
    private readonly RegisteredWaitHandle _signalRegistration;
    private readonly DispatcherTimer _fallbackTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    private readonly DispatcherTimer _clockTimer = new() { Interval = TimeSpan.FromMinutes(1) };
    private bool _isClosed;

    /// <summary>사용자가 ⋯ 메뉴·트레이로 종료했는지. 아니면 창이 닫혀도 앱이 다시 띄운다.</summary>
    public bool ExitRequested { get; private set; }

    public bool IsAdjusting { get; private set; }

    public void Exit()
    {
        ExitRequested = true;
        Close();
    }

    public MainWindow(Database database)
    {
        InitializeComponent();
        _database = database;
        _events = new EventRepository(database);
        _todos = new TodoRepository(database);
        _settings = new SettingStore(database);
        _viewModel = new MainViewModel(database);
        DataContext = _viewModel;
        PlaceWindow();
        SourceInitialized += (_, _) => HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)!.AddHook(AdjustHook);
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
        // 체크박스·버튼·입력란 안쪽(체크 표시, 테두리 등)을 누른 경우는 그대로 둔다.
        for (var node = e.OriginalSource as DependencyObject; node is not null; node = Parent(node))
            if (node is ButtonBase or TextBoxBase) return;
        for (var node = e.OriginalSource as DependencyObject; node is not null; node = Parent(node))
        {
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

    // 반복 회차는 시리즈 기록으로 연다. 완료 등으로 따로 저장된 회차의 기록을 열면 그 날짜가 시리즈 시작일로 저장되기 때문이다.
    private EditorModel? Open(string? eventId, string? occurrenceId, string? todoId) =>
        todoId is not null ? EditorModel.FromTodo(_todos.Get(todoId))
        : occurrenceId is not null ? EditorModel.FromEvent(_events.Get(OccurrenceId.Parse(occurrenceId).EventUid), occurrenceId)
        : eventId is not null ? EditorModel.FromEvent(_events.Get(eventId), null)
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
            EndEditing();
        }
    }

    // 창이 열린 사이 트레이로 조정 모드를 켰으면 고정 해제를 유지한다. 조정 모드가 끝날 때 되돌린다.
    private void EndEditing()
    {
        if (!IsAdjusting) _pinning.EndEditing();
    }

    // 카테고리 이름이 바뀌면 칩·사이드바의 이름을 다시 그린다.
    // 트레이 메뉴는 다른 창이 모달로 떠 있어도 열리므로, 열린 창이 있으면 그 창을 앞으로 가져오기만 한다.
    public void ShowSettings()
    {
        if (IsAdjusting) return;
        var openDialog = Application.Current.Windows.OfType<Window>().FirstOrDefault(window => window != this && window.IsVisible);
        if (openDialog is not null)
        {
            openDialog.Activate();
            return;
        }
        _pinning.BeginEditing();
        try
        {
            var dialog = new SettingsDialog(new CategoryRepository(_database));
            dialog.PlaceOver(this);
            if (dialog.ShowDialog() == true) _viewModel.Refresh();
        }
        finally
        {
            EndEditing();
        }
    }

    private void OnSearchClick(object sender, RoutedEventArgs e) => ShowSearch();

    // Ctrl+F는 달력 창이 키보드를 받고 있을 때(할 일 입력란 등)만 온다. 평소에는 창이 활성화되지 않기 때문이다.
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (IsAdjusting && e.Key is Key.Enter or Key.Escape)
        {
            e.Handled = true;
            ToggleAdjust();
            return;
        }
        if (IsAdjusting || e.Key != Key.F || Keyboard.Modifiers != ModifierKeys.Control) return;
        e.Handled = true;
        ShowSearch();
    }

    // 고른 결과의 날짜로 이동한다. 마감일 없는 할 일은 갈 날짜가 없으므로 편집 창을 연다.
    private void ShowSearch()
    {
        _pinning.BeginEditing();
        SearchHit? hit;
        try
        {
            var dialog = new SearchDialog(new SearchService(_events, _todos), ReadCategories(), DateOnly.FromDateTime(DateTime.Now));
            dialog.PlaceOver(this);
            if (dialog.ShowDialog() != true) return;
            hit = dialog.SelectedHit;
        }
        finally
        {
            EndEditing();
        }
        if (hit is null) return;
        if (hit.Date is { } date) _viewModel.GoTo(date);
        else ShowEditor(EditorModel.FromTodo(_todos.Get(hit.Uid)));
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

    // 조정 모드에서 저장한 자리가 있으면 그대로, 없거나 화면 밖으로 벗어났으면 주 모니터 작업 영역 가운데(§3.3).
    private void PlaceWindow()
    {
        var saved = _settings.Get(BoundsKey())?.Split(',')
            .Select(part => double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : double.NaN).ToArray();
        if (saved is [var left, var top, var width, var height] && !saved.Any(double.IsNaN))
        {
            var screen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
            var visible = Rect.Intersect(new Rect(left, top, Math.Max(width, MinWidth), Math.Max(height, MinHeight)), screen);
            if (visible.Width >= 200 && visible.Height >= 200)
            {
                (Left, Top, Width, Height) = (left, top, Math.Max(width, MinWidth), Math.Max(height, MinHeight));
                return;
            }
        }
        var workArea = SystemParameters.WorkArea;
        // 작은 화면(노트북)에서는 작업 영역 안에 들어가도록 줄인다.
        Width = Math.Max(MinWidth, Math.Min(Width, workArea.Width));
        Height = Math.Max(MinHeight, Math.Min(Height, workArea.Height));
        Left =workArea.Left + (workArea.Width - Width) / 2;
        Top = workArea.Top + (workArea.Height - Height) / 2;
    }

    // 크기·위치는 화면 구성(모니터마다의 영역, 픽셀)마다 따로 저장한다. 노트북 단독일 때와 모니터를 연결했을 때 자리가 다르기 때문이다.
    // 전체 외곽만 보면 배치가 바뀌어도 같은 키가 되어 모니터 사이 빈 곳을 복원할 수 있다.
    private static string BoundsKey() => "window_bounds:" + string.Join(";", System.Windows.Forms.Screen.AllScreens
        .Select(screen => screen.Bounds).OrderBy(b => b.X).ThenBy(b => b.Y)
        .Select(b => string.Create(CultureInfo.InvariantCulture, $"{b.X},{b.Y},{b.Width},{b.Height}")));

    // 조정 모드(§3.3): 켜진 동안만 다른 앱 위로 꺼내 옮기고 크기를 바꿀 수 있다. 끝나면 자리를 저장하고 바탕화면으로 돌려보낸다.
    public void ToggleAdjust()
    {
        if (IsAdjusting)
        {
            IsAdjusting = false;
            AdjustOverlay.Visibility = Visibility.Collapsed;
            ResizeMode = ResizeMode.NoResize;
            _settings.Set(BoundsKey(), string.Create(CultureInfo.InvariantCulture, $"{Left},{Top},{ActualWidth},{ActualHeight}"));
            _pinning.EndEditing();
            return;
        }
        IsAdjusting = true;
        AdjustOverlay.Visibility = Visibility.Visible;
        // 테두리 없는 창도 가장자리로 크기를 바꾸려면 크기 조절 스타일(WS_THICKFRAME)이 있어야 한다.
        ResizeMode = ResizeMode.CanResize;
        _pinning.BeginEditing(bringToFront: true);
        Activate();
    }

    private void OnAdjustDone(object sender, RoutedEventArgs e) => ToggleAdjust();

    // 카드 안쪽 가장자리 10px는 크기 조절, 나머지 안쪽은 끌어서 옮기기. 그림자 여백과 완료 버튼은 평소처럼 둔다.
    private IntPtr AdjustHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_NCHITTEST || !IsAdjusting) return IntPtr.Zero;
        var value = lParam.ToInt64();
        var point = PointFromScreen(new Point((short)(value & 0xFFFF), (short)((value >> 16) & 0xFFFF)));
        var card = new Rect(SHADOW_MARGIN, SHADOW_MARGIN, ActualWidth - 2 * SHADOW_MARGIN, ActualHeight - 2 * SHADOW_MARGIN);
        if (!card.Contains(point) || InputHitTest(point) is Visual hit && hit.IsDescendantOf(AdjustDoneButton)) return IntPtr.Zero;
        var left = point.X < card.Left + ADJUST_EDGE;
        var right = point.X > card.Right - ADJUST_EDGE;
        var top = point.Y < card.Top + ADJUST_EDGE;
        var bottom = point.Y > card.Bottom - ADJUST_EDGE;
        handled = true;
        return new IntPtr((top, bottom, left, right) switch
        {
            (true, _, true, _) => HTTOPLEFT,
            (true, _, _, true) => HTTOPRIGHT,
            (_, true, true, _) => HTBOTTOMLEFT,
            (_, true, _, true) => HTBOTTOMRIGHT,
            (true, _, _, _) => HTTOP,
            (_, true, _, _) => HTBOTTOM,
            (_, _, true, _) => HTLEFT,
            (_, _, _, true) => HTRIGHT,
            _ => HTCAPTION
        });
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
        // 설정 창은 메뉴가 닫힌(편집 상태가 풀린) 뒤에 연다.
        menu.Items.Add(MenuItem("설정", () => Dispatcher.BeginInvoke(ShowSettings)));
        menu.Items.Add(MenuItem("크기·위치 조정", ToggleAdjust));
        menu.Items.Add(MenuItem("종료", Exit));
        // 메뉴가 열린 동안에는 창이 활성화되므로 Z순서 고정을 잠시 멈춘다. 조정 모드를 켰으면 끝날 때까지 그대로 둔다.
        menu.Opened += (_, _) => _pinning.BeginEditing();
        menu.Closed += (_, _) => EndEditing();
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
        if (_pinning.State == PinState.Editing) EndEditing();
    }

    private static string LogPath(string name)
    {
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeskCal", "logs");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, name);
    }
}
