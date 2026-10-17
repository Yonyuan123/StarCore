using StarCore.Core.App;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace StarCore.Desktop.Interop;

/// <summary>
/// 系统托盘图标（第 6 批）。
///
/// 为什么不用 WinForms 的 NotifyIcon：那需要把整个宿主切到 UseWindowsForms，
/// 为了一个托盘图标引入整套 WinForms 不值得，也会让发布产物体积变大。
/// 这里直接用 Shell_NotifyIcon，几十行就够，且行为与系统完全一致。
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private const int WM_APP = 0x8000;
    private const int WM_TRAYICON = WM_APP + 1;
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_LBUTTONDBLCLK = 0x0203;
    private const int WM_RBUTTONUP = 0x0205;

    private const int NIM_ADD = 0x0;
    private const int NIM_MODIFY = 0x1;
    private const int NIM_DELETE = 0x2;
    private const int NIF_MESSAGE = 0x1;
    private const int NIF_ICON = 0x2;
    private const int NIF_TIP = 0x4;

    private const int MF_STRING = 0x0;
    private const int MF_SEPARATOR = 0x800;
    private const uint TPM_LEFTALIGN = 0x0;
    private const uint TPM_RIGHTBUTTON = 0x2;
    private const uint TPM_RETURNCMD = 0x100;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID;
        public int uFlags;
        public int uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(int message, ref NOTIFYICONDATA data);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenu(IntPtr menu, int flags, int id, string text);

    [DllImport("user32.dll")]
    private static extern int TrackPopupMenu(IntPtr menu, uint flags, int x, int y, int reserved, IntPtr hwnd, IntPtr rect);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr menu);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT pt);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int ExtractIconEx(string file, int index, IntPtr[] large, IntPtr[]? small, int count);

    private readonly Window _window;
    private readonly Action _onOpen;
    private readonly Action _onExit;
    private IntPtr _hwnd;
    private IntPtr _hicon;
    private bool _added;
    private HwndSource? _source;

    public TrayIcon(Window window, Action onOpen, Action onExit)
    {
        _window = window;
        _onOpen = onOpen;
        _onExit = onExit;
    }

    public void Install()
    {
        if (_added) return;
        _hwnd = new WindowInteropHelper(_window).Handle;
        if (_hwnd == IntPtr.Zero) return;

        // 用可执行文件自带的图标（Win32 提取，不引 System.Drawing）
        try
        {
            var exe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exe))
            {
                var icons = new IntPtr[1];
                if (ExtractIconEx(exe, 0, icons, null, 1) > 0) _hicon = icons[0];
            }
        }
        catch { /* 拿不到图标就显示系统默认 */ }

        var data = new NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = _hwnd,
            uID = 1,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
            uCallbackMessage = WM_TRAYICON,
            hIcon = _hicon,
            szTip = "StarCore Launcher",
        };
        _added = Shell_NotifyIcon(NIM_ADD, ref data);

        // 托盘消息走窗口消息钩子
        _source = HwndSource.FromHwnd(_hwnd);
        _source?.AddHook(WndProc);
        Logger.Info($"[tray] 托盘图标已添加：{_added}");
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_TRAYICON) return IntPtr.Zero;

        var evt = lParam.ToInt32() & 0xFFFF;
        switch (evt)
        {
            case WM_LBUTTONUP:
            case WM_LBUTTONDBLCLK:
                _onOpen();
                handled = true;
                break;

            case WM_RBUTTONUP:
                ShowMenu();
                handled = true;
                break;
        }
        return IntPtr.Zero;
    }

    private void ShowMenu()
    {
        var menu = CreatePopupMenu();
        try
        {
            AppendMenu(menu, MF_STRING, 1, "打开 StarCore Launcher");
            AppendMenu(menu, MF_SEPARATOR, 0, "");
            AppendMenu(menu, MF_STRING, 2, "退出");

            GetCursorPos(out var pt);
            // 必须先置前台，否则点菜单外部收起时菜单会"粘"在屏幕上
            SetForegroundWindow(_hwnd);
            var cmd = TrackPopupMenu(menu, TPM_LEFTALIGN | TPM_RIGHTBUTTON | TPM_RETURNCMD,
                                     pt.X, pt.Y, 0, _hwnd, IntPtr.Zero);

            if (cmd == 1) _onOpen();
            else if (cmd == 2) _onExit();
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    public void Dispose()
    {
        if (_added)
        {
            var data = new NOTIFYICONDATA
            {
                cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
                hWnd = _hwnd,
                uID = 1,
            };
            Shell_NotifyIcon(NIM_DELETE, ref data);
            _added = false;
        }
        _source?.RemoveHook(WndProc);
        if (_hicon != IntPtr.Zero)
        {
            DestroyIcon(_hicon);
            _hicon = IntPtr.Zero;
        }
    }
}
