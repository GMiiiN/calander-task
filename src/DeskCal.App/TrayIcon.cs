using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Windows.Forms;
using DeskCal.Core;

namespace DeskCal.App;

/// <summary>
/// 알림 영역 아이콘과 메뉴(§8). 달력 창이 다시 만들어져도 그대로 남도록 App이 하나만 갖는다.
/// Explorer가 다시 시작되면(TaskbarCreated) NotifyIcon이 스스로 다시 등록한다.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private const string ClosePage = "<html><head><meta charset=\"utf-8\"><title>DeskCal</title></head>" +
        "<body style=\"font-family:sans-serif;padding:40px\"><h3>DeskCal에 Google 계정을 연결했습니다.</h3><p>이 창은 닫아도 됩니다.</p></body></html>";

    private readonly NotifyIcon _icon;
    private readonly GoogleAccount _google;
    private bool _isSigningIn;

    public TrayIcon(GoogleAccount google, Func<bool> isAdjusting, Action toggleAdjust, Action showSettings, Action exit)
    {
        _google = google;
        var connect = new ToolStripMenuItem("Google 연결", null, async (_, _) => await ConnectGoogleAsync());
        var adjust = new ToolStripMenuItem("크기·위치 조정", null, (_, _) => toggleAdjust());
        var startup = new ToolStripMenuItem("시작 시 실행", null, (_, _) => AutoStart.Set(!AutoStart.IsEnabled));
        var menu = new ContextMenuStrip();
        menu.Items.AddRange([connect, new ToolStripSeparator(), new ToolStripMenuItem("설정", null, (_, _) => showSettings()), adjust, startup,
            new ToolStripSeparator(), new ToolStripMenuItem("종료", null, (_, _) => exit())]);
        // 체크 표시는 열 때마다 실제 상태로 맞춘다(레지스트리는 다른 곳에서 바뀔 수 있다).
        menu.Opening += (_, _) =>
        {
            adjust.Checked = isAdjusting();
            startup.Checked = AutoStart.IsEnabled;
            connect.Text = _isSigningIn ? "Google 연결 중… (브라우저에서 로그인)"
                : _google.IsSignedIn ? $"Google 연결됨: {_google.Email}" : "Google 연결";
            connect.Enabled = !_isSigningIn && !_google.IsSignedIn;
        };
        _icon = new NotifyIcon { Icon = DrawIcon(), Text = "DeskCal", ContextMenuStrip = menu, Visible = true };
    }

    // 브라우저로 로그인한다(§5.1). 5분 안에 끝내지 않으면 그만둔다. 결과는 알림 풍선으로 알린다.
    private async Task ConnectGoogleAsync()
    {
        // 새 PC에는 클라이언트 파일이 없다. 다른 PC에서 가져온 파일(또는 Cloud 콘솔에서 받은 파일)을 골라 제자리에 복사한다.
        if (!_google.HasClientSecret && !ImportClientSecret()) return;
        _isSigningIn = true;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            var email = await _google.SignInAsync(ClosePage, timeout.Token);
            _icon.ShowBalloonTip(5000, "Google에 연결했습니다", email, ToolTipIcon.Info);
        }
        catch (InvalidOperationException exception) when (exception.Message == "Signed in with a different Google account.")
        {
            _icon.ShowBalloonTip(10000, "Google 연결", "처음 연결한 계정과 다른 계정입니다. 같은 계정으로 로그인해 주세요.", ToolTipIcon.Warning);
        }
        catch (Exception exception)
        {
            // 브라우저에서 거부하거나 5분이 지난 경우 등. 다시 누르면 처음부터 한다.
            _icon.ShowBalloonTip(10000, "Google에 연결하지 못했습니다", exception.Message, ToolTipIcon.Error);
        }
        finally
        {
            _isSigningIn = false;
        }
    }

    private bool ImportClientSecret()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Google OAuth 클라이언트 파일(client_secret….json)을 고르세요",
            Filter = "JSON 파일 (*.json)|*.json",
        };
        if (dialog.ShowDialog() != DialogResult.OK) return false;
        try
        {
            _google.ImportClientSecret(dialog.FileName);
            return true;
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException
            or Newtonsoft.Json.JsonException)
        {
            _icon.ShowBalloonTip(10000, "Google 연결", "OAuth 클라이언트 파일이 아닙니다. Google Cloud 콘솔에서 받은 데스크톱 앱용 JSON 파일을 골라 주세요.", ToolTipIcon.Warning);
            return false;
        }
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
