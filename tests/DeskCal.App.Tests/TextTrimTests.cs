using System.Windows;
using System.Windows.Controls;

namespace DeskCal.App.Tests;

// feedback 3-2: 사이드바 제목은 잘렸을 때만 전체 제목 툴팁을 띄운다.
public sealed class TextTrimTests
{
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
