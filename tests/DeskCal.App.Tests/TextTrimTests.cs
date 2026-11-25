using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using System.Diagnostics;
using System.Runtime.ExceptionServices;

namespace DeskCal.App.Tests;

// feedback 3-2: 사이드바 제목은 잘렸을 때만 전체 제목 툴팁을 띄운다.
public sealed class TextTrimTests
{
    [Fact]
    public void KeyboardAndMouseShowOnlyTheCurrentTrimmedTitleAfterOneSecond()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var first = new TextBlock { Text = new string('가', 40), TextTrimming = TextTrimming.CharacterEllipsis };
            var second = new TextBlock { Text = "두 번째 제목 " + new string('나', 35), TextTrimming = TextTrimming.CharacterEllipsis };
            var firstRow = new Border { Child = first, Tag = first, Focusable = true };
            var secondRow = new Border { Child = second, Tag = second, Focusable = true };
            var popup = new ToolTip { Focusable = false, Opacity = 0 };
            var controller = new SidebarTitleToolTip(popup);
            var host = new Window
            {
                Width = 160, Height = 100, Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false,
                WindowStyle = WindowStyle.None, Opacity = 0, Content = new StackPanel { Children = { firstRow, secondRow } }
            };
            foreach (var row in new[] { firstRow, secondRow })
            {
                row.GotKeyboardFocus += controller.OnRowFocusChanged;
                row.LostKeyboardFocus += controller.OnRowFocusChanged;
            }
            static void FocusEvent(UIElement row, RoutedEvent evt, IInputElement? from, IInputElement? to) =>
                row.RaiseEvent(new KeyboardFocusChangedEventArgs(Keyboard.PrimaryDevice, Environment.TickCount, from, to) { RoutedEvent = evt });
            try
            {
                host.Show();
                host.UpdateLayout();
                var opened = new List<string>();
                popup.Opened += (_, _) => opened.Add((string)popup.Content);
                controller.Enter(first);
                controller.Leave(first); // mouse leaves during delay
                WaitInDispatcher(1100);
                Assert.Empty(opened);

                FocusEvent(firstRow, Keyboard.GotKeyboardFocusEvent, null, firstRow);
                FocusEvent(firstRow, Keyboard.LostKeyboardFocusEvent, firstRow, secondRow);
                var elapsed = Stopwatch.StartNew();
                FocusEvent(secondRow, Keyboard.GotKeyboardFocusEvent, firstRow, secondRow);
                WaitInDispatcher(350);
                Assert.False(popup.IsOpen);
                WaitInDispatcher(750);
                Assert.True(elapsed.ElapsedMilliseconds >= 1000);
                Assert.Equal(second.Text, Assert.Single(opened));
                Assert.Same(second, popup.PlacementTarget);
                Assert.True(controller.Dismiss()); // Escape
                Assert.False(popup.IsOpen);

                FocusEvent(secondRow, Keyboard.GotKeyboardFocusEvent, firstRow, secondRow);
                FocusEvent(secondRow, Keyboard.LostKeyboardFocusEvent, secondRow, firstRow);
                FocusEvent(firstRow, Keyboard.GotKeyboardFocusEvent, secondRow, firstRow);
                host.Width = 900;
                host.UpdateLayout();
                WaitInDispatcher(1100);
                Assert.False(popup.IsOpen); // widened enough before timer fired
                host.Width = 160;
                host.UpdateLayout();
                WaitInDispatcher(1100);
                Assert.Equal(new[] { second.Text, first.Text }, opened);
                FocusEvent(firstRow, Keyboard.LostKeyboardFocusEvent, firstRow, null);
                Assert.False(popup.IsOpen);
            }
            catch (Exception error) { failure = error; }
            finally { controller.Dismiss(); host.Close(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static void WaitInDispatcher(int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    [Theory]
    [InlineData(100)]
    [InlineData(115)]
    [InlineData(130)]
    public void LayoutScaleMeasuresTitlesAtAvailableWidth(int percent)
    {
        bool[]? trimmed = null;
        var thread = new Thread(() =>
        {
            var title = new TextBlock { Text = "아주 긴 한글 제목과 시간 정보", FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis };
            var panel = new Border { Child = title };
            panel.SetResourceReference(FrameworkElement.LayoutTransformProperty, "TextLayoutScale");
            TextSize.Apply(panel.Resources, percent);
            bool AtWidth(double width)
            {
                panel.Measure(new Size(width, 50));
                panel.Arrange(new Rect(0, 0, width, 50));
                return TextTrim.IsTrimmed(title);
            }
            trimmed = [AtWidth(80), AtWidth(320), AtWidth(80)];
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        Assert.Equal(new[] { true, false, true }, trimmed);
    }

    [Theory]
    [InlineData("MVP 만들기 말,글 -> 발주 그리고 아주 긴 설명이 계속 이어지는 제목", 120, true)]
    [InlineData("짧은 제목", 300, false)]
    public void DetectsWhetherTextIsCutOff(string title, double width, bool expected)
    {
        bool? result = null;
        var thread = new Thread(() =>
        {
            var text = new TextBlock { Text = title, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis, Width = width };
            text.Measure(new Size(width, 50));
            text.Arrange(new Rect(0, 0, width, 50));
            result = TextTrim.IsTrimmed(text);
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        Assert.Equal(expected, result);
    }
}
