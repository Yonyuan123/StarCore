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
