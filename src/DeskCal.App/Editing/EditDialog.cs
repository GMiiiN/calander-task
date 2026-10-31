using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using DeskCal.Core;

namespace DeskCal.App;

public sealed record CategoryOption(string? ColorId, string Name, string ColorHex);

/// <summary>
/// 일정·할 일 추가/수정 창(§6.5, Figma "Event edit dialog"). 입력 검증과 저장은 EditorModel이 한다.
/// 저장하거나 삭제하면 DialogResult=true로 닫힌다.
/// </summary>
public partial class EditDialog : Window
{
    private static readonly (RepeatKind Kind, string Label)[] RepeatOptions =
        [(RepeatKind.None, "안 함"), (RepeatKind.Daily, "매일"), (RepeatKind.Weekly, "매주"), (RepeatKind.Monthly, "매월"), (RepeatKind.Yearly, "매년")];

    private readonly EditorModel _editor;
    private readonly EventRepository _events;
    private readonly TodoRepository _todos;
    private readonly IReadOnlyList<CategoryOption> _categories;

    public EditDialog(EditorModel editor, EventRepository events, TodoRepository todos, IReadOnlyList<CategoryOption> categories)
    {
        InitializeComponent();
        _editor = editor;
        _events = events;
        _todos = todos;
        _categories = [new(null, "기본", "#246BDE"), .. categories];
        BuildRepeatChoices();
        BuildColorChoices();
        Load();
        Loaded += (_, _) => { TitleBox.Focus(); TitleBox.SelectAll(); };
    }

    // 달력 창 가운데에 띄운다. 달력이 화면 가장자리에 있어도 작업 영역 안에 들어오게 한다.
    public void CenterOver(Window owner)
    {
        WindowStartupLocation = WindowStartupLocation.Manual;
        var workArea = SystemParameters.WorkArea;
        Left = Math.Clamp(owner.Left + (owner.ActualWidth - Width) / 2, workArea.Left, Math.Max(workArea.Left, workArea.Right - Width));
        Top = Math.Clamp(owner.Top + 80, workArea.Top, Math.Max(workArea.Top, workArea.Bottom - 640));
    }

    private void Load()
    {
        ClaudeBadge.Visibility = _editor.CreatedBy == "claude" ? Visibility.Visible : Visibility.Collapsed;
        KindRow.Visibility = _editor.CanChangeKind ? Visibility.Visible : Visibility.Collapsed;
        KindEvent.IsChecked = _editor.Kind == "event";
        KindTodo.IsChecked = _editor.Kind == "todo";
        TitleBox.Text = _editor.Title;
        DateBox.Text = _editor.Date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "";
        AllDayCheck.IsChecked = _editor.IsAllDay;
        StartBox.Text = _editor.StartTime?.ToString("HH:mm", CultureInfo.InvariantCulture) ?? "09:00";
        EndBox.Text = _editor.EndTime?.ToString("HH:mm", CultureInfo.InvariantCulture) ?? "10:00";
        DdayCheck.IsChecked = _editor.IsDday;
        MemoBox.Text = _editor.Memo;
        foreach (RadioButton choice in RepeatChoices.Children) choice.IsChecked = Equals(choice.Tag, _editor.Repeat);
        foreach (RadioButton choice in ColorChoices.Children) choice.IsChecked = Equals(((CategoryOption)choice.Tag).ColorId, _editor.ColorId);
        UpdateCategoryName();
        UpdateLayoutForKind();
    }

    private void BuildRepeatChoices()
    {
        foreach (var (kind, label) in RepeatOptions)
            RepeatChoices.Children.Add(new RadioButton { Content = label, Tag = kind, GroupName = "Repeat", Style = (Style)FindResource("Pill") });
        // 앱에서 고를 수 없는 규칙(격주, 횟수 제한 등)은 그대로 보여 주기만 하고, 다른 값을 고르기 전에는 손대지 않는다.
        if (_editor.Repeat == RepeatKind.Custom)
            RepeatChoices.Children.Add(new RadioButton { Content = "사용자 지정", Tag = RepeatKind.Custom, GroupName = "Repeat", Style = (Style)FindResource("Pill") });
        RepeatChoices.IsEnabled = _editor.CanEditRepeat;
        if (!_editor.CanEditRepeat) RepeatChoices.ToolTip = "Google 캘린더에서 만든 반복 규칙은 Google 캘린더에서 바꾸세요.";
    }

    private void BuildColorChoices()
    {
        foreach (var category in _categories)
        {
            var dot = new RadioButton
            {
                Tag = category, GroupName = "Color", Style = (Style)FindResource("ColorDot"), ToolTip = category.Name,
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(category.ColorHex))
            };
            dot.Checked += (_, _) => UpdateCategoryName();
            ColorChoices.Children.Add(dot);
        }
    }

    private void UpdateCategoryName() =>
        CategoryName.Text = ColorChoices.Children.OfType<RadioButton>().FirstOrDefault(c => c.IsChecked == true)?.Tag is CategoryOption selected
            ? selected.Name : "기본";

    // 할 일에는 시간·반복·카테고리·D-day가 없다(설계 §4, Google Tasks).
    private void UpdateLayoutForKind()
    {
        var isEvent = KindTodo.IsChecked != true;
        DialogTitle.Text = (isEvent ? "일정 " : "할 일 ") + (_editor.IsNew ? "추가" : "수정");
        DateLabel.Text = isEvent ? "날짜" : "마감일";
        DateBox.ToolTip = isEvent ? "예: 2026-11-03" : "예: 2026-11-03 (비우면 마감일 없음)";
        foreach (UIElement element in new UIElement[] { AllDayCheck, AllDayLabel, RepeatRow, CategoryRow, DdayRow })
            element.Visibility = isEvent ? Visibility.Visible : Visibility.Collapsed;
        TimeRow.Visibility = isEvent && AllDayCheck.IsChecked != true ? Visibility.Visible : Visibility.Collapsed;
        DeleteButton.Visibility = _editor.IsNew ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnKindChanged(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized || _editor is null) return;
        UpdateLayoutForKind();
    }

    private void OnAllDayChanged(object sender, RoutedEventArgs e)
    {
        if (_editor is null) return;
        UpdateLayoutForKind();
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var error = Collect() ?? _editor.Save(_events, _todos);
        if (error is not null)
        {
            ShowError(error);
            return;
        }
        DialogResult = true;
    }

    // 입력란의 글자를 모델 값으로 옮긴다. 형식이 틀리면 오류 문장을 돌려준다.
    private string? Collect()
    {
        var isEvent = KindTodo.IsChecked != true;
        _editor.Kind = isEvent ? "event" : "todo";
        _editor.Title = TitleBox.Text.Trim();
        _editor.Memo = MemoBox.Text;
        var dateText = DateBox.Text.Trim();
        if (dateText.Length == 0 && !isEvent) _editor.Date = null;
        else if (DateOnly.TryParseExact(dateText, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) _editor.Date = date;
        else return "날짜는 2026-11-03처럼 입력하세요.";
        if (!isEvent) return null;

        _editor.IsAllDay = AllDayCheck.IsChecked == true;
        if (!_editor.IsAllDay)
        {
            if (!TryTime(StartBox.Text, out var start) || !TryTime(EndBox.Text, out var end)) return "시간은 09:30처럼 입력하세요.";
            _editor.StartTime = start;
            _editor.EndTime = end;
        }
        _editor.Repeat = RepeatChoices.Children.OfType<RadioButton>().FirstOrDefault(c => c.IsChecked == true)?.Tag as RepeatKind? ?? RepeatKind.None;
        // 목록에 없는 색(아직 받지 못한 Google 색 등)이면 아무 점도 선택되지 않는다. 그때는 원래 값을 그대로 둔다.
        if (ColorChoices.Children.OfType<RadioButton>().FirstOrDefault(c => c.IsChecked == true)?.Tag is CategoryOption selected)
            _editor.ColorId = selected.ColorId;
        _editor.IsDday = DdayCheck.IsChecked == true;
        return null;

        // "9:30"·"0930"도 받아 준다.
        static bool TryTime(string text, out TimeOnly value) =>
            TimeOnly.TryParseExact(text.Trim(), ["H:mm", "HH:mm", "HHmm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out value);
    }

    // 반복 회차에서 연 일정은 이 회차만 지울지 전체를 지울지 고른다.
    private void OnDelete(object sender, RoutedEventArgs e)
    {
        if (!_editor.IsRecurring)
        {
            Delete(onlyThisOccurrence: false);
            return;
        }
        var menu = new ContextMenu { PlacementTarget = (UIElement)sender, Placement = PlacementMode.Top };
        menu.Items.Add(Item("이 회차만 삭제", () => Delete(onlyThisOccurrence: true)));
        menu.Items.Add(Item("반복 일정 전체 삭제", () => Delete(onlyThisOccurrence: false)));
        menu.IsOpen = true;

        static MenuItem Item(string header, Action action)
        {
            var item = new MenuItem { Header = header };
            item.Click += (_, _) => action();
            return item;
        }
    }

    private void Delete(bool onlyThisOccurrence)
    {
        var error = _editor.Delete(_events, _todos, onlyThisOccurrence);
        if (error is not null)
        {
            ShowError(error);
            return;
        }
        DialogResult = true;
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;

    private void OnHeaderMouseDown(object sender, MouseButtonEventArgs e) => DragMove();

    // Esc = 취소, Enter = 저장. 메모 칸에서는 Enter가 줄바꿈이므로 Ctrl+Enter로 저장한다.
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Key == Key.Escape)
        {
            DialogResult = false;
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) || !MemoBox.IsKeyboardFocused))
        {
            OnSave(this, e);
            e.Handled = true;
        }
    }
}
