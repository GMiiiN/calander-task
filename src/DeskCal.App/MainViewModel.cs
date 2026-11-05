using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using DeskCal.App.Calendar;
using DeskCal.Core;

namespace DeskCal.App;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    protected void OnPropertyChanged(string? name) => PropertyChanged?.Invoke(this, new(name));
}

public sealed class RelayCommand(Action<object?> execute) : ICommand
{
    public event EventHandler? CanExecuteChanged { add { } remove { } }
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => execute(parameter);
}

public sealed record DdayChipViewModel(string Title, string Label, bool IsToday);

public sealed record ChecklistItemViewModel(string ItemId, string Text, bool IsDone, bool IsEditing, string EventId, string? OccurrenceId);

public sealed record SidebarItemViewModel(string Title, string Meta, bool IsDone, string? DotColorHex,
    string? TodoId, string? EventId, string? OccurrenceId, IReadOnlyList<ChecklistItemViewModel>? Checklist = null, bool IsExpanded = false)
{
    public bool HasChecklist => Checklist is { Count: > 0 };
}

public sealed record SidebarGroupViewModel(string Header, string Kind, IReadOnlyList<SidebarItemViewModel> Items);

public sealed class MainViewModel : ObservableObject
{
    private static readonly CultureInfo Korean = CultureInfo.GetCultureInfo("ko-KR");

    private readonly Database _database;
    private readonly EventRepository _events;
    private readonly TodoRepository _todos;
    private readonly SettingStore _settings;
    private DateOnly _today;
    private DateOnly _anchor;
    private bool _isMonthView = true;
    private string _yearText = "";
    private string _titleText = "";
    private MonthModel? _month;
    private WeekModel? _week;
    private IReadOnlyList<DdayChipViewModel> _ddays = [];
    private IReadOnlyList<SidebarGroupViewModel> _groups = [];
    private int _agendaCount;
    private string _newTodoTitle = "";
    // 펼침 상태는 앱이 켜져 있는 동안만 기억한다(feedback 3-4). 다시 켜면 모두 접혀 있다.
    private readonly HashSet<string> _expandedChecklists = [];
    private string? _editingChecklistItemId;

    public MainViewModel(Database database)
    {
        _database = database;
        _events = new EventRepository(database);
        _todos = new TodoRepository(database);
        _settings = new SettingStore(database);
        _today = Today();
        _isMonthView = _settings.Get("view") != "week";
        _anchor = DateOnly.TryParseExact(_settings.Get("anchor_date"), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var saved)
            ? saved : _today;
        PreviousCommand = new RelayCommand(_ => Move(-1));
        NextCommand = new RelayCommand(_ => Move(1));
        TodayCommand = new RelayCommand(_ => { _anchor = _today; SaveView(); Refresh(); });
        ShowMonthCommand = new RelayCommand(_ => { IsMonthView = true; SaveView(); Refresh(); });
        ShowWeekCommand = new RelayCommand(_ => { IsMonthView = false; SaveView(); Refresh(); });
        ToggleDoneCommand = new RelayCommand(p => ToggleDone((SidebarItemViewModel)p!));
        ToggleChecklistCommand = new RelayCommand(p => ToggleChecklist((SidebarItemViewModel)p!));
        ToggleChecklistItemCommand = new RelayCommand(p => ToggleChecklistItem((ChecklistItemViewModel)p!));
        AddTodoCommand = new RelayCommand(_ => AddTodo());
        Refresh();
    }

    public ICommand PreviousCommand { get; }
    public ICommand NextCommand { get; }
    public ICommand TodayCommand { get; }
    public ICommand ShowMonthCommand { get; }
    public ICommand ShowWeekCommand { get; }
    public ICommand ToggleDoneCommand { get; }
    public ICommand ToggleChecklistCommand { get; }
    public ICommand ToggleChecklistItemCommand { get; }
    public ICommand AddTodoCommand { get; }

    public bool IsMonthView { get => _isMonthView; private set => Set(ref _isMonthView, value); }
    public string YearText { get => _yearText; private set => Set(ref _yearText, value); }
    public string TitleText { get => _titleText; private set => Set(ref _titleText, value); }
    public MonthModel? Month { get => _month; private set => Set(ref _month, value); }
    public WeekModel? Week { get => _week; private set => Set(ref _week, value); }
    public IReadOnlyList<DdayChipViewModel> Ddays { get => _ddays; private set => Set(ref _ddays, value); }
    public IReadOnlyList<SidebarGroupViewModel> Groups { get => _groups; private set => Set(ref _groups, value); }
    public int AgendaCount { get => _agendaCount; private set => Set(ref _agendaCount, value); }
    public string NewTodoTitle { get => _newTodoTitle; set => Set(ref _newTodoTitle, value); }
    public string TodayText => $"오늘 · {_today.ToString("M월 d일 dddd", Korean)}";

    // 자정이 지났으면 오늘 기준도 옮긴다. 보던 날짜가 어제(=그때의 오늘)였으면 함께 따라간다.
    public void Refresh()
    {
        var today = Today();
        if (today != _today)
        {
            if (_anchor == _today) _anchor = today;
            _today = today;
            OnPropertyChanged(nameof(TodayText));
        }
        var builder = new CalendarModelBuilder(_events, _todos, _database);
        if (IsMonthView)
        {
            Month = builder.BuildMonth(_anchor.Year, _anchor.Month, _today);
            Week = null;
            YearText = $"{_anchor.Year}년";
            TitleText = $"{_anchor.Month}월";
        }
        else
        {
            Week = builder.BuildWeek(_anchor, _today);
            Month = null;
            var end = Week.WeekStart.AddDays(6);
            YearText = $"{Week.WeekStart.Year}년 {Week.WeekStart.Month}월";
            TitleText = $"{Week.WeekStart:M.d} – {end:M.d}";
        }
        RefreshSidebar();
    }

    private void RefreshSidebar()
    {
        if (_editingChecklistItemId is null) BuildSidebar(); // 고치는 중에 다시 만들면 입력란이 사라진다.
    }

    private void BuildSidebar()
    {
        var agenda = new AgendaService(_events, _todos, today: () => _today).GetAgenda();
        var categories = ReadCategories();
        Ddays = agenda.Ddays.Where(d => d.DaysRemaining >= 0)
            .Select(d => new DdayChipViewModel(d.Occurrence.Event.Title, d.DaysRemaining == 0 ? "D-day" : $"D-{d.DaysRemaining}", d.DaysRemaining == 0))
            .ToArray();
        var groups = new List<SidebarGroupViewModel>();
        // 마감일 없는 할 일은 14일 목록 맨 아래로 밀려 보이지 않았다(feedback 4). 기한 지남 바로 다음에 둔다.
        foreach (var group in agenda.Groups.OrderBy(g => g.Kind switch { "overdue" => 0, "anytime" => 1, _ => 2 }))
        {
            var items = group.Events.Select(o => EventItem(o, categories))
                .Concat(group.Todos.Select(TodoItem)).ToArray();
            if (items.Length == 0) continue;
            groups.Add(new(GroupHeader(group), group.Kind == "date" && group.Date == _today ? "today" : group.Kind, items));
        }
        Groups = groups;
        AgendaCount = groups.Sum(g => g.Items.Count(i => !i.IsDone));
    }

    private SidebarItemViewModel EventItem(EventOccurrence occurrence, IReadOnlyDictionary<string, (string Name, string Hex)> categories)
    {
        var value = occurrence.Event;
        var category = value.ColorId is not null && categories.TryGetValue(value.ColorId, out var found) ? found : ("일정", "#246BDE");
        var time = value.StartTime is null ? "종일" : $"{value.StartTime}–{value.EndTime}";
        var eventId = occurrence.OccurrenceId is { } id ? OccurrenceId.Parse(id).EventUid : value.EventUid;
        var checklist = Checklist.Visible(value.Checklist)
            .Select(i => new ChecklistItemViewModel(i.ItemId, i.Text, i.IsDone, i.ItemId == _editingChecklistItemId, eventId, occurrence.OccurrenceId)).ToArray();
        return new(value.Title, $"{time} · {category.Item1}", value.IsDone, category.Item2, null, value.EventUid, occurrence.OccurrenceId,
            checklist, _expandedChecklists.Contains(occurrence.OccurrenceId ?? eventId));
    }

    private SidebarItemViewModel TodoItem(TodoRecord todo)
    {
        var meta = todo.DueDate is null ? "할 일"
            : todo.DueDate == _today.ToString("yyyy-MM-dd") ? "할 일 · 오늘 마감"
            : $"할 일 · {DateOnly.ParseExact(todo.DueDate, "yyyy-MM-dd", CultureInfo.InvariantCulture).ToString("M'/'d")} 마감";
        return new(todo.Title, meta, todo.IsDone, null, todo.TodoUid, null, null);
    }

    private string GroupHeader(AgendaGroup group) => group.Kind switch
    {
        "overdue" => "기한 지남",
        "later" => "이후",
        "anytime" => "마감일 없음",
        _ when group.Date == _today => $"오늘 · {group.Date.Value.ToString("M월 d일 ddd", Korean)}",
        _ => group.Date!.Value.ToString("M월 d일 ddd", Korean)
    };

    private void ToggleDone(SidebarItemViewModel item)
    {
        var done = !item.IsDone;
        if (item.TodoId is not null) _todos.Update(item.TodoId, new TodoPatch { IsDone = new(true, done) });
        else if (item.OccurrenceId is not null) _events.SetOccurrenceDone(item.OccurrenceId, done);
        else _events.Update(item.EventId!, new EventPatch { IsDone = new(true, done) });
        Refresh();
    }

    private void ToggleChecklist(SidebarItemViewModel item)
    {
        var key = item.OccurrenceId ?? item.EventId!;
        if (!_expandedChecklists.Remove(key)) _expandedChecklists.Add(key);
        RefreshSidebar();
    }

    private void ToggleChecklistItem(ChecklistItemViewModel item)
    {
        UpdateChecklistItem(item, isDone: !item.IsDone);
        Refresh();
    }

    public void BeginChecklistEdit(ChecklistItemViewModel item)
    {
        _editingChecklistItemId = item.ItemId;
        BuildSidebar();
    }

    // text가 null이면 취소다. 내용을 다 지우고 끝내도 항목은 지우지 않는다(삭제는 메뉴로만).
    public void EndChecklistEdit(ChecklistItemViewModel item, string? text)
    {
        if (_editingChecklistItemId != item.ItemId) return;
        _editingChecklistItemId = null;
        if (!string.IsNullOrWhiteSpace(text) && text.Trim() != item.Text) UpdateChecklistItem(item, text: text);
        Refresh();
    }

    public void DeleteChecklistItem(ChecklistItemViewModel item)
    {
        UpdateChecklistItem(item, isDeleted: true);
        Refresh();
    }

    private void UpdateChecklistItem(ChecklistItemViewModel item, string? text = null, bool? isDone = null, bool? isDeleted = null) =>
        _events.UpdateChecklistItem(item.OccurrenceId ?? item.EventId, item.OccurrenceId is not null, item.ItemId, text, isDone, isDeleted);

    private void AddTodo()
    {
        var title = NewTodoTitle.Trim();
        if (title.Length == 0) return;
        _todos.Create([new TodoInput(title)]);
        NewTodoTitle = "";
        Refresh();
    }

    public void GoTo(DateOnly date)
    {
        _anchor = date;
        SaveView();
        Refresh();
    }

    private void Move(int direction)
    {
        _anchor = IsMonthView
            ? new DateOnly(_anchor.Year, _anchor.Month, 1).AddMonths(direction)
            : _anchor.AddDays(7 * direction);
        SaveView();
        Refresh();
    }

    private void SaveView()
    {
        _settings.Set("view", IsMonthView ? "month" : "week");
        _settings.Set("anchor_date", _anchor.ToString("yyyy-MM-dd"));
    }

    private IReadOnlyDictionary<string, (string Name, string Hex)> ReadCategories()
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT color_id, name, color_hex FROM category;";
        using var reader = command.ExecuteReader();
        var result = new Dictionary<string, (string, string)>();
        while (reader.Read()) result[reader.GetString(0)] = (reader.GetString(1), reader.GetString(2));
        return result;
    }

    private static DateOnly Today() => DateOnly.FromDateTime(DateTime.Now);
}
