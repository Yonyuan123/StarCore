using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace StarCore.Desktop.Interop;

/// <summary>
/// 把 Windows 原生的窗口行为接进来。
///
/// 为什么需要它：界面跑在 WebView2 里，标题栏是 HTML 画的，
/// 因此系统的"拖拽移动窗口 / 双击最大化 / 圆角 / 深色边框"都要手动补上，
/// 否则用户体验会明显不对（拖不动、圆角是方的、深色下有一圈白边）。
/// </summary>
internal enum WindowCommand { Minimize, Maximize, Restore, Close }

internal static class NativeWindow
{
    private const int WM_NCLBUTTONDOWN = 0x00A1;
    private const int HTCAPTION = 0x0002;

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWA_BORDER_COLOR = 34;

    private enum CornerPreference
    {
        Default = 0,
        DoNotRound = 1,
        Round = 2,
        RoundSmall = 3,
    }

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    private const int WM_SYSCOMMAND = 0x0112;
    private const int SC_MINIMIZE = 0xF020;
    private const int SC_MAXIMIZE = 0xF030;
    private const int SC_RESTORE = 0xF120;
    private const int SC_CLOSE = 0xF060;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    /// <summary>
    /// 用系统命令最小化 / 最大化 / 还原 / 关闭窗口。
    ///
    /// 为什么不用 window.WindowState = … ：
    /// 1. 那是 UI 线程上的同步赋值，UI 线程一忙（页面重排、渲染），调用就要排队
    ///    —— 实测出现过一分钟才响应的极端情况；
    /// 2. 直接改 WindowState 会绕开 DWM，窗口没有系统的放大/还原动画。
    /// PostMessage 是异步投递，任意线程都能调，走的正是标题栏按钮那条系统路径，
    /// 系统动画、任务栏行为、贴边规则全都对。
    /// </summary>
    public static void PostCommand(IntPtr handle, WindowCommand command)
    {
        if (handle == IntPtr.Zero) return;
        var sc = command switch
        {
            WindowCommand.Minimize => SC_MINIMIZE,
            WindowCommand.Maximize => SC_MAXIMIZE,
            WindowCommand.Restore => SC_RESTORE,
            WindowCommand.Close => SC_CLOSE,
            _ => SC_RESTORE,
        };
        PostMessage(handle, WM_SYSCOMMAND, (IntPtr)sc, IntPtr.Zero);
    }

    /// <summary>
    /// 开始原生窗口拖拽。
    /// 比 <see cref="Window.DragMove"/> 更可靠：走的是系统非客户区拖拽，
    /// 因此能得到贴边吸附、多显示器正确处理、以及拖拽时的窗口预览。
    /// </summary>
    public static void BeginDrag(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;

        ReleaseCapture();
        SendMessage(handle, WM_NCLBUTTONDOWN, (IntPtr)HTCAPTION, IntPtr.Zero);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    private const uint MONITOR_DEFAULTTONEAREST = 2;

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

    /// <summary>
    /// 当前窗口所在显示器的工作区（DIP，已扣除任务栏）。
    /// WPF 的 Left/Top/Width/Height 都是 DIP，而 Win32 给的是物理像素，
    /// 所以必须除以 DPI 缩放，否则 125% / 150% 缩放下最大化会超出屏幕。
    /// </summary>
    public static Rect WorkAreaDip(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return SystemParameters.WorkArea;

        var monitor = MonitorFromWindow(handle, MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info))
            return SystemParameters.WorkArea;

        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(window);
        var r = info.rcWork;
        // 用窗口左上角像素坐标换算成 DIP，兼容负坐标（副屏在主屏左侧）
        var originX = r.Left / dpi.DpiScaleX;
        var originY = r.Top / dpi.DpiScaleY;
        return new Rect(originX, originY,
                        (r.Right - r.Left) / dpi.DpiScaleX,
                        (r.Bottom - r.Top) / dpi.DpiScaleY);
    }

    /// <summary>应用圆角、深色边框等 Windows 11 的窗口外观属性。</summary>
    public static void ApplyTheme(Window window, bool dark, bool maximized)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;

        try
        {
            var darkFlag = dark ? 1 : 0;
            DwmSetWindowAttribute(handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkFlag, sizeof(int));

            // 最大化时不要圆角，否则屏幕四角会露出桌面
            var corner = maximized ? CornerPreference.DoNotRound : CornerPreference.Round;
            var cornerValue = (int)corner;
            DwmSetWindowAttribute(handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref cornerValue, sizeof(int));

            // 边框颜色设为极深/极浅，避免深色主题下出现一圈突兀的白边
            var border = dark ? 0x000D0E0F : 0x00EEF1F2; // 0x00BBGGRR
            DwmSetWindowAttribute(handle, DWMWA_BORDER_COLOR, ref border, sizeof(int));
        }
        catch (Exception ex)
        {
            // 老系统（Windows 10）不支持这些属性，静默降级即可
            Core.App.Logger.Debug($"[window] 设置窗口外观失败（可忽略）：{ex.Message}");
        }
    }
}
