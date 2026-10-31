using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DeskCal.Core;

namespace DeskCal.App;

public sealed record SearchResultViewModel(SearchHit Hit, string DateText, string TimeText, string ColorHex, string Meta)
{
    public string Title => Hit.Title;
    public bool IsTodo => Hit.Kind == "todo";
    public Visibility TimeVisibility => TimeText.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    public Visibility BarVisibility => IsTodo ? Visibility.Collapsed : Visibility.Visible;
    public Visibility CircleVisibility => IsTodo ? Visibility.Visible : Visibility.Collapsed;
    public TextDecorationCollection? Decorations => Hit.IsDone ? TextDecorations.Strikethrough : null;
}

/// <summary>
/// 검색 창(§6.5, Figma "Search panel"). 고른 결과는 SelectedHit에 담고 DialogResult=true로 닫힌다.
/// </summary>
public partial class SearchDialog : Window
{
    private static readonly CultureInfo Korean = CultureInfo.GetCultureInfo("ko-KR");

    private readonly SearchService _search;
    private readonly IReadOnlyDictionary<string, CategoryOption> _categories;
    private readonly DateOnly _today;

    public SearchDialog(SearchService search, IReadOnlyList<CategoryOption> categories, DateOnly today)
    {
        InitializeComponent();
        _search = search;
        _categories = categories.Where(c => c.ColorId is not null).ToDictionary(c => c.ColorId!);
        _today = today;
        Loaded += (_, _) => QueryBox.Focus();    }

    public SearchHit? SelectedHit { get; private set; }

    // 달력 창 위쪽 가운데에 띄운다.
    public void PlaceOver(Window owner)
    {
        WindowStartupLocation = WindowStartupLocation.Manual;
        var workArea = SystemParameters.WorkArea;
        Left = Math.Clamp(owner.Left + (owner.ActualWidth - Width) / 2, workArea.Left, Math.Max(workArea.Left, workArea.Right - Width));
        Top = Math.Clamp(owner.Top + 60, workArea.Top, Math.Max(workArea.Top, workArea.Bottom - 420));
    }

    private void OnQueryChanged(object sender, TextChangedEventArgs e)
    {
        var query = QueryBox.Text.Trim();
        Placeholder.Visibility = QueryBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        ResultsPanel.Visibility = query.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        SearchResultViewModel[] results = query.Length == 0 ? [] : _search.Search(query).Select(ToViewModel).ToArray();
        ResultList.ItemsSource = results;
        ResultList.SelectedIndex = results.Length > 0 ? 0 : -1;
        CountText.Text = results.Length.ToString(CultureInfo.InvariantCulture);
        EmptyText.Visibility = results.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private SearchResultViewModel ToViewModel(SearchHit hit)
    {
        var date = hit.Date is not { } value ? "마감일 없음"
            : value.Year == _today.Year ? value.ToString("M월 d일 (ddd)", Korean)
            : value.ToString("yyyy년 M월 d일", Korean);
        var time = hit.Kind == "todo" ? "" : hit.StartTime ?? "종일";
        var category = hit.ColorId is not null && _categories.TryGetValue(hit.ColorId, out var found) ? found : new(null, "일정", "#246BDE");
        var meta = hit.Kind == "todo" ? hit.IsDone ? "할 일 · 완료" : "할 일" : hit.IsDone ? $"{category.Name} · 완료" : category.Name;
        return new(hit, date, time, category.ColorHex, meta);
    }

    private void Choose()
    {
        if (ResultList.SelectedItem is not SearchResultViewModel selected) return;
        SelectedHit = selected.Hit;
        DialogResult = true;
    }

    private void OnResultDoubleClick(object sender, MouseButtonEventArgs e) => Choose();

    // 입력란에 글자를 넣는 채로 ↑↓로 결과를 고르고 Enter로 이동한다.
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        var count = ResultList.Items.Count;
        switch (e.Key)
        {
            case Key.Escape:
                DialogResult = false;
                break;
            case Key.Enter:
                Choose();
                break;
            case Key.Down when count > 0:
                ResultList.SelectedIndex = (ResultList.SelectedIndex + 1) % count;
                ResultList.ScrollIntoView(ResultList.SelectedItem);
                break;
            case Key.Up when count > 0:
                ResultList.SelectedIndex = (ResultList.SelectedIndex - 1 + count) % count;
                ResultList.ScrollIntoView(ResultList.SelectedItem);
                break;
            default:
                return;
        }
        e.Handled = true;
    }
}
