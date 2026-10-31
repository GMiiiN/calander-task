using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;

namespace DeskCal.App;

public enum PinState { Normal, ShowDesktop, Editing }

/// <summary>
/// 창을 바탕화면 바로 위(모든 앱 아래)에 고정한다. 설계 §3.3의 Z순서 상태 머신.
/// </summary>
public sealed class DesktopPinning : IDisposable
{
    private const int GwlExStyle = -20;
    private const long WsExToolWindow = 0x00000080;
    private const long WsExNoActivate = 0x08000000;
    private const int WmWindowPosChanging = 0x0046;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint EventSystemForeground = 0x0003;
    private const uint WinEventOutOfContext = 0x0000;
    private const int GwlpHwndParent = -8;
    private const long WsExTopMost = 0x00000008;
    private const int ObjIdWindow = 0;
    private static readonly IntPtr HwndTop = IntPtr.Zero;
    private static readonly IntPtr HwndBottom = new(1);
    private static readonly IntPtr HwndTopMost = new(-1);
    private static readonly IntPtr HwndNoTopMost = new(-2);

    private readonly Window _window;
    private readonly string _logPath;
    private readonly WinEventDelegate _foregroundCallback; // 필드로 보관해 GC 수집을 막는다
    private readonly uint _taskbarCreatedMessage;
    private IntPtr _hwnd;
    private IntPtr _winEventHook;
    private bool _isRaising;
    private bool _isOwned;
    private readonly System.Windows.Threading.DispatcherTimer _showDesktopTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };

    public PinState State { get; private set; } = PinState.Normal;
    public event Action<PinState>? StateChanged;

    public DesktopPinning(Window window, string logPath)
    {
        _window = window;
        _logPath = logPath;
        _foregroundCallback = OnForegroundChanged;
        _taskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");
        _window.SourceInitialized += OnSourceInitialized;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(_window).Handle;
        var exStyle = GetWindowLongPtr(_hwnd, GwlExStyle).ToInt64();
        SetWindowLongPtr(_hwnd, GwlExStyle, new IntPtr(exStyle | WsExToolWindow | WsExNoActivate));
        // 방식 D(§3.3): 바탕화면 아이콘 목록 창을 owner로 두면 셸이 바탕화면의 일부로 취급해 Win+D에도 그대로 남는다.
        // 아이콘 목록 창을 못 찾는 셸에서만 아래의 Z순서 상태 머신(방식 C)으로 동작한다.
        if (AttachToDesktop())
        {
            HwndSource.FromHwnd(_hwnd)!.AddHook(OwnedWndProc);
            return;
        }
        // ShowInTaskbar=False면 WPF가 숨은 owner 창을 붙인다. owner가 우리 창의 Z순서를 끌고 다니므로 끊는다(M-1 진단).
        SetWindowLongPtr(_hwnd, GwlpHwndParent, IntPtr.Zero);
        HwndSource.FromHwnd(_hwnd)!.AddHook(WndProc);
        _winEventHook = SetWinEventHook(EventSystemForeground, EventSystemForeground, IntPtr.Zero,
            _foregroundCallback, 0, 0, WinEventOutOfContext);
        _showDesktopTimer.Tick += (_, _) => CheckShowDesktop();
        Log($"init hwnd=0x{_hwnd.ToInt64():X} hook=0x{_winEventHook.ToInt64():X}");
        SendToBottom();
    }

    // bringToFront: 조정 모드처럼 사용자가 달력 자체를 봐야 할 때 다른 앱 위로 꺼낸다.
    public void BeginEditing(bool bringToFront = false)
    {
        _showDesktopTimer.Stop();
        SetNoActivate(false);
        SetState(PinState.Editing);
        if (bringToFront) SetWindowPos(_hwnd, HwndTop, 0, 0, 0, 0, SwpNoMove | SwpNoSize);
    }

    // 편집이 끝나면 그때의 전경 창 기준으로 상태를 다시 정한다(바탕화면 보기 중이었으면 그대로 유지).
    public void EndEditing()
    {
        SetNoActivate(true);
        if (_isOwned)
        {
            // 바탕화면에 붙어 있으면 Z순서는 셸이 정한다. 조정 모드로 꺼냈던 것만 바탕화면 바로 위로 되돌린다.
            SetState(PinState.Normal);
            SetWindowPos(_hwnd, HwndBottom, 0, 0, 0, 0, SwpNoActivate | SwpNoMove | SwpNoSize);
            return;
        }
        SetState(PinState.Normal);
        if (IsDesktopClass(GetClassName(GetForegroundWindow()))) EnterShowDesktop();
        else SendToBottom();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmWindowPosChanging && State != PinState.Editing)
        {
            var pos = Marshal.PtrToStructure<WindowPos>(lParam);
            if ((pos.Flags & SwpNoZOrder) != 0) return IntPtr.Zero;
            if (State == PinState.Normal && pos.HwndInsertAfter != HwndBottom)
            {
                pos.HwndInsertAfter = HwndBottom;
                Marshal.StructureToPtr(pos, lParam, false);
            }
            else if (State == PinState.ShowDesktop && !_isRaising)
            {
                // 바탕화면 보기 중 외부에서 오는 강등은 거부한다. 우리의 해제는 상태를 Normal로 바꾼 뒤에 한다.
                pos.Flags |= SwpNoZOrder;
                Marshal.StructureToPtr(pos, lParam, false);
                Log($"blocked demotion insertAfter=0x{pos.HwndInsertAfter.ToInt64():X}");
            }
        }
        else if (msg == _taskbarCreatedMessage)
        {
            Log("TaskbarCreated (Explorer 재시작)");
            if (State == PinState.Normal) SendToBottom();
        }
        return IntPtr.Zero;
    }

    private void OnForegroundChanged(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (hwnd == IntPtr.Zero || idObject != ObjIdWindow || hwnd == _hwnd || State == PinState.Editing) return;
        var className = GetClassName(hwnd);
        Log($"foreground class={className}");
        if (IsDesktopClass(className)) EnterShowDesktop();
        else if (State == PinState.ShowDesktop && !IsShellOverlayClass(className) && !IsToolWindow(hwnd)) ExitShowDesktop();
    }

    // Windows 11의 바탕화면 보기에서는 Progman이 일반 창 위로 올라오고, 우리가 건 TOPMOST가 수백 ms 뒤 외부에서 풀린다(M-1 실측).
    // 그래서 바탕화면 보기 동안 타이머로 전경과 TOPMOST를 계속 확인한다(Rainmeter의 Show Desktop 처리와 같은 방식).
    private void EnterShowDesktop()
    {
        SetState(PinState.ShowDesktop);
        Raise("enter");
        _showDesktopTimer.Start();
    }

    private void ExitShowDesktop()
    {
        _showDesktopTimer.Stop();
        SetState(PinState.Normal);
        SendToBottom();
    }

    private void CheckShowDesktop()
    {
        var foreground = GetForegroundWindow();
        var className = GetClassName(foreground);
        if (!IsDesktopClass(className) && !IsShellOverlayClass(className) && className != "" && !IsToolWindow(foreground))
        {
            ExitShowDesktop();
            return;
        }
        if ((GetWindowLongPtr(_hwnd, GwlExStyle).ToInt64() & WsExTopMost) == 0) Raise("re-raise");
    }

    // WM_WINDOWPOSCHANGING에는 HWND_TOPMOST가 아니라 실제 삽입 위치 핸들이 오므로, 우리 호출인지는 플래그로 구분한다.
    private void Raise(string reason)
    {
        _isRaising = true;
        bool ok;
        try { ok = SetWindowPos(_hwnd, HwndTopMost, 0, 0, 0, 0, SwpNoActivate | SwpNoMove | SwpNoSize); }
        finally { _isRaising = false; }
        Log($"raised topmost ({reason}) ok={ok}" + (ok ? "" : $" err={Marshal.GetLastWin32Error()}"));
    }

    private static bool IsDesktopClass(string className) => className is "WorkerW" or "Progman";

    // 바탕화면 보기 중에 열어도 바탕화면이 그대로인 셸 요소들(작업 표시줄, 시작·검색, 알림 영역).
    private static bool IsShellOverlayClass(string className) => className is
        "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "Windows.UI.Core.CoreWindow" or
        "NotifyIconOverflowWindow" or "TopLevelWindowForOverflowXamlIsland" or "XamlExplorerHostIslandWindow";

    private bool AttachToDesktop()
    {
        var folderView = FindDesktopFolderView();
        _isOwned = folderView != IntPtr.Zero;
        if (_isOwned) SetWindowLongPtr(_hwnd, GwlpHwndParent, folderView);
        Log($"attach owner=0x{folderView.ToInt64():X}");
        return _isOwned;
    }

    // Explorer가 다시 시작되면 아이콘 목록 창이 새로 만들어지므로 owner를 다시 건다.
    private IntPtr OwnedWndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == _taskbarCreatedMessage)
        {
            Log("TaskbarCreated (Explorer 재시작)");
            AttachToDesktop();
        }
        return IntPtr.Zero;
    }

    // 바탕화면 아이콘 목록 창. 셸 보기 창은 Progman 아래 또는 (배경 전환 후) WorkerW 아래에 있다.
    private static IntPtr FindDesktopFolderView()
    {
        var defView = FindWindowEx(FindWindow("Progman", null), IntPtr.Zero, "SHELLDLL_DefView", null);
        var worker = IntPtr.Zero;
        while (defView == IntPtr.Zero && (worker = FindWindowEx(IntPtr.Zero, worker, "WorkerW", null)) != IntPtr.Zero)
            defView = FindWindowEx(worker, IntPtr.Zero, "SHELLDLL_DefView", null);
        return FindWindowEx(defView, IntPtr.Zero, "SysListView32", null);
    }

    // 작업 표시줄에 없는 도구 창(다른 바탕화면 위젯 등)은 Win+D 때 잠깐 전경이 되기도 한다. 앱 전환으로 보지 않는다.
    private static bool IsToolWindow(IntPtr hwnd) => (GetWindowLongPtr(hwnd, GwlExStyle).ToInt64() & WsExToolWindow) != 0;

    private void SendToBottom()
    {
        SetWindowPos(_hwnd, HwndNoTopMost, 0, 0, 0, 0, SwpNoActivate | SwpNoMove | SwpNoSize);
        SetWindowPos(_hwnd, HwndBottom, 0, 0, 0, 0, SwpNoActivate | SwpNoMove | SwpNoSize);
    }

    private void SetNoActivate(bool enabled)
    {
        var exStyle = GetWindowLongPtr(_hwnd, GwlExStyle).ToInt64();
        exStyle = enabled ? exStyle | WsExNoActivate : exStyle & ~WsExNoActivate;
        SetWindowLongPtr(_hwnd, GwlExStyle, new IntPtr(exStyle));
    }

    private void SetState(PinState state)
    {
        if (State == state) return;
        Log($"state {State} -> {state}");
        State = state;
        StateChanged?.Invoke(state);
    }

    private void Log(string message) =>
        File.AppendAllText(_logPath, $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");

    private static string GetClassName(IntPtr hwnd)
    {
        var buffer = new StringBuilder(256);
        return GetClassNameW(hwnd, buffer, buffer.Capacity) > 0 ? buffer.ToString() : "";
    }

    public void Dispose()
    {
        _showDesktopTimer.Stop();
        if (_winEventHook != IntPtr.Zero) UnhookWinEvent(_winEventHook);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowPos
    {
        public IntPtr Hwnd;
        public IntPtr HwndInsertAfter;
        public int X, Y, Cx, Cy;
        public uint Flags;
    }

    private delegate void WinEventDelegate(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

    [DllImport("user32.dll")] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll")] private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string? className, string? title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string? className, string? title);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEventDelegate callback, uint process, uint thread, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetClassNameW")] private static extern int GetClassNameW(IntPtr hwnd, StringBuilder name, int max);
}
