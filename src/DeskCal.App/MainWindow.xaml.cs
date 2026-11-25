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
    private const int WM_WINDOWPOSCHANGED = 0x0047;
    private const int HTCAPTION = 2, HTLEFT = 10, HTRIGHT = 11, HTTOP = 12, HTTOPLEFT = 13, HTTOPRIGHT = 14,
        HTBOTTOM = 15, HTBOTTOMLEFT = 16, HTBOTTOMRIGHT = 17;
    private const uint SWP_NOSIZE = 0x0001, SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010;
    private static readonly string[] WeekdayNames = ["월", "화", "수", "목", "금", "토", "일"];

    private readonly MainViewModel _viewModel;
    private readonly Database _database;
    private readonly EventRepository _events;
    private readonly TodoRepository _todos;
    private readonly SettingStore _settings;
    private readonly DesktopPinning _pinning;
    private readonly WallpaperTone _wallpaperTone;
    private readonly DataVersionWatcher _watcher;
    private readonly SidebarTitleToolTip _sidebarTip;
    private bool _sidebarOwnsEditing;
    private readonly ChangeSignal _signal = new();
    private readonly RegisteredWaitHandle _signalRegistration;
    private readonly DispatcherTimer _fallbackTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    private readonly DispatcherTimer _clockTimer = new() { Interval = TimeSpan.FromMinutes(1) };
    private readonly DispatcherTimer _displayTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool _isClosed;
    private BlurBackdrop? _blur;

    /// <summary>사용자가 ⋯ 메뉴·트레이로 종료했는지. 아니면 창이 닫혀도 앱이 다시 띄운다.</summary>
    public bool ExitRequested { get; private set; }

    public bool IsAdjusting { get; private set; }

    /// <summary>다른 프로세스(MCP)가 DB를 바꿨다는 신호를 받았다. 앱은 이때 올릴 변경이 있으면 바로 동기화한다.</summary>
    public event Action? ExternalChange;

    public void Exit()
    {
        ExitRequested = true;
        Close();
    }

    public MainWindow(Database database)
    {
        InitializeComponent();
        _sidebarTip = new SidebarTitleToolTip((ToolTip)FindResource("SidebarTitleTip"));
        _database = database;
        _events = new EventRepository(database);
        _todos = new TodoRepository(database);
        _settings = new SettingStore(database);
        TextSize.Apply(Application.Current.Resources, _settings.TextSizePercent);
        _viewModel = new MainViewModel(database);
        DataContext = _viewModel;
        // 자리는 물리 픽셀로 옮기므로 창 핸들이 생긴 뒤(보이기 전)에 놓는다.
        SourceInitialized += (_, _) =>
        {
            HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)!.AddHook(AdjustHook);
            HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)!.AddHook(BlurHook);
            PlaceWindow();
            // 흐림 창은 바탕화면 고정(DesktopPinning, 이 다음 SourceInitialized 처리)보다 먼저 있어야 그 창을 바탕화면에 붙인다.
            _blur = BlurBackdrop.TryCreate(message => File.AppendAllText(LogPath("blur.log"), $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}"));
        };
        BuildWeekdayHeader();
        BuildHourGrid();
        UpdateViewToggle();
        ApplySidebar();
        _viewModel.PropertyChanged += OnViewModelChanged;

        _pinning = new DesktopPinning(this, LogPath("pinning.log"), () => _blur?.Handle ?? IntPtr.Zero);
        // 카드는 투명하다. 뒤 배경화면이 어두우면 밝은 글자, 밝으면 어두운 글자를 쓴다.
        _wallpaperTone = new WallpaperTone(this);
        _wallpaperTone.DarkChanged += _ => ApplyTheme();
        ApplyTheme();

        // 변경 알림(§2.3): MCP가 쓰면 신호로 즉시, 신호를 놓쳐도 data_version으로 30초 안에 다시 읽는다.
        _watcher = new DataVersionWatcher(database);
        _watcher.HasChanged();
        _signalRegistration = ThreadPool.RegisterWaitForSingleObject(_signal.Handle,
            (_, _) => Dispatcher.BeginInvoke(RefreshFromOutside), null, Timeout.Infinite, false);
        _fallbackTimer.Tick += (_, _) => { if (_watcher.HasChanged()) _viewModel.Refresh(); UpdateSyncStatus(); };
        _fallbackTimer.Start();
        Deactivated += (_, _) =>
        {
            _sidebarTip.Dismiss();
            if (_sidebarOwnsEditing) EndEditing();
        };
        Activated += (_, _) => { if (_watcher.HasChanged()) _viewModel.Refresh(); };
        UpdateSyncStatus();

        // 날짜가 바뀌면 오늘 표시를, 주간 보기에서는 현재 시각 선을 옮긴다.
        _clockTimer.Tick += (_, _) => { _viewModel.Refresh(); UpdateNowLine(); };
        _clockTimer.Start();

        // 켜 둔 채로 모니터를 연결·해제하면 그 화면 구성의 자리로 옮긴다. 알림이 연달아 오므로 1초 동안 조용해지면 한 번만 한다.
        _displayTimer.Tick += (_, _) =>
        {
            _displayTimer.Stop();
            if (!IsAdjusting) PlaceWindow();
        };
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;

        Loaded += (_, _) => { TimeScroll.ScrollToVerticalOffset(FIRST_VISIBLE_HOUR * WeekDayPanel.HOUR_HEIGHT); UpdateNowLine(); };
        // 방식 C(바탕화면에 못 붙음)에서는 흐림 창이 다른 앱 위에 뜰 수 있으므로 쓰지 않는다.
        Loaded += (_, _) =>
        {
            if (_blur is not null && !_pinning.IsAttached)
            {
                _blur.Dispose();
                _blur = null;
            }
            ApplyGlass();
            UpdateBlur();
        };
        CalendarCard.SizeChanged += (_, _) => UpdateBlur();
        SidebarCard.SizeChanged += (_, _) => UpdateBlur();
        DpiChanged += (_, _) => UpdateBlur();        NowLayer.SizeChanged += (_, _) => UpdateNowLine();
        // 창이 닫혀도 앱은 새 창을 띄우며 계속 살아 있으므로(App.ShowCalendar) 타이머까지 모두 멈춘다.
        Closed += (_, _) =>
        {
            _isClosed = true;
            _sidebarTip.Dismiss();
            _fallbackTimer.Stop();
            _clockTimer.Stop();
            _displayTimer.Stop();
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
            _signalRegistration.Unregister(null);
            _signal.Dispose();
            _watcher.Dispose();
            _pinning.Dispose();
            _blur?.Dispose();
        };
    }

    // 창이 움직이거나 크기가 바뀌면 흐림 창도 같은 자리·카드 모양으로 맞춘다.
    private IntPtr BlurHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_WINDOWPOSCHANGED) UpdateBlur();
        return IntPtr.Zero;
    }

    private void UpdateBlur()
    {
        if (_blur is null || !IsLoaded) return;
        GetWindowRect(new WindowInteropHelper(this).Handle, out var rect);
        var scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var cards = new[] { CalendarCard, SidebarCard }.Where(card => card.Visibility == Visibility.Visible).Select(card =>
        {
            var bounds = card.TransformToAncestor(this).TransformBounds(new Rect(card.RenderSize));
            return new Rect(bounds.X * scale, bounds.Y * scale, bounds.Width * scale, bounds.Height * scale);
        }).ToList();
        _blur.Place(new Int32Rect(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top), cards,
            (float)(CalendarCard.CornerRadius.TopLeft * scale));
    }

    // SystemEvents는 자기 스레드에서 알릴 수 있으므로 UI 스레드로 넘긴다.
    private void OnDisplaySettingsChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(() =>
    {
        if (_isClosed) return;
        _displayTimer.Stop();
        _displayTimer.Start();
    });

    // 트레이에서 로그인하면 30초 안에 바뀐다. 동기화는 2분마다 하므로 마지막으로 받은 시각을 보여 준다.
    // 토큰이 없는데 계정 결합 정보가 있으면 만료·취소로 끊긴 것이다(GoogleAccount.NeedsReconnect와 같은 기준).
    // 기기 사이 설정 공유(카테고리 별칭 등, S6)를 Drive에서 못 하면 그 이유를 덧붙인다. Calendar·Tasks 동기화와는 따로다.
    private void UpdateSyncStatus()
    {
        if (_settings.Get(ProtectedTokenStore.SettingKey) is null)
        {
            SyncStatusText.Text = _settings.Get("google_account_id") is null ? "Google 연결 전 · 이 PC에만 저장" : "Google 연결 끊김 · 알림 영역 메뉴에서 다시 연결";
            return;
        }
        // 토큰은 있는데 invalid_grant 말고 4xx로 계속 실패한다(OAuth 클라이언트가 바뀜 등, S4). "연결됨"으로 두지 않는다.
        if (GoogleAccount.ReadAuthError(_database) is not null)
        {
            SyncStatusText.Text = "Google 연결 오류 · 알림 영역 메뉴에서 다시 연결";
            return;
        }
        var text = EventSync.LastSyncedAt(_database) is { } last ? $"Calendar 동기화 · {last.ToLocalTime():HH:mm}" : "Google 연결됨 · 동기화 준비 중";
        text = SharedSettingsSync.UnavailableReason(_database) switch
        {
            SharedSettingsSync.ScopeMissing => text + " · 설정 공유에 Drive 권한 필요",
            SharedSettingsSync.ApiDisabled => text + " · 설정 공유에 Drive API 필요",
            _ => text
        };
        // 확인하지 않은 충돌(S4)은 여기 한 줄로만 알리고, 상세는 눌러서 연다.
        SyncStatusText.Text = ConflictLog.UnseenCount(_database) is var conflicts and > 0 ? $"{text} · 충돌 {conflicts}건 확인" : text;
    }

    private void OnSyncDetails(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = (UIElement)sender, Placement = PlacementMode.Top };
        AddConflicts(menu);
        var recovery = new TaskRecovery(_database);
        IReadOnlyList<TaskRecovery.Candidate>? candidates = null;
        foreach (var item in PendingChanges.List(_database))
        {
            var reason = item.Reason switch
            {
                "needs_review" => "전송 결과 확인 필요",
                "not_signed_in" => "Google 로그인 대기",
                "push_failed" => "전송 실패 · 재시도 대기",
                SharedSettingsSync.ScopeMissing => "Drive 권한 없음 · 알림 영역 메뉴에서 Google 다시 연결",
                SharedSettingsSync.ApiDisabled => "Google Cloud 프로젝트에서 Drive API를 켜야 함",
                _ => "전송 대기"
            };
            var row = new MenuItem { Header = $"{item.Kind} · {item.Title} · {reason}", IsEnabled = item.Reason == "needs_review" };
            menu.Items.Add(row);
            if (!row.IsEnabled) continue;
            var original = _todos.Get(item.Uid);
            row.ToolTip = $"{original.DueDate} {original.Memo} · {original.TodoUid}";
            foreach (var candidate in (candidates ??= recovery.Candidates()).OrderByDescending(c => c.Title == original.Title))
            {
                var choice = new MenuItem { Header = $"연결 · {candidate.Title} · {candidate.DueDate ?? "날짜 없음"} · {candidate.Uid[..8]}",
                    ToolTip = candidate.Memo };
                choice.Click += async (_, _) => await Resolve(candidate);
                row.Items.Add(choice);
            }
            var retry = new MenuItem { Header = "새 할 일로 다시 전송…" };
            retry.Click += async (_, _) => await Resolve(null);
            row.Items.Add(retry);

            async Task Resolve(TaskRecovery.Candidate? candidate)
            {
                var message = candidate is null
                    ? $"Google Tasks에 이미 ‘{original.Title}’이 있으면 중복됩니다. 새 할 일로 다시 전송할까요?"
                    : $"‘{original.Title}’을 Google의 ‘{candidate.Title}’과 연결할까요?\n\nGoogle의 제목·마감·완료·메모를 받습니다. 이 PC의 내용이 다르면 충돌 기록에 보관합니다.\n\nGoogle 메모\n{candidate.Memo}";
                if (MessageBox.Show(this, message, "할 일 복구", MessageBoxButton.YesNo, MessageBoxImage.Question,
                        MessageBoxResult.No) != MessageBoxResult.Yes) return;
                if (await recovery.ResolveAsync(item.Uid, candidate))
                {
                    _viewModel.Refresh();
                    ExternalChange?.Invoke();
                }
                else MessageBox.Show(this, "동기화 중이거나 후보가 변경되었습니다. 상태 상세를 다시 열어 주세요.", "할 일 복구");
            }
        }
        if (menu.Items.Count == 0 || menu.Items[^1] is Separator) menu.Items.Add(new MenuItem { Header = "전송 대기 항목이 없습니다.", IsEnabled = false });
        menu.Opened += (_, _) => BeginDialogEditing();
        menu.Closed += (_, _) => EndEditing();
        menu.IsOpen = true;
    }

    // 확인하지 않은 충돌(S4). 무엇이 겹쳐 어느 값을 남겼는지 보이고, 이 PC 값을 다시 적용하거나 확인으로 숨긴다.
    private void AddConflicts(ContextMenu menu)
    {
        var conflicts = ConflictLog.Unseen(_database);
        foreach (var conflict in conflicts)
        {
            var kind = conflict.Kind switch { "event" => "일정", "todo" => "할 일", _ => "설정" };
            var row = new MenuItem
            {
                Header = $"충돌 · {kind} ‘{conflict.Title}’ · {conflict.Summary}",
                ToolTip = conflict.Fields.Count == 0 ? null
                    : string.Join("\n", conflict.Fields.Select(f => $"{f.Label}: 이 PC ‘{f.Mine}’ → 남긴 값 ‘{f.Kept}’"))
            };
            if (conflict.CanReapply)
            {
                var reapply = new MenuItem { Header = conflict.Deleted == "local" ? "다시 지우기" : "이 PC 값 다시 적용" };
                reapply.Click += (_, _) =>
                {
                    try { ConflictLog.Reapply(_database, conflict.Id); }
                    catch (Exception error) when (error is ArgumentException or InvalidOperationException or KeyNotFoundException)
                    {
                        MessageBox.Show(this, $"다시 적용하지 못했습니다.\n{error.Message}", "충돌 기록");
                        return;
                    }
                    _viewModel.Refresh();
                    UpdateSyncStatus();
                    ExternalChange?.Invoke(); // 바로 보내도록 빠른 동기화를 건다
                };
                row.Items.Add(reapply);
            }
            var seen = new MenuItem { Header = "확인" };
            seen.Click += (_, _) =>
            {
                ConflictLog.MarkSeen(_database, conflict.Id);
                UpdateSyncStatus();
            };
            row.Items.Add(seen);
            menu.Items.Add(row);
        }
        if (conflicts.Count > 1)
        {
            var all = new MenuItem { Header = "충돌 모두 확인" };
            all.Click += (_, _) =>
            {
                ConflictLog.MarkAllSeen(_database);
                UpdateSyncStatus();
            };
            menu.Items.Add(all);
        }
        if (conflicts.Count > 0) menu.Items.Add(new Separator());
    }

    private void RefreshFromOutside()
    {
        if (_isClosed) return; // 닫히기 직전에 들어온 신호
        _watcher.HasChanged(); // 신호로 이미 반영한 변경을 30초 확인에서 다시 세지 않도록 기준값을 옮긴다.
        _viewModel.Refresh();
        ExternalChange?.Invoke();
    }

    // 더블클릭으로 추가·수정(§6.5). 한 번 클릭은 아무 동작 없음. 누른 곳에서 위로 올라가며 처음 만나는 항목을 연다.
    protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseLeftButtonDown(e);
        TrackOrderPress(e, this);
        OpenEditorAt(e);
    }

    // 같은 날 일정 끌기(U6): 달력 칸·우측 목록·+N 목록. 받는 규칙은 DayOrderItem에 있다(완료 상태가 같은 단일 일정끼리).
    private DayOrderItem? _pressedEvent;
    private DayOrderItem? _draggingEvent;
    private Point _pressPoint;

    private void TrackOrderPress(MouseButtonEventArgs e, UIElement surface)
    {
        _pressedEvent = e.ClickCount == 1 ? OrderItemAt(e.OriginalSource as DependencyObject) : null;
        _pressPoint = e.GetPosition(surface);
    }

    private static DayOrderItem? OrderItemAt(DependencyObject? node)
    {
        // 체크박스 내부 도형에도 행 DataContext가 상속된다. 전체 입력 경로에서 버튼을 먼저 제외한다.
        for (var part = node; part is not null; part = Parent(part))
            if (part is ButtonBase or TextBoxBase || part is FrameworkElement { DataContext: ChecklistItemViewModel }) return null;
        for (; node is not null; node = Parent(node))
            if (node is FrameworkElement element && DayOrderItem.From(element.DataContext) is { } item) return item;
        return null;
    }

    protected override void OnPreviewMouseMove(MouseEventArgs e)
    {
        base.OnPreviewMouseMove(e);
        StartOrderDrag(e, this);
    }

    // dragging·ended: 끌기가 실제로 시작될 때와 끝날 때 부른다(+N 목록은 끄는 동안 닫히지 않게 한다).
    private void StartOrderDrag(MouseEventArgs e, UIElement surface, Action? dragging = null, Action? ended = null)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _pressedEvent is null) return;
        var delta = e.GetPosition(surface) - _pressPoint;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        _draggingEvent = _pressedEvent;
        _pressedEvent = null;
        dragging?.Invoke();
        try { DragDrop.DoDragDrop(surface, new DataObject("DeskCal.DayOrder", _draggingEvent.Key), DragDropEffects.Move); }
        finally
        {
            _draggingEvent = null;
            ended?.Invoke();
        }
    }

    protected override void OnPreviewDragOver(DragEventArgs e)
    {
        base.OnPreviewDragOver(e);
        ShowOrderDropEffect(e);
    }

    // 받는 자리면 이동, 아니면 금지 커서를 보인다. 받는 대상을 돌려준다(+N 목록이 놓을 자리 선을 긋는다).
    private DayOrderItem? ShowOrderDropEffect(DragEventArgs e)
    {
        if (_draggingEvent is not { } source) return null;
        var target = OrderItemAt(e.OriginalSource as DependencyObject);
        var accepted = source.Accepts(target);
        e.Effects = accepted ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
        return accepted ? target : null;
    }

    protected override void OnPreviewDrop(DragEventArgs e)
    {
        base.OnPreviewDrop(e);
        DropOrderItem(e);
    }

    // 다시 그리면 열린 +N 목록도 같은 날짜의 새 순서로 바뀐다(RefreshOverflow).
    private void DropOrderItem(DragEventArgs e)
    {
        if (_draggingEvent is not { } source) return;
        e.Handled = true;
        var target = OrderItemAt(e.OriginalSource as DependencyObject);
        if (source.Accepts(target) && DayEventOrder.Move(_events, target!.Day, source.Key, target.Key))
            _viewModel.Refresh();
    }

    private static DependencyObject? Parent(DependencyObject node) =>
        node is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);

    private void OpenEditorAt(MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2) return;
        // 체크박스·버튼·입력란 안쪽(체크 표시, 테두리 등)을 누른 경우는 그대로 둔다.
        for (var node = e.OriginalSource as DependencyObject; node is not null; node = Parent(node))
            if (node is ButtonBase or TextBoxBase) return;
        for (var node = e.OriginalSource as DependencyObject; node is not null; node = Parent(node))
        {
            if (node is not FrameworkElement { DataContext: var data } element) continue;
            if (data is ChecklistItemViewModel checklistItem)
            {
                e.Handled = true;
                _viewModel.BeginChecklistEdit(checklistItem);
                return;
            }
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

    private void OnChipMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var target = (FrameworkElement)sender;
        var chip = target.DataContext is WeekBlockModel block ? block.Chip : target.DataContext as ChipModel;
        if (chip is null || chip.IsPlaceholder)
        {
            e.Handled = true;
            return;
        }

        var menu = target.ContextMenu;
        var done = new MenuItem { Header = chip.DoneMenuText, StaysOpenOnClick = true };
        var edit = new MenuItem { Header = "편집…", StaysOpenOnClick = true };
        menu.Items.Clear();
        menu.Items.Add(done);
        menu.Items.Add(new Separator());
        menu.Items.Add(edit);

        var editRequested = false;
        done.Click += (_, _) =>
        {
            menu.IsOpen = false;
            _viewModel.SetDone(chip.EventId, chip.OccurrenceId, chip.TodoId, !chip.IsDone);
        };
        edit.Click += (_, _) =>
        {
            editRequested = true;
            menu.IsOpen = false;
        };
        // 메뉴가 마우스 캡처를 가져가기 전에 +N 목록의 바깥 클릭 닫기를 잠시 끈다.
        var popup = _overflow is { } list && ContainerOf(list.Items, target) is not null ? list.Popup : null;
        if (popup is not null) popup.StaysOpen = true;
        BeginDialogEditing();
        menu.Closed += OnClosed;

        void OnClosed(object? sender, RoutedEventArgs args)
        {
            menu.Closed -= OnClosed;
            if (popup?.IsOpen == true)
            {
                if (editRequested) popup.IsOpen = false;
                else popup.StaysOpen = false;
            }
            EndEditing();

            // 닫힘 애니메이션까지 끝난 뒤 더블클릭과 같은 경로로 편집을 시작한다.
            if (editRequested && Open(chip.EventId, chip.OccurrenceId, chip.TodoId) is { } editor)
                Dispatcher.BeginInvoke(() => ShowEditor(editor));
        }
    }

    // 편집 창이 떠 있는 동안은 Z순서 고정을 멈춘다(§3.3 Editing). 저장·삭제했으면 바로 다시 그린다.
    private void ShowEditor(EditorModel editor)
    {
        BeginDialogEditing();
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
    private void BeginDialogEditing()
    {
        _sidebarOwnsEditing = false;
        _pinning.BeginEditing();
    }

    private void EndEditing()
    {
        _sidebarOwnsEditing = false;
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
        BeginDialogEditing();
        try
        {
            var dialog = new SettingsDialog(new CategoryRepository(_database), _settings);
            dialog.PlaceOver(this);
            if (dialog.ShowDialog() == true)
            {
                TextSize.Apply(Application.Current.Resources, _settings.TextSizePercent);
                _viewModel.Refresh();
            }
        }
        finally
        {
            EndEditing();
        }
    }

    private void OnSearchClick(object sender, RoutedEventArgs e) => ShowSearch();

    private void OnSidebarToggleClick(object sender, RoutedEventArgs e)
    {
        _settings.Set("sidebar_collapsed", SidebarCard.Visibility == Visibility.Visible ? "1" : "0");
        ApplySidebar();
    }

    // 기본은 펼침. 접어도 달력 크기는 그대로이고 사이드바 자리만 비운다. 크기가 안 바뀌므로 흐림 창은 직접 다시 놓는다.
    private void ApplySidebar()
    {
        var isCollapsed = _settings.Get("sidebar_collapsed") == "1";
        SidebarCard.Visibility = isCollapsed ? Visibility.Hidden : Visibility.Visible;
        UpdateBlur();
        var label = isCollapsed ? "사이드바 펼치기" : "사이드바 접기";
        SidebarToggleButton.ToolTip = label;
        System.Windows.Automation.AutomationProperties.SetName(SidebarToggleButton, label);
    }

    // Ctrl+F는 달력 창이 키보드를 받고 있을 때(할 일 입력란 등)만 온다. 평소에는 창이 활성화되지 않기 때문이다.
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Key == Key.Escape && !IsAdjusting)
        {
            var dismissed = _sidebarTip.Dismiss();
            if (_sidebarOwnsEditing && Keyboard.FocusedElement is DockPanel { Tag: TextBlock })
            {
                Keyboard.ClearFocus();
                if (_sidebarOwnsEditing) EndEditing();
                e.Handled = true;
                return;
            }
            if (dismissed && Keyboard.FocusedElement is not TextBoxBase)
            {
                e.Handled = true;
                return;
            }
        }
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
        BeginDialogEditing();
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
    // 시작할 때와 화면 구성이 바뀌었을 때 부른다.
    // 자리는 물리 픽셀로 다룬다. WPF 좌표(DIP)는 창이 지금 있는 모니터의 배율로 바뀌므로, 배율이 다른 모니터(노트북 175%, 외부 100%)의 자리를 그대로 옮기면 어긋난다.
    private void PlaceWindow()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var saved = _settings.Get(BoundsKey())?.Split(',')
            .Select(part => int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : (int?)null).ToArray();
        if (saved is [{ } x, { } y, { } width, { } height])
        {
            // 모니터 사이 빈 곳이 아니라 어느 한 모니터에 200 × 200 이상 보여야 한다.
            var rect = new System.Drawing.Rectangle(x, y, width, height);
            if (System.Windows.Forms.Screen.AllScreens.Any(screen =>
                System.Drawing.Rectangle.Intersect(rect, screen.Bounds) is { Width: >= 200, Height: >= 200 }))
            {
                MoveWindow(hwnd, x, y, width, height);
                return;
            }
        }
        // 주 모니터로 먼저 옮겨 그 배율의 크기를 얻은 뒤, 작업 영역 안에 들어가도록 줄여 가운데에 둔다.
        var area = System.Windows.Forms.Screen.PrimaryScreen!.WorkingArea;
        SetWindowPos(hwnd, IntPtr.Zero, area.X, area.Y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
        GetWindowRect(hwnd, out var current);
        var fitWidth = Math.Min(current.Right - current.Left, area.Width);
        var fitHeight = Math.Min(current.Bottom - current.Top, area.Height);
        SetWindowPos(hwnd, IntPtr.Zero, area.X + (area.Width - fitWidth) / 2, area.Y + (area.Height - fitHeight) / 2,
            fitWidth, fitHeight, SWP_NOZORDER | SWP_NOACTIVATE);
    }

    // 배율이 다른 모니터로 옮기면 WPF가 크기를 그 배율로 다시 잡으므로, 먼저 자리만 옮긴 뒤 크기까지 한 번 더 맞춘다.
    private static void MoveWindow(IntPtr hwnd, int x, int y, int width, int height)
    {
        SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
        SetWindowPos(hwnd, IntPtr.Zero, x, y, width, height, SWP_NOZORDER | SWP_NOACTIVATE);
    }

    // 크기·위치는 화면 구성(모니터마다의 영역, 픽셀)마다 따로 저장한다. 노트북 단독일 때와 모니터를 연결했을 때 자리가 다르기 때문이다.
    // 전체 외곽만 보면 배치가 바뀌어도 같은 키가 되어 모니터 사이 빈 곳을 복원할 수 있다.
    // 값은 물리 픽셀이다. DIP로 저장하던 예전 키(window_bounds:)는 단위가 달라 읽지 않는다.
    private static string BoundsKey() => "window_rect:" + string.Join(";", System.Windows.Forms.Screen.AllScreens
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
            GetWindowRect(new WindowInteropHelper(this).Handle, out var rect);
            _settings.Set(BoundsKey(), string.Create(CultureInfo.InvariantCulture,
                $"{rect.Left},{rect.Top},{rect.Right - rect.Left},{rect.Bottom - rect.Top}"));
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

    // The local widths already account for both Windows DPI and the selected text scale.
    private void OnToolbarSizeChanged(object sender, SizeChangedEventArgs e) =>
        DockPanel.SetDock(HeaderButtons, e.NewSize.Width < 400 ? Dock.Top : Dock.Right);

    private void OnFooterSizeChanged(object sender, SizeChangedEventArgs e) =>
        DockPanel.SetDock(SyncStatusPanel, e.NewSize.Width < 400 ? Dock.Bottom : Dock.Right);

    private void BuildWeekdayHeader()
    {
        for (var day = 0; day < 7; day++)
        {
            var label = new TextBlock
            {
                Text = WeekdayNames[day], Style = (Style)FindResource("BaseText"), FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
            };
            label.SetResourceReference(TextBlock.ForegroundProperty, day == 6 ? "SundayBrush" : day == 5 ? "SaturdayBrush" : "SubtleBrush");
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
                Text = $"{hour:00}:00", Style = (Style)FindResource("BaseText"), FontSize = 11,
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
        // 놓기 처리 중에는 그 목록의 항목을 바꾸지 않고, 이벤트가 끝난 뒤에 바꾼다.
        if (e.PropertyName == nameof(MainViewModel.Month) && _overflow is not null) Dispatcher.BeginInvoke(RefreshOverflow);
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

    private void OnTitleEnter(object sender, MouseEventArgs e) => _sidebarTip.Enter((TextBlock)sender);
    private void OnTitleLeave(object sender, MouseEventArgs e) => _sidebarTip.Leave((TextBlock)sender);

    private void OnSidebarRowPreviewFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (e.NewFocus == sender && !_pinning.AllowsKeyboardNavigation) e.Handled = true;
    }

    private void OnSidebarRowFocusChanged(object sender, KeyboardFocusChangedEventArgs e) =>
        _sidebarTip.OnRowFocusChanged(sender, e);

    // +N 목록의 폭: 달력 칸보다 넓게(1.6배) 잡되 너무 작거나 크지 않게. 긴 제목은 줄바꿈한다(feedback 3-7).
    private const double OVERFLOW_MIN_WIDTH = 280, OVERFLOW_MAX_WIDTH = 420, OVERFLOW_WIDTH_RATIO = 1.6;
    // 끄는 동안 목록의 위·아래 끝에서 이 거리 안에 있으면 그쪽으로 굴린다(목록이 길어 스크롤될 때).
    private const double OVERFLOW_SCROLL_EDGE = 24;

    // 열린 +N 목록. 열려 있는 동안 달력을 다시 그리면(끌어 놓기, Claude·다른 PC의 변경) 같은 날짜의 새 목록으로 바꾼다.
    private sealed record OverflowList(Popup Popup, DateOnly Date, TextBlock Header, ItemsControl Items, ScrollViewer Scroll, Border DropLine);
    private OverflowList? _overflow;

    // +N: 그날 항목 전체를 목록으로 보여준다(§6.3). 많으면 목록 안에서 스크롤한다.
    // 같은 날 단일 일정끼리 끌어 순서를 바꾼다. 놓아도 목록은 열린 채 새 순서로 바뀌어 여러 개를 연달아 옮길 수 있다.
    private void OnOverflowClick(object sender, RoutedEventArgs e)
    {
        var button = (FrameworkElement)sender;
        var cell = (DayCellModel)button.DataContext;
        if (_overflow is { } previous) previous.Popup.IsOpen = false;
        var items = new ItemsControl { ItemTemplate = (DataTemplate)FindResource("OverflowItemTemplate") };
        // 놓을 자리 표시선. 목록과 함께 굴러가도록 같은 칸에 겹쳐 둔다.
        var dropLine = new Border { Height = 2, CornerRadius = new CornerRadius(1), Visibility = Visibility.Collapsed };
        dropLine.SetResourceReference(Border.BackgroundProperty, "AccentBrush");
        var scroll = new ScrollViewer
        {
            Content = new Grid { Children = { items, new Canvas { IsHitTestVisible = false, Children = { dropLine } } } },
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = Math.Max(240, CalendarCard.ActualHeight * 0.6)
        };
        var header = new TextBlock
        {
            Style = (Style)FindResource("BaseText"), FontWeight = FontWeights.Bold, FontSize = 15, Margin = new Thickness(2, 0, 0, 2)
        };
        var cellWidth = CalendarCard.ActualWidth / 7;
        var card = new Border
        {
            Width = Math.Clamp(cellWidth * OVERFLOW_WIDTH_RATIO, OVERFLOW_MIN_WIDTH, OVERFLOW_MAX_WIDTH),
            Padding = new Thickness(12, 10, 12, 12), CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1),
            Child = new DockPanel { Children = { header, scroll } }
        };
        DockPanel.SetDock(header, Dock.Top);
        card.SetResourceReference(LayoutTransformProperty, "TextLayoutScale");
        card.SetResourceReference(Border.BackgroundProperty, "PopupBrush");
        card.SetResourceReference(Border.BorderBrushProperty, "ControlBorderBrush");
        // 놓은 뒤 달력을 다시 그리면 +N 단추는 새로 만들어진다. 자리는 그대로 남는 달력 카드 위, 그 단추가 있던 곳을 기준으로 잡는다.
        var popup = new Popup
        {
            Child = card, PlacementTarget = CalendarCard, Placement = PlacementMode.Bottom, StaysOpen = false,
            PlacementRectangle = button.TransformToVisual(CalendarCard).TransformBounds(new Rect(button.RenderSize))
        };
        var overflow = new OverflowList(popup, cell.Date, header, items, scroll, dropLine);
        popup.Closed += (_, _) =>
        {
            if (_overflow == overflow) _overflow = null;
        };
        // 팝업은 창과 다른 시각 트리라 창의 더블클릭·끌기 처리가 닿지 않는다. 같은 처리를 붙인다.
        card.AllowDrop = true;
        card.PreviewMouseLeftButtonDown += (_, args) =>
        {
            TrackOrderPress(args, card);
            if (args.ClickCount != 2) return;
            popup.IsOpen = false;
            OpenEditorAt(args);
        };
        // 끄는 동안(DoDragDrop이 마우스를 쥔 동안)은 밖을 눌러 닫는 동작이 목록을 닫지 않게 하고, 끝나면 되살린다.
        card.PreviewMouseMove += (_, args) => StartOrderDrag(args, card,
            dragging: () => popup.StaysOpen = true,
            ended: () =>
            {
                dropLine.Visibility = Visibility.Collapsed;
                if (popup.IsOpen) popup.StaysOpen = false;
            });
        card.PreviewDragOver += (_, args) =>
        {
            ShowDropLine(overflow, args, ShowOrderDropEffect(args));
            ScrollNearEdge(scroll, args);
        };
        card.PreviewDragLeave += (_, args) =>
        {
            if (!new Rect(card.RenderSize).Contains(args.GetPosition(card))) dropLine.Visibility = Visibility.Collapsed;
        };
        card.PreviewDrop += (_, args) =>
        {
            dropLine.Visibility = Visibility.Collapsed;
            DropOrderItem(args);
        };
        _overflow = overflow;
        ShowOverflowItems(overflow, cell);
        popup.IsOpen = true;
    }

    private static void ShowOverflowItems(OverflowList overflow, DayCellModel cell)
    {
        overflow.Items.ItemsSource = cell.AllItems;
        overflow.Header.Text = $"{cell.Date.ToString("M월 d일 dddd", CultureInfo.GetCultureInfo("ko-KR"))} · {cell.AllItems.Count}개";
    }

    // 달력을 다시 그렸다. 열린 +N 목록을 같은 날짜의 새 항목으로 바꾼다. 그 날짜가 화면에서 빠졌거나 항목이 없으면 닫는다.
    private void RefreshOverflow()
    {
        if (_overflow is not { } overflow || !overflow.Popup.IsOpen) return;
        if (_viewModel.Month?.CellOn(overflow.Date) is { AllItems.Count: > 0 } cell) ShowOverflowItems(overflow, cell);
        else overflow.Popup.IsOpen = false;
    }

    // 놓을 자리에 선을 긋는다. 위로 옮기면 대상 앞, 아래로 옮기면 대상 다음 자리다(DayEventOrder.Move와 같다).
    // 받지 않는 자리(여러 날 일정·할 일·완료 상태가 다른 일정)면 선을 지우고 금지 커서만 보인다.
    private void ShowDropLine(OverflowList overflow, DragEventArgs e, DayOrderItem? target)
    {
        var container = target is null ? null : ContainerOf(overflow.Items, e.OriginalSource as DependencyObject);
        if (_draggingEvent is not { } source || target is null || container is null || overflow.DropLine.Parent is not Canvas layer)
        {
            overflow.DropLine.Visibility = Visibility.Collapsed;
            return;
        }
        var bounds = container.TransformToVisual(layer).TransformBounds(new Rect(container.RenderSize));
        // 항목마다 위쪽 여백(6)이 있다. 선은 그 여백 가운데에 긋는다(아래로 옮기면 다음 항목의 여백).
        Canvas.SetTop(overflow.DropLine, (source.LandsBelow(overflow.Items.Items.OfType<ChipModel>(), target) ? bounds.Bottom : bounds.Top) + 2);
        overflow.DropLine.Width = bounds.Width;
        overflow.DropLine.Visibility = Visibility.Visible;
    }

    private static FrameworkElement? ContainerOf(ItemsControl items, DependencyObject? node)
    {
        for (; node is not null; node = Parent(node))
            if (node is FrameworkElement element && ItemsControl.ItemsControlFromItemContainer(element) == items) return element;
        return null;
    }

    private static void ScrollNearEdge(ScrollViewer scroll, DragEventArgs e)
    {
        var y = e.GetPosition(scroll).Y;
        if (y < OVERFLOW_SCROLL_EDGE) scroll.LineUp();
        else if (y > scroll.ActualHeight - OVERFLOW_SCROLL_EDGE) scroll.LineDown();
    }

    private void OnMenuClick(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = (UIElement)sender, Placement = PlacementMode.Top };
        // 설정 창은 메뉴가 닫힌(편집 상태가 풀린) 뒤에 연다.
        if (SidebarCard.IsVisible)
            menu.Items.Add(MenuItem("사이드바 제목 탐색", () => Dispatcher.BeginInvoke(BrowseSidebar)));
        menu.Items.Add(MenuItem("설정", () => Dispatcher.BeginInvoke(ShowSettings)));
        menu.Items.Add(MenuItem("크기·위치 조정", ToggleAdjust));
        menu.Items.Add(MenuItem("종료", Exit));
        // 메뉴가 열린 동안에는 창이 활성화되므로 Z순서 고정을 잠시 멈춘다. 조정 모드를 켰으면 끝날 때까지 그대로 둔다.
        menu.Opened += (_, _) => BeginDialogEditing();
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
        // 뒤가 흐리게 비치면(BlurBackdrop) 막을 절반쯤만 덮는다. 흐림이 없으면 뒤 그림이 또렷해 글자가 묻히므로 더 짙게 덮는다.
        var alpha = _blur is null ? (byte)0xA6 : (byte)0x80;
        var plain = Color.FromArgb(alpha, tint.R, tint.G, tint.B);
        // 광택은 왼쪽 위에서 비스듬히 흘러내린다(가장자리 반사선 GlassRimBrush와 같은 방향).
        var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0.3, 1) };
        brush.GradientStops.Add(new GradientStop(Blend(tint, alpha, 0x40), 0));
        brush.GradientStops.Add(new GradientStop(Blend(tint, alpha, 0x14), 0.18));
        brush.GradientStops.Add(new GradientStop(plain, 0.45));
        brush.GradientStops.Add(new GradientStop(plain, 0.85));
        brush.GradientStops.Add(new GradientStop(Blend(tint, alpha, 0x22), 1));
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
        _sidebarOwnsEditing = true;
        if (_pinning.State == PinState.Editing) return;
        _pinning.BeginEditing();
        Activate();
        NewTodoBox.Focus();
    }

    private void BrowseSidebar()
    {
        if (IsAdjusting || !SidebarCard.IsVisible) return;
        _sidebarOwnsEditing = true;
        _pinning.BeginEditing();
        if (Activate() && SidebarCard.MoveFocus(new TraversalRequest(FocusNavigationDirection.First))) return;
        EndEditing();
    }

    private void OnSidebarBlur(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (!_sidebarOwnsEditing || e.NewFocus is Visual target && (target == SidebarCard || target.IsDescendantOf(SidebarCard))) return;
        EndEditing();
    }

    private void OnChecklistEditLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not TextBox { DataContext: ChecklistItemViewModel { IsEditing: true } } box) return;
        _sidebarOwnsEditing = true;
        if (_pinning.State != PinState.Editing) _pinning.BeginEditing();
        Activate();
        box.Focus();
        box.SelectAll();
    }

    private void OnChecklistEditKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Enter or Key.Escape) || sender is not TextBox { DataContext: ChecklistItemViewModel item } box) return;
        e.Handled = true;
        _viewModel.EndChecklistEdit(item, e.Key == Key.Enter ? box.Text : null);
        if (_pinning.State == PinState.Editing) EndEditing();
    }

    private void OnChecklistEditBlur(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is not TextBox { DataContext: ChecklistItemViewModel { IsEditing: true } item } box) return;
        _viewModel.EndChecklistEdit(item, box.Text);
    }

    private void OnChecklistItemRightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ChecklistItemViewModel item } target) return;
        e.Handled = true;
        var delete = new MenuItem { Header = "삭제" };
        delete.Click += (_, _) => _viewModel.DeleteChecklistItem(item);
        var menu = new ContextMenu { PlacementTarget = target };
        menu.Items.Add(delete);
        menu.Opened += (_, _) => BeginDialogEditing();
        menu.Closed += (_, _) => EndEditing();
        menu.IsOpen = true;
    }

    private static string LogPath(string name)
    {
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeskCal", "logs");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, name);
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left, Top, Right, Bottom;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);
}
