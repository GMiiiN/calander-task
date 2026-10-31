using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using DeskCal.Core;

namespace DeskCal.App;

/// <summary>
/// 설정 창(§6.5 카테고리 이름 관리, §8 시작 시 실행). 저장하면 DialogResult=true로 닫힌다.
/// </summary>
public partial class SettingsDialog : Window
{
    private readonly CategoryRepository _categories;
    private readonly Dictionary<string, TextBox> _nameBoxes = [];

    public SettingsDialog(CategoryRepository categories)
    {
        InitializeComponent();
        _categories = categories;
        foreach (var category in categories.List()) AddRow(category);
        AutoStartCheck.IsChecked = AutoStart.IsEnabled;
        Loaded += (_, _) => _nameBoxes.Values.FirstOrDefault()?.Focus();
    }

    // 달력 창 위쪽 가운데에 띄운다.
    public void PlaceOver(Window owner)
    {
        WindowStartupLocation = WindowStartupLocation.Manual;
        var workArea = SystemParameters.WorkArea;
        Left = Math.Clamp(owner.Left + (owner.ActualWidth - Width) / 2, workArea.Left, Math.Max(workArea.Left, workArea.Right - Width));
        Top = Math.Clamp(owner.Top + 60, workArea.Top, Math.Max(workArea.Top, workArea.Bottom - 640));
    }

    private void AddRow(CategoryRecord category)
    {
        var dot = new Ellipse
        {
            Width = 12, Height = 12, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center,
            Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(category.ColorHex))
        };
        var box = new TextBox { Text = category.Name, Style = (Style)FindResource("Field"), MaxLength = 40, Padding = new Thickness(6, 3, 6, 3) };
        var row = new DockPanel { Margin = new Thickness(0, 0, 8, 6) };
        DockPanel.SetDock(dot, Dock.Left);
        row.Children.Add(dot);
        row.Children.Add(box);
        CategoryRows.Children.Add(row);
        _nameBoxes[category.ColorId] = box;
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        try
        {
            _categories.Rename(_nameBoxes.ToDictionary(pair => pair.Key, pair => pair.Value.Text));
        }
        catch (ArgumentException exception)
        {
            ErrorText.Text = exception.Message switch
            {
                "Category name is required." => "카테고리 이름을 비울 수 없습니다.",
                "Category names must be unique." => "카테고리 이름이 서로 겹칩니다. Claude가 이름으로 카테고리를 고를 수 있도록 다르게 지어 주세요.",
                "Category name must not be another category's color_id." => "숫자만으로 된 이름(1–11)은 쓸 수 없습니다.",
                _ => $"저장하지 못했습니다: {exception.Message}"
            };
            ErrorText.Visibility = Visibility.Visible;
            return;
        }
        if (AutoStartCheck.IsChecked != AutoStart.IsEnabled) AutoStart.Set(AutoStartCheck.IsChecked == true);
        DialogResult = true;
    }

    private void OnAutoStartLabelClick(object sender, MouseButtonEventArgs e) => AutoStartCheck.IsChecked = AutoStartCheck.IsChecked != true;

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;

    private void OnHeaderMouseDown(object sender, MouseButtonEventArgs e) => DragMove();

    // Esc = 취소, Enter = 저장
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Key == Key.Escape)
        {
            DialogResult = false;
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            OnSave(this, e);
            e.Handled = true;
        }
    }
}
