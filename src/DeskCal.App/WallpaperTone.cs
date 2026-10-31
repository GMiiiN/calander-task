using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace DeskCal.App;

/// <summary>
/// 카드는 투명해서 뒤의 바탕화면이 그대로 비친다. 그 위의 글자가 읽히도록, 창 뒤 배경화면이 어두운지 밝은지 잰다.
/// </summary>
public sealed class WallpaperTone
{
    private const uint SPI_GETDESKWALLPAPER = 0x0073;
    private const double DARK_THRESHOLD = 0.5;
    private const int COLOR_DESKTOP = 1;

    private readonly Window _window;
    private BitmapSource? _wallpaper;
    private byte[]? _pixels;
    private bool _hasMeasured;

    /// <summary>창 뒤 배경화면이 어두운지. 바뀔 때마다 DarkChanged로 알린다.</summary>
    public bool IsDark { get; private set; }
    public event Action<bool>? DarkChanged;

    public WallpaperTone(Window window)
    {
        _window = window;
        _window.LocationChanged += (_, _) => Measure();
        _window.SizeChanged += (_, _) => Measure();
        // 배경화면을 바꾸면 다시 읽는다.
        SystemEvents.UserPreferenceChanged += OnPreferenceChanged;
        _window.Closed += (_, _) => SystemEvents.UserPreferenceChanged -= OnPreferenceChanged;
        Reload();
    }

    private void OnPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category == UserPreferenceCategory.Desktop) _window.Dispatcher.BeginInvoke(Reload);
    }

    private void Reload()
    {
        _wallpaper = LoadWallpaperImage() ?? SolidDesktopColor();
        var converted = new FormatConvertedBitmap(_wallpaper, PixelFormats.Bgra32, null, 0);
        _pixels = new byte[converted.PixelWidth * converted.PixelHeight * 4];
        converted.CopyPixels(_pixels, converted.PixelWidth * 4, 0);
        Measure();
    }

    // 창 뒤에 해당하는 배경화면 픽셀의 평균 밝기로 판단한다. 배경화면은 주 모니터(DIP 0,0 기준)에 그려져 있다고 본다.
    private void Measure()
    {
        if (_wallpaper is null || _pixels is null) return;
        var screen = new Rect(0, 0, SystemParameters.PrimaryScreenWidth, SystemParameters.PrimaryScreenHeight);
        var drawn = WallpaperRect(_wallpaper.PixelWidth, _wallpaper.PixelHeight, screen);
        var scaleX = _wallpaper.PixelWidth / drawn.Width;
        var scaleY = _wallpaper.PixelHeight / drawn.Height;
        var left = Math.Clamp((int)((_window.Left - drawn.X) * scaleX), 0, _wallpaper.PixelWidth - 1);
        var top = Math.Clamp((int)((_window.Top - drawn.Y) * scaleY), 0, _wallpaper.PixelHeight - 1);
        var right = Math.Clamp((int)((_window.Left + _window.Width - drawn.X) * scaleX), left + 1, _wallpaper.PixelWidth);
        var bottom = Math.Clamp((int)((_window.Top + _window.Height - drawn.Y) * scaleY), top + 1, _wallpaper.PixelHeight);
        double sum = 0;
        var count = 0;
        for (var y = top; y < bottom; y += 4)
        for (var x = left; x < right; x += 4)
        {
            var index = (y * _wallpaper.PixelWidth + x) * 4;
            sum += 0.0722 * _pixels[index] + 0.7152 * _pixels[index + 1] + 0.2126 * _pixels[index + 2];
            count++;
        }
        var isDark = sum / count / 255 < DARK_THRESHOLD;
        if (_hasMeasured && isDark == IsDark) return;
        _hasMeasured = true;
        IsDark = isDark;
        DarkChanged?.Invoke(isDark);
    }

    // 배경화면 맞춤 방식(설정 > 개인 설정 > 배경). 채우기(기본)·맞춤·늘이기만 구분하고 나머지는 채우기로 본다.
    private static Rect WallpaperRect(double width, double height, Rect screen)
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop");
        var style = key?.GetValue("WallpaperStyle") as string;
        if (style == "2") return screen;
        var scale = style == "6"
            ? Math.Min(screen.Width / width, screen.Height / height)
            : Math.Max(screen.Width / width, screen.Height / height);
        var size = new Size(width * scale, height * scale);
        return new Rect(screen.X + (screen.Width - size.Width) / 2, screen.Y + (screen.Height - size.Height) / 2, size.Width, size.Height);
    }

    // 배경화면 이미지가 없으면(단색 배경) 바탕화면 색 한 장으로 대신한다.
    private static BitmapSource SolidDesktopColor()
    {
        var bgr = GetSysColor(COLOR_DESKTOP);
        var pixels = new byte[] { (byte)(bgr >> 16), (byte)(bgr >> 8), (byte)bgr, 255 };
        var bitmap = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, pixels, 4);
        bitmap.Freeze();
        return bitmap;
    }

    private static BitmapSource? LoadWallpaperImage()
    {
        var path = new StringBuilder(260);
        if (!SystemParametersInfo(SPI_GETDESKWALLPAPER, (uint)path.Capacity, path, 0) || !File.Exists(path.ToString())) return null;
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad; // 파일을 잠그지 않는다
            bitmap.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            bitmap.UriSource = new Uri(path.ToString());
            bitmap.DecodePixelWidth = 640; // 밝기만 재므로 작게 읽는다
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception exception) when (exception is IOException or NotSupportedException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    [DllImport("user32.dll")]
    private static extern uint GetSysColor(int index);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool SystemParametersInfo(uint action, uint param, StringBuilder value, uint winIni);
}
