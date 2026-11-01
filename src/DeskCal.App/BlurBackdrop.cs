using System.Numerics;
using System.Runtime.InteropServices;
using System.Windows;
using Windows.UI.Composition;
using Windows.UI.Composition.Desktop;

namespace DeskCal.App;

/// <summary>
/// 카드 뒤 바탕화면을 흐리게 비춘다. 흐림 전용 창(내용 없음, 클릭 통과)에 시스템 배경 흐림(HostBackdropBrush)을 카드 모양으로 칠한다.
/// 달력 창(레이어드 WPF 창)에 직접 흐림을 걸면 창 영역(region)과 상관없이 창 직사각형 전체가 흐려지고,
/// 같은 창에 Composition을 붙이면 WPF 내용 위에 그려지므로, 흐림 창을 달력 창 바로 아래(달력 창의 owner)에 둔다.
/// </summary>
public sealed unsafe class BlurBackdrop : IDisposable
{
    private const int WS_POPUP = unchecked((int)0x80000000);
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_NOREDIRECTIONBITMAP = 0x00200000;
    private const int DWMWA_USE_HOSTBACKDROPBRUSH = 17;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_DONOTROUND = 1;
    private const int SW_SHOWNOACTIVATE = 4;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private static readonly Guid CompositorDesktopInterop = new("29E691FA-4567-4DCA-B319-D0F207EB6807");

    // Compositor는 UI 스레드의 DispatcherQueue가 있어야 만들 수 있고, DispatcherQueue는 스레드마다 하나뿐이다.
    // 달력 창은 Explorer 재시작 때 다시 만들어지므로 프로세스에 하나만 두고 같이 쓴다.
    private static Compositor? s_compositor;
    private static IntPtr s_queueController; // 프로세스가 끝날 때까지 놓지 않는다

    private readonly System.Windows.Forms.NativeWindow _window = new();
    private readonly DesktopWindowTarget _target;
    private readonly ContainerVisual _root;
    private readonly CompositionBrush _brush;

    private BlurBackdrop()
    {
        if (s_compositor is null)
        {
            Marshal.ThrowExceptionForHR(CreateDispatcherQueueController(new DispatcherQueueOptions { Size = 12, ThreadType = 2, ApartmentType = 2 }, out s_queueController));
            s_compositor = new Compositor();
        }
        _window.CreateHandle(new System.Windows.Forms.CreateParams
        {
            Style = WS_POPUP, ExStyle = WS_EX_NOREDIRECTIONBITMAP | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT,
        });
        var on = 1;
        Marshal.ThrowExceptionForHR(DwmSetWindowAttribute(Handle, DWMWA_USE_HOSTBACKDROPBRUSH, ref on, sizeof(int)));
        var corner = DWMWCP_DONOTROUND;
        DwmSetWindowAttribute(Handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));

        // ICompositorDesktopInterop::CreateDesktopWindowTarget(vtable 3)
        var unknown = ((WinRT.IWinRTObject)s_compositor).NativeObject.ThisPtr;
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(unknown, in CompositorDesktopInterop, out var interop));
        try
        {
            IntPtr raw;
            var create = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int, IntPtr*, int>)(*(*(void***)interop + 3));
            Marshal.ThrowExceptionForHR(create(interop, Handle, 1, &raw));
            _target = DesktopWindowTarget.FromAbi(raw);
        }
        finally
        {
            Marshal.Release(interop);
        }
        _root = s_compositor.CreateContainerVisual();
        _target.Root = _root;
        _brush = s_compositor.CreateHostBackdropBrush();
    }

    public IntPtr Handle => _window.Handle;

    // 실패하면(오래된 Windows, 투명 효과를 쓸 수 없는 환경 등) 흐림 없이 그대로 쓴다.
    public static BlurBackdrop? TryCreate(Action<string> log)
    {
        try
        {
            return new BlurBackdrop();
        }
        catch (Exception exception)
        {
            log($"blur unavailable: {exception.GetType().Name}: {exception.Message}");
            return null;
        }
    }

    // window: 달력 창의 화면 사각형(물리 픽셀). cards: 창 안 카드 사각형(물리 픽셀). radius: 모서리 반지름(물리 픽셀).
    public void Place(Int32Rect window, IReadOnlyList<Rect> cards, float radius)
    {
        SetWindowPos(Handle, IntPtr.Zero, window.X, window.Y, window.Width, window.Height, SWP_NOZORDER | SWP_NOACTIVATE);
        ShowWindow(Handle, SW_SHOWNOACTIVATE);
        _root.Children.RemoveAll();
        foreach (var card in cards)
        {
            var visual = s_compositor!.CreateSpriteVisual();
            visual.Offset = new Vector3((float)card.X, (float)card.Y, 0);
            visual.Size = new Vector2((float)card.Width, (float)card.Height);
            visual.Brush = _brush;
            var shape = s_compositor.CreateRoundedRectangleGeometry();
            shape.Size = visual.Size;
            shape.CornerRadius = new Vector2(radius);
            visual.Clip = s_compositor.CreateGeometricClip(shape);
            _root.Children.InsertAtTop(visual);
        }
    }

    public void Dispose()
    {
        _root.Dispose();
        _target.Dispose();
        _window.DestroyHandle();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DispatcherQueueOptions
    {
        public int Size;
        public int ThreadType;    // 2 = 현재 스레드
        public int ApartmentType; // 2 = STA
    }

    [DllImport("CoreMessaging.dll")] private static extern int CreateDispatcherQueueController(DispatcherQueueOptions options, out IntPtr controller);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int command);
}
