using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using StarCore.Core;
using StarCore.Core.App;
using StarCore.Core.Services;
using StarCore.Desktop.Bridge;

namespace StarCore.Desktop;

public partial class App : Application
{
    /* ==================================================================
       单实例守卫

       为什么必须有：WebView2 的用户数据目录是**独占**的。用户双击两次图标
       （或第一次启动较慢时又点了一下），第二个进程会在
       CreateCoreWebView2ControllerAsync 处抛 COMException 0x8007139F
       （"组或资源的状态不是执行请求操作的正确状态"），然后整个进程直接消失。
       对用户来说就是"点了没反应"或者"闪一下就没了"，完全无法理解发生了什么。

       正确做法是只允许一个实例，第二个实例把已有窗口叫到前台然后自己退出。
       ================================================================== */

    private Mutex? _singleInstanceMutex;

    private bool AcquireSingleInstance()
    {
        // Local\ 前缀即可：同一登录会话内互斥就够了，也不需要跨会话抢占
        _singleInstanceMutex = new Mutex(true, @"Local\StarCore.SingleInstance", out var created);
        if (created) return true;

        // 已经有实例在跑 —— 把它的窗口叫到最前面，然后本进程安静退出
        Logger.Info("[app] 检测到已有实例正在运行，激活其窗口后退出");
        ActivateExistingWindow();
        return false;
    }

    private const uint SEM_FAILCRITICALERRORS = 0x0001;
    private const uint SEM_NOOPENFILEERRORBOX = 0x8000;

    [DllImport("kernel32.dll")] private static extern uint SetErrorMode(uint uMode);

    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    private static void ActivateExistingWindow()
    {
        try
        {
            var me = Environment.ProcessId;
            foreach (var proc in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? "StarCore")))
            {
                using (proc)
                {
                    if (proc.Id == me) continue;
                    if (proc.MainWindowHandle == IntPtr.Zero) continue;
                    ShowWindow(proc.MainWindowHandle, 9);   // SW_RESTORE
                    SetForegroundWindow(proc.MainWindowHandle);
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Debug($"[app] 激活已有窗口失败：{ex.Message}");
        }
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 抑制系统级的"弹出框"错误模式。
        //
        // 场景：某个 java.exe 残缺时，Windows 加载器会弹一个模态的
        // 「无法找到入口：无法定位程序输入点 JLI_MemFree」对话框，
        // 而它是**阻塞**的 —— 我们的进程会卡在那个 CreateProcess 上直到用户点确定，
        // 超时机制也救不回来。设置这个标志后，子进程会继承该模式，加载失败直接返回
        // 错误码而不是弹框，我们就能用正常异常路径处理。
        try { SetErrorMode(SEM_FAILCRITICALERRORS | SEM_NOOPENFILEERRORBOX); }
        catch { /* 非致命 */ }

        if (!AcquireSingleInstance())
        {
            Shutdown(0);
            return;
        }

        // 内核初始化：解析路径、读设置、载入账户与实例索引
        try
        {
            // 注册当前平台实现。内核只依赖 IRuntimePlatform 接口，
        // 桌面宿主在启动时给它一份 Windows 实现（未来安卓版给安卓实现即可）。
        StarCore.Core.Platform.PlatformServices.AutoDetect();

        StarCoreHost.Initialize();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"StarCore 内核初始化失败：\n\n{ex.Message}", "StarCore",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        // 未捕获异常：写日志 + 尽量不让进程直接消失
        DispatcherUnhandledException += OnDispatcherException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Logger.Error(args.ExceptionObject as Exception ?? new Exception("未知异常"), "[app] 未处理异常");

        var window = new MainWindow();

        // 支持从桌面快捷方式直接启动某个实例：StarCore.exe --launch <id>
        var launchId = ParseLaunchArgument(e.Args);
        if (launchId is not null) window.PendingLaunchInstanceId = launchId;

        // 开发/自检用：--page instances:mods 直接打开指定页面，--theme dark 强制主题
        window.PendingPage = ParseValueArgument(e.Args, "--page");
        window.PendingTheme = ParseValueArgument(e.Args, "--theme");
        window.PendingAccent = ParseValueArgument(e.Args, "--accent");
        window.MeasureFps = e.Args.Contains("--fps");
        // 开发用：--dev-page _preview.html 直接打开 wwwroot 下的任意页面（绕开路由），
        // 用于渲染自检页、样式比对等一次性验证。
        window.DevPage = ParseValueArgument(e.Args, "--dev-page");

        MainWindow = window;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { _singleInstanceMutex?.ReleaseMutex(); } catch { }
        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }

    private static string? ParseValueArgument(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == name && !string.IsNullOrWhiteSpace(args[i + 1])) return args[i + 1];
        }
        return null;
    }

    private static long? ParseLaunchArgument(string[] args)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] is "--launch" or "-l" && long.TryParse(args[i + 1], out var id)) return id;
        }
        return null;
    }

    private void OnDispatcherException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Logger.Error(e.Exception, "[app] 界面线程未处理异常");

        // 已经在关闭流程里就别再弹窗了
        if (e.Exception is TaskCanceledException or OperationCanceledException)
        {
            e.Handled = true;
            return;
        }

        try
        {
            MessageBox.Show(
                $"发生了未预期的错误：\n\n{e.Exception.Message}\n\n日志位置：\n{StarCorePaths.LogsRoot}",
                "StarCore", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch { /* 连弹窗都失败就只留日志 */ }

        e.Handled = true;
    }
}
