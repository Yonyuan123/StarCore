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

    public HostBridge(CoreWebView2 core, Window window)
    {
        _core = core;
        _window = window;
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

            var data = await Task.Run(() => DispatchAsync(method, prms));
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
    private static bool RequiresUiThread(string method) =>
        method is "window.minimize" or "window.toggleMaximize" or "window.close"
                or "window.drag" or "window.setTheme" or "window.state"
        || method.StartsWith("system.", StringComparison.Ordinal);

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
            case "window.minimize":
                _window.WindowState = WindowState.Minimized;
                return null;

            case "window.toggleMaximize":
                _window.WindowState = _window.WindowState == WindowState.Maximized
                    ? WindowState.Normal
                    : WindowState.Maximized;
                return new { maximized = _window.WindowState == WindowState.Maximized };

            case "window.close":
                _window.Close();
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
