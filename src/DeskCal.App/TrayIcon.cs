using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace DeskCal.App;

/// <summary>
/// 알림 영역 아이콘과 메뉴(§8). 달력 창이 다시 만들어져도 그대로 남도록 App이 하나만 갖는다.
/// Explorer가 다시 시작되면(TaskbarCreated) NotifyIcon이 스스로 다시 등록한다.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;

    public TrayIcon(Func<bool> isAdjusting, Action toggleAdjust, Action exit)
    {
        var adjust = new ToolStripMenuItem("크기·위치 조정", null, (_, _) => toggleAdjust());
        var startup = new ToolStripMenuItem("시작 시 실행", null, (_, _) => AutoStart.Set(!AutoStart.IsEnabled));
        var menu = new ContextMenuStrip();
        menu.Items.AddRange([adjust, startup, new ToolStripSeparator(), new ToolStripMenuItem("종료", null, (_, _) => exit())]);
        // 체크 표시는 열 때마다 실제 상태로 맞춘다(레지스트리는 다른 곳에서 바뀔 수 있다).
        menu.Opening += (_, _) =>
        {
            adjust.Checked = isAdjusting();
            startup.Checked = AutoStart.IsEnabled;
        };
        _icon = new NotifyIcon { Icon = DrawIcon(), Text = "DeskCal", ContextMenuStrip = menu, Visible = true };
    }

    // 파란 머리띠가 있는 작은 달력 모양. 아이콘 파일을 따로 두지 않고 시스템 작은 아이콘 크기에 맞춰 그린다.
    private static Icon DrawIcon()
    {
        var size = SystemInformation.SmallIconSize.Width;
        using var bitmap = new Bitmap(size, size);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var unit = size / 16f;
            var body = new RectangleF(1.5f * unit, 2.5f * unit, 13 * unit, 12 * unit);
            using var path = RoundedRectangle(body, 2.5f * unit);
            using var white = new SolidBrush(Color.White);
            using var blue = new SolidBrush(Color.FromArgb(0x24, 0x6B, 0xDE));
            graphics.FillPath(white, path);
            graphics.SetClip(new RectangleF(body.X, body.Y, body.Width, 4 * unit));
            graphics.FillPath(blue, path);
            graphics.ResetClip();
            using var outline = new Pen(Color.FromArgb(0x24, 0x6B, 0xDE), unit);
            graphics.DrawPath(outline, path);
            // 날짜 칸 점 3×2
            for (var row = 0; row < 2; row++)
                for (var column = 0; column < 3; column++)
                    graphics.FillRectangle(blue, (4 + column * 3) * unit, (8.5f + row * 3) * unit, 2 * unit, 2 * unit);
        }
        return Icon.FromHandle(bitmap.GetHicon());

        static GraphicsPath RoundedRectangle(RectangleF rect, float radius)
        {
            var path = new GraphicsPath();
            var diameter = radius * 2;
            path.AddArc(rect.X, rect.Y, diameter, diameter, 180, 90);
            path.AddArc(rect.Right - diameter, rect.Y, diameter, diameter, 270, 90);
            path.AddArc(rect.Right - diameter, rect.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(rect.X, rect.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
