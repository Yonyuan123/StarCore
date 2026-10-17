using System.IO;
using System.Text.Json;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using StarCore.Core;
using StarCore.Core.App;
using StarCore.Desktop.Interop;

namespace StarCore.Desktop.Bridge;

/// <summary>
/// JS ↔ C# 的消息中枢。
///
/// 协议（与 wwwroot/scripts/bridge.js 一一对应）：
///   收  { kind:"invoke", id, method, params }
///   发  { kind:"result", id, ok, data | error }
///   发  { kind:"event",  name, payload }
///
/// 全部消息都在 UI 线程上处理，但耗时调用会 await 到线程池，
/// 因此长时间的下载/安装在执行期间界面依然可交互。
/// </summary>
internal sealed class HostBridge
{
    private readonly CoreWebView2 _core;
    private readonly Window _window;

    /// <summary>窗口句柄：PostMessage 用，任意线程可读（创建后不变）。</summary>
    private IntPtr _hwnd;

    /// <summary>非 UI 线程上无法安全读 WindowState，这里自己记一份。</summary>
    private bool _lastMaximized;

    private IntPtr Hwnd => _hwnd != IntPtr.Zero
        ? _hwnd
        : (_hwnd = new System.Windows.Interop.WindowInteropHelper(_window).Handle);

    public HostBridge(CoreWebView2 core, Window window)
    {
        _core = core;
        _window = window;
        // 窗口创建后句柄就固定了，先缓存好，避免在后台线程里现取
        window.SourceInitialized += (_, _) =>
            _hwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;
        window.StateChanged += (_, _) =>
        {
            if (_window.Dispatcher.CheckAccess())
                _lastMaximized = window.WindowState == WindowState.Maximized;
        };
    }

    public void Attach()
    {
        _core.WebMessageReceived += OnMessage;
        StarCoreHost.Event += OnCoreEvent;
    }

    public void Detach()
    {
        _core.WebMessageReceived -= OnMessage;
        StarCoreHost.Event -= OnCoreEvent;
    }

    /* ==================================================================
       内核 → 前端
       ================================================================== */

    private void OnCoreEvent(string name, object? payload)
    {
        // 内核事件可能来自任意线程，PostWebMessageAsString 必须在 UI 线程调用
        Post(new { kind = "event", name, payload });
    }

    private void Post(object message)
    {
        var json = JsonSerializer.Serialize(message, Json.Options);
        var dispatcher = _window.Dispatcher;

        if (dispatcher.CheckAccess())
        {
            TryPost(json);
        }
        else
        {
            dispatcher.BeginInvoke(() => TryPost(json));
        }
    }

    private long _postFailures;

    private void TryPost(string json)
    {
        // 这里刻意**不写日志**：推送失败最常见的场景正是"界面进程已退出"，
        // 而写日志会再触发一次事件推送，形成自激递归。用计数器代替，静默降级。
        try { _core.PostWebMessageAsString(json); }
        catch { Interlocked.Increment(ref _postFailures); }
    }

    /* ==================================================================
       前端 → 内核
       ================================================================== */

    private async void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        /* ⚠️ 内核调用必须离开 UI 线程，别把 Task.Run 去掉。
           WebView2 是在 UI 线程上抛 WebMessageReceived 的，而内核方法里有大量
           **同步**的文件系统工作 —— 光是 instance.list 就会对每个实例递归遍历目录
           统计体积（几百 MB、上万个文件，每个文件一次 stat）。这些同步部分如果
           跑在 UI 线程上，界面线程就被占住了，用户看到的是：
             · 拖窗口/点最大化要顿一下才响应
             · 关闭窗口"点了没反应"，过一会儿才消失
           内核（StarCore.Core）本身没有任何 UI 依赖，所以整段搬到线程池是安全的；
           回包的 Post() 自己会切回 UI 线程。 */
        string raw;
        try { raw = e.TryGetWebMessageAsString(); }
        catch (Exception ex)
        {
            Logger.Warn($"[bridge] 无法读取前端消息：{ex.Message}");
            return;
        }

        string id = "";
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return;

            id = root.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";
            var method = root.TryGetProperty("method", out var mEl) ? mEl.GetString() ?? "" : "";
            var prms = root.TryGetProperty("params", out var pEl) ? pEl.Clone() : default;

            var started = System.Diagnostics.Stopwatch.GetTimestamp();

            /* 窗口控制：同步直达，不进线程池、不排队、不等 await。
               WebMessageReceived 本身就在 UI 线程，这里直接投递系统命令即可 ——
               从点击到窗口响应只有一次进程内调用。
               这是对标 PCL 的关键点：PCL 是纯 WPF，按钮点击就在主线程上直接执行，
               中间没有任何跨进程往返；我们没法去掉 WebView2 这一层，
               但可以把它压成"一次同步调用"，效果等价。
               其余方法仍然必须走线程池（内核里有大量同步文件 IO）。 */
            if (method is "window.minimize" or "window.toggleMaximize" or "window.close")
            {
                var winData = DispatchWindowCommand(method);
                var winMs = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                Logger.Info($"[bridge] {method} 直达耗时 {winMs:F1} ms");
                Post(new { kind = "result", id, ok = true, data = winData });
                return;
            }

            var data = await Task.Run(() => DispatchAsync(method, prms));

            // 窗口控制单独计时：用户报过"点按钮要等一分钟"，
            // 这类问题必须能从日志里直接看出耗时，而不是靠感觉。
            if (method.StartsWith("window.", StringComparison.Ordinal) && method != "window.drag")
            {
                var ms = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                Logger.Info($"[bridge] {method} 处理耗时 {ms:F1} ms");
            }

            Post(new { kind = "result", id, ok = true, data });
        }
        catch (Exception ex)
        {
            Logger.Error(ex, $"[bridge] 调用失败");
            Post(new
            {
                kind = "result",
                id,
                ok = false,
                error = new
                {
                    code = ex.GetType().Name,
                    message = Friendly(ex),
                    detail = ex.StackTrace ?? "",
                },
            });
        }
    }

    private static string Friendly(Exception ex) => ex switch
    {
        InvalidOperationException => ex.Message,
        UnauthorizedAccessException => "没有权限访问该文件或目录。",
        IOException => $"读写文件时出错：{ex.Message}",
        System.Net.Http.HttpRequestException => $"网络请求失败：{ex.Message}",
        TaskCanceledException => "操作已取消或超时。",
        _ => ex.Message,
    };

    /// <summary>
    /// 这些方法直接操作 WPF 窗口对象，只能在 UI 线程上跑。
    /// 其余方法一律在线程池上跑（见 <see cref="OnMessage"/> 里的说明）。
    ///
    /// 白名单而不是黑名单：新加一个"也碰窗口"的方法时，忘了登记会立刻抛
    /// InvalidOperationException（WPF 在非创建线程访问 Window 会明确报错），
    /// 不会变成一个偶发的诡异现象。
    /// </summary>
    /// <summary>
    /// 三个窗口按钮的同步实现（只允许在 UI 线程调用）。
    /// 直接投递系统命令：异步、带 DWM 动画、不受渲染压力影响。
    /// </summary>
    private object? DispatchWindowCommand(string method)
    {
        switch (method)
        {
            case "window.minimize":
                NativeWindow.PostCommand(Hwnd, WindowCommand.Minimize);
                return null;

            case "window.toggleMaximize":
            {
                /* 系统命令，不是自己画的动画。
                   SC_MAXIMIZE / SC_RESTORE 由 Windows 自己处理，
                   DWM 会播原生的最大化/还原过渡动画（缩放 + 淡入）；
                   自绘的几何补间做不到这一点，而且在 WebView2 上
                   逐帧改窗口尺寸会让渲染进程反复重排，直接卡死。 */
                var maximized = _window.WindowState == WindowState.Maximized;
                NativeWindow.PostCommand(Hwnd, maximized ? WindowCommand.Restore : WindowCommand.Maximize);
                _lastMaximized = !maximized;
                return new { maximized = !maximized };
            }

            default:
                NativeWindow.PostCommand(Hwnd, WindowCommand.Close);
                return null;
        }
    }

    private static bool RequiresUiThread(string method) =>
        // 只保留"真的必须碰 UI 线程"的：
        //   window.drag / setTheme / state —— 直接操作 WPF 窗口对象
        //   system.pick* —— 模态对话框
        // 最小化 / 最大化 / 关闭改走 PostMessage（任意线程安全）；
        // 系统级文件操作不需要 UI 线程。它们原来被一起塞进白名单，
        // 只要 UI 线程一忙按钮就要排队 —— 实测出现过一分钟才响应。
        method is "window.drag" or "window.setTheme" or "window.state"
        || method is "system.pickFile" or "system.pickFiles" or "system.pickFolder";

    private Task<object?> DispatchAsync(string method, JsonElement prms)
    {
        if (!RequiresUiThread(method)) return DispatchCoreAsync(method, prms);

        // 已经在 UI 线程就直接跑，否则派回去等结果
        if (_window.Dispatcher.CheckAccess()) return DispatchCoreAsync(method, prms);
        return _window.Dispatcher.InvokeAsync(() => DispatchCoreAsync(method, prms)).Task.Unwrap();
    }

    private async Task<object?> DispatchCoreAsync(string method, JsonElement prms)
    {
        // ---- 窗口类：由宿主直接处理，不进内核 ----
        switch (method)
        {
            // 三个窗口命令全部走系统消息：异步投递、不等 UI 线程，
            // 同时获得 Windows 自带的最小化/最大化动画（见 NativeWindow.PostCommand）。
            case "window.minimize":
                NativeWindow.PostCommand(Hwnd, WindowCommand.Minimize);
                return null;

            case "window.toggleMaximize":
            {
                // 非 UI 线程读 WindowState 不安全，用自己跟踪的状态兜底
                var maximized = _window.Dispatcher.CheckAccess()
                    ? _window.WindowState == WindowState.Maximized
                    : _lastMaximized;
                NativeWindow.PostCommand(Hwnd, maximized ? WindowCommand.Restore : WindowCommand.Maximize);
                _lastMaximized = !maximized;
                return new { maximized = !maximized };
            }

            case "window.close":
                NativeWindow.PostCommand(Hwnd, WindowCommand.Close);
                return null;

            case "window.drag":
                NativeWindow.BeginDrag(_window);
                return null;

            case "window.setTheme":
            {
                // 界面主题变化时同步到原生窗口（圆角、深色边框、窗口底色）
                var dark = prms.ValueKind == JsonValueKind.Object &&
                           prms.TryGetProperty("dark", out var d) &&
                           d.ValueKind == JsonValueKind.True;
                if (_window is MainWindow mw) mw.SetDarkTheme(dark);
                else NativeWindow.ApplyTheme(_window, dark, _window.WindowState == WindowState.Maximized);
                return null;
            }

            case "window.state":
                return new { maximized = _window.WindowState == WindowState.Maximized };
        }

        // ---- 系统类：需要 WPF 的对话框与 Shell，留在宿主 ----
        if (method.StartsWith("system.", StringComparison.Ordinal))
            return SystemActions.Invoke(method, prms);

        // ---- 其余全部交给内核 ----
        return await StarCoreHost.InvokeAsync(method, prms);
    }
}
