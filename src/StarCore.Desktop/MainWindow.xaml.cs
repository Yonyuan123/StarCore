using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using StarCore.Core;
using StarCore.Core.App;
using StarCore.Core.Models;
using StarCore.Core.Services;
using StarCore.Desktop.Bridge;
using StarCore.Desktop.Interop;

namespace StarCore.Desktop;

public partial class MainWindow : Window
{
    private HostBridge? _bridge;
    private bool _dark;

    /// <summary>由命令行 --launch 指定：界面就绪后自动启动这个实例。</summary>
    public long? PendingLaunchInstanceId { get; set; }

    /// <summary>由命令行 --page 指定：界面就绪后直接打开该页面（如 instances:mods）。</summary>
    public string? PendingPage { get; set; }

    /// <summary>由命令行 --theme / --accent 指定：覆盖主题，仅用于自检与截图。</summary>
    public string? PendingTheme { get; set; }
    public string? PendingAccent { get; set; }

    /// <summary>由命令行 --fps 指定：采样真实帧率并写进日志，用于性能自检。</summary>
    public bool MeasureFps { get; set; }

    /// <summary>控制台转发只挂一次，重复挂接会重复记日志。</summary>
    private bool _consoleWired;

    /// <summary>由命令行 --dev-page 指定：直接打开 wwwroot 下的某个页面，绕开路由。</summary>
    public string? DevPage { get; set; }

    public MainWindow()
    {
        InitializeComponent();

        StateChanged += OnWindowStateChanged;
        Loaded += OnLoaded;
        Closing += OnClosing;

        // 游戏启动后按设置处理启动器窗口（启动后自动关闭 / 隐藏到托盘）
        InstallLaunchHook();
    }

    /* ==================================================================
       初始化
       ================================================================== */

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await InitializeWebViewAsync();
    }

    private async Task InitializeWebViewAsync()
    {
        var wwwroot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        if (!Directory.Exists(wwwroot))
        {
            ShowFallback("界面资源缺失", $"未找到 {wwwroot}\n请确认 wwwroot 目录随程序一起发布。");
            return;
        }

        try
        {
            var userData = Path.Combine(StarCorePaths.DataRoot, "webview");
            Directory.CreateDirectory(userData);

            var options = new CoreWebView2EnvironmentOptions
            {
                AdditionalBrowserArguments = BuildBrowserArguments(),
            };

            var env = await CoreWebView2Environment.CreateAsync(null, userData, options);
            await Web.EnsureCoreWebView2Async(env);

            var core = Web.CoreWebView2;

            // —— 浏览器行为收敛：这是应用窗口，不是浏览器 ——
            var s = core.Settings;
            s.AreDefaultContextMenusEnabled = false;
            s.IsStatusBarEnabled = false;
            s.IsZoomControlEnabled = false;
            s.IsSwipeNavigationEnabled = false;
            s.AreBrowserAcceleratorKeysEnabled = false;
            s.IsPasswordAutosaveEnabled = false;
            s.IsGeneralAutofillEnabled = false;
            s.AreDevToolsEnabled = Debugger.IsAttached;
            s.IsWebMessageEnabled = true;

            core.NewWindowRequested += (_, args) =>
            {
                // 界面里的外链一律用系统浏览器打开，绝不在应用内开新窗口
                args.Handled = true;
                var url = args.Uri;
                if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                    url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
                }
            };

            core.ProcessFailed += OnCoreProcessFailed;

            // —— 本地资源映射 ——
            // 界面受 CSP 限制不能直接读 file://，而皮肤、背景、导出图标都在磁盘上，
            // 于是把两个目录挂到虚拟主机下，前端按 https 地址访问即可。
            core.SetVirtualHostNameToFolderMapping(
                WebAssets.AppHost, wwwroot, CoreWebView2HostResourceAccessKind.Allow);
            core.SetVirtualHostNameToFolderMapping(
                WebAssets.DataHost, StarCorePaths.DataRoot, CoreWebView2HostResourceAccessKind.Allow);

            _bridge = new HostBridge(core, this);
            _bridge.Attach();

            // 内核里"启动完成后关闭启动器"的请求（用户在设置里开启时才会触发）
            LaunchService.QuitRequested += OnQuitRequested;

            core.NavigationCompleted += OnNavigationCompleted;

            // 必须在 Navigate 之前注入：AddScriptToExecuteOnDocumentCreatedAsync
            // 只对"之后创建的文档"生效。放在导航完成后再挂，首次加载期间抛的错
            // （尤其是模块导入失败）就完全抓不到 —— 而那正是白屏最常见的原因。
            // 用 _consoleWired 做幂等：这个初始化流程在某些路径下可能被重入，
            // 重复注入会让同一条错误在日志里出现多份。
            if (!_consoleWired)
            {
                _consoleWired = true;
                await core.AddScriptToExecuteOnDocumentCreatedAsync(ErrorHookScript);
            }

            core.Navigate(DevPage is { Length: > 0 } dev
                ? WebAssets.App(dev)
                : WebAssets.App("index.html"));
            Logger.Info("[window] 界面已加载");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "[window] WebView2 初始化失败");

            // 把最可能的原因直接说出来。0x8007139F 几乎总是"用户数据目录被另一个
            // 实例占用"，而 0x80070002 一般是 WebView2 运行时没装。
            var hint = ex.HResult switch
            {
                unchecked((int)0x8007139F) =>
                    "另一个 StarCore 实例正在使用界面组件。请先关闭已打开的 StarCore 窗口，再重新启动。",
                unchecked((int)0x80070002) =>
                    "系统缺少 WebView2 运行时。请安装 Microsoft Edge WebView2 Runtime 后重试。",
                _ => "如果反复出现，请检查是否被安全软件拦截。",
            };
            MessageBox.Show($"界面初始化失败：\n\n{hint}", "StarCore",
                MessageBoxButton.OK, MessageBoxImage.Error);
            ShowFallback(
                "界面组件未能启动",
                "StarCore 的界面依赖 Microsoft Edge WebView2 运行时。\n\n"
                + "• Windows 11 与较新的 Windows 10 已内置该组件\n"
                + "• 若缺失，请安装「Microsoft Edge WebView2 Runtime」（微软官网免费下载）\n\n"
                + $"技术细节：{ex.Message}");
        }
    }

    /* ==================================================================
       渲染后端：硬件加速优先，失败自动降级到软件渲染
       ================================================================== */

    private static string BuildBrowserArguments()
    {
        var args = new List<string>
        {
            // WebView2 在 WPF 里有个已知问题：窗口被判定为"被遮挡"时会停止渲染，
            // 表现为切回窗口一片白。关掉原生遮挡计算即可。
            "--disable-features=CalculateNativeWinOcclusion",
            "--autoplay-policy=user-gesture-required",
        };

        RenderBackend.ApplyTo(args);
        return string.Join(' ', args);
    }

    private int _gpuFailureStreak;

    private void OnCoreProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs args)
    {
        var kind = args.ProcessFailedKind;
        Logger.Warn($"[window] 子进程失败 kind={kind} reason={args.Reason} exitCode={args.ExitCode} " +
                    $"process={args.ProcessDescription}");

        if (kind is CoreWebView2ProcessFailedKind.BrowserProcessExited)
        {
            Logger.Error(new Exception($"WebView2 浏览器进程退出（{kind}）"), "[window] 界面进程异常");
            MessageBox.Show(
                "界面渲染进程意外退出，StarCore 需要重新启动。\n\n如果反复出现，请检查是否被安全软件拦截。",
                "StarCore", MessageBoxButton.OK, MessageBoxImage.Warning);
            Application.Current.Shutdown(2);
            return;
        }

        // GPU / 工具进程崩溃：累加计数，达到阈值就带着"软件渲染"标记重启自身。
        // 这是这类问题唯一可靠的自愈方式 —— 运行中的 WebView2 无法安全切换渲染后端。
        if (kind is CoreWebView2ProcessFailedKind.GpuProcessExited
                 or CoreWebView2ProcessFailedKind.UtilityProcessExited)
        {
            var streak = Interlocked.Increment(ref _gpuFailureStreak);

            if (streak >= RenderBackend.FailureThreshold &&
                RenderBackend.EscalateAndRelaunch())
            {
                Application.Current.Shutdown(0);
            }
        }
    }

    /* ==================================================================
       前端错误上报

       为什么必须做：界面是 HTML/JS，前端一旦抛异常，用户看到的就是"一直停在启动画面"
       或者"某个页面空白"，而 C# 这边一无所知 —— 排查只能靠猜。
       这里注入一个最早执行的错误钩子，把 window.onerror / unhandledrejection
       连同文件名行号与堆栈一起发回内核写进日志。

       用 AddScriptToExecuteOnDocumentCreatedAsync 而不是在页面里写：
       它在这个文档的任何脚本之前执行，能抓到模块加载阶段的错误 ——
       而模块加载失败恰恰是"白屏"最常见的原因。
       ================================================================== */

    private const string ErrorHookScript = """
        (function () {
          if (window.__scErrorHook) return;
          window.__scErrorHook = true;

          function send(payload) {
            try {
              // 与 bridge.js 同一套协议；用固定前缀的 id，避免和正常调用撞号
              window.chrome.webview.postMessage(JSON.stringify({
                kind: 'invoke',
                id: '__ui_error__' + Date.now() + '_' + Math.random().toString(36).slice(2),
                method: 'ui.error',
                params: payload,
              }));
            } catch (e) { /* 宿主不在时静默 */ }
          }

          // 资源加载失败会以 error 事件冒泡到 window，但它们的 e.message 是空的
          // （只会得到 "[object Event]"）。所以要按 tagName 分开处理：
          //   script / link 失败 —— 直接让界面不可用，必须上报；
          //   img 失败 —— 图标地址失效是常态（Modrinth 上的图偶尔会 404），
          //              各处都已挂 onerror 兜底，上报只会把日志淹掉。
          window.addEventListener('error', function (e) {
            var t = e && e.target;
            if (t && t !== window && t.tagName) {
              var tag = t.tagName;
              if (tag === 'IMG' || tag === 'VIDEO' || tag === 'AUDIO' || tag === 'SOURCE') return;
              if (tag === 'SCRIPT' || tag === 'LINK') {
                send({
                  kind: 'script-load',
                  message: (tag === 'SCRIPT' ? '脚本' : '样式表') + '加载失败',
                  source: t.src || t.href || '(inline)',
                });
                return;
              }
              return;
            }

            send({
              kind: 'error',
              message: (e && e.message) || '未知的界面错误',
              source: (e && e.filename) || '',
              line: (e && e.lineno) || 0,
              column: (e && e.colno) || 0,
              stack: (e && e.error && e.error.stack) || '',
            });
          }, true);

          window.addEventListener('unhandledrejection', function (e) {
            var r = e && e.reason;
            send({
              kind: 'rejection',
              message: (r && (r.message || String(r))) || '未处理的 Promise 拒绝',
              stack: (r && r.stack) || '',
            });
          });

          // —— 长任务探针 ——
          // 界面卡顿（含"渲染进程无响应"）的唯一成因是主线程被某个同步任务占住。
          // PerformanceObserver 的 longtask 能直接给出"哪一个任务、卡了多久"，
          // 这比看帧率有用得多 —— 帧率只说明"慢"，长任务说明"慢在哪"。
          try {
            var seen = 0;
            new PerformanceObserver(function (list) {
              list.getEntries().forEach(function (entry) {
                // 超过 50ms 就算长任务，但那种量级太多；只报真正会造成可感卡顿的
                if (entry.duration < 250) return;
                if (seen++ > 30) return;   // 防止日志被刷爆
                send({
                  kind: 'longtask',
                  message: '主线程长任务 ' + Math.round(entry.duration) + 'ms',
                  duration: Math.round(entry.duration),
                  source: entry.name || '',
                  stack: '起点 ' + Math.round(entry.startTime) + 'ms' +
                         (entry.attribution && entry.attribution[0]
                           ? ' / ' + (entry.attribution[0].name || entry.attribution[0].containerType || '')
                           : ''),
                });
              });
            }).observe({ entryTypes: ['longtask'] });
          } catch (e) { /* 不支持就算了，不影响功能 */ }
        })();
        """;

    private async void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {

        if (!e.IsSuccess)
        {
            Logger.Warn($"[window] 界面导航失败：{e.WebErrorStatus}");
            return;
        }

        // 让原生窗口外观与界面主题对齐
        SyncNativeTheme();

        // 告诉界面当前是不是软件光栅化 —— 它据此决定要不要关掉毛玻璃与混合模式。
        // 这一步必须在页面加载完成后立刻做，否则首帧会先按高开销模式画一遍。
        try
        {
            await Web.CoreWebView2.ExecuteScriptAsync(
                $"window.__scSetRenderMode && window.__scSetRenderMode({{ software: {(RenderBackend.IsSoftware ? "true" : "false")}, level: {RenderBackend.Level} }})");
        }
        catch (Exception ex)
        {
            Logger.Debug($"[window] 同步渲染模式失败：{ex.Message}");
        }

        // 先应用命令行覆盖项，再跳页，保证截图看到的是最终状态
        if (PendingTheme is not null || PendingAccent is not null)
        {
            var patch = new List<string>();
            if (PendingTheme is not null) patch.Add($"theme: '{PendingTheme}'");
            if (PendingAccent is not null) patch.Add($"accent: '{PendingAccent}'");
            try
            {
                await Web.CoreWebView2.ExecuteScriptAsync(
                    $"window.__scApplySettings({{ {string.Join(", ", patch)} }})");
            }
            catch (Exception ex) { Logger.Warn($"[window] 应用命令行主题失败：{ex.Message}"); }
        }

        if (PendingPage is { Length: > 0 } page)
        {
            PendingPage = null;
            var parts = page.Split(':', 2);
            var section = parts[0];
            var sub = parts.Length > 1 ? parts[1] : null;
            try
            {
                var arg = sub is null ? $"'{section}'" : $"'{section}', '{sub}'";
                await Web.CoreWebView2.ExecuteScriptAsync($"window.StarCore && window.StarCore.go({arg})");
                await Task.Delay(700);
            }
            catch (Exception ex) { Logger.Warn($"[window] 跳转到指定页面失败：{ex.Message}"); }
        }

        if (MeasureFps)
        {
            MeasureFps = false;
            await MeasureFrameRateAsync();
        }

        if (PendingLaunchInstanceId is { } id)
        {
            PendingLaunchInstanceId = null;
            try
            {
                await Web.CoreWebView2.ExecuteScriptAsync(
                    $"window.StarCore && window.StarCore.launch({id})");
            }
            catch (Exception ex)
            {
                Logger.Warn($"[window] 自动启动实例失败：{ex.Message}");
            }
        }
    }

    /// <summary>
    /// 采样真实帧率。
    /// 用 requestAnimationFrame 的间隔统计 —— 这是浏览器实际的出帧节奏，
    /// 比任何"感觉快不快"都可靠；同时给出 p95 帧间隔，能看出有没有周期性卡顿。
    /// </summary>
    private async Task MeasureFrameRateAsync()
    {
        const string script = """
            (async () => {
              // 先等界面稳定（字体、图片、首屏动画都结束）
              await new Promise(r => setTimeout(r, 1500));
              const stamps = [];
              await new Promise(resolve => {
                const start = performance.now();
                function tick(t) {
                  stamps.push(t);
                  if (t - start < 3000) requestAnimationFrame(tick);
                  else resolve();
                }
                requestAnimationFrame(tick);
              });
              const deltas = [];
              for (let i = 1; i < stamps.length; i++) deltas.push(stamps[i] - stamps[i - 1]);
              deltas.sort((a, b) => a - b);
              const avg = deltas.reduce((a, b) => a + b, 0) / Math.max(1, deltas.length);
              const p95 = deltas[Math.floor(deltas.length * 0.95)] ?? avg;
              const worst = deltas[deltas.length - 1] ?? avg;
              return JSON.stringify({
                frames: deltas.length,
                fps: Math.round(1000 / Math.max(0.001, avg)),
                avgMs: +avg.toFixed(2),
                p95Ms: +p95.toFixed(2),
                worstMs: +worst.toFixed(2),
              });
            })()
            """;

        try
        {
            var raw = await Web.CoreWebView2.ExecuteScriptAsync(script);
            // ExecuteScriptAsync 返回的是 JSON 编码的字符串，这里需要再解一层
            var inner = System.Text.Json.JsonSerializer.Deserialize<string>(raw) ?? "{}";
            Logger.Info($"[perf] 帧率采样：{inner}");
        }
        catch (Exception ex)
        {
            Logger.Warn($"[perf] 帧率采样失败：{ex.Message}");
        }
    }

    /* ==================================================================
       窗口状态
       ================================================================== */

    private void OnWindowStateChanged(object? sender, EventArgs e)
    {
        SyncNativeTheme();

        var maximized = WindowState == WindowState.Maximized;
        try
        {
            Web.CoreWebView2?.PostWebMessageAsString(
                WireJson(new { kind = "event", name = "window.state", payload = new { maximized } }));
        }
        catch { /* 尚未初始化完成时忽略 */ }
    }

    private void SyncNativeTheme()
    {
        // 与前端 tokens.css 的 --bg 保持一致，避免缩放/最大化瞬间露出异色底
        var dark = _dark;
        Background = dark
            ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x0F, 0x0E, 0x0D))
            : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF2, 0xF1, 0xEE));
        RootGrid.Background = Background;

        NativeWindow.ApplyTheme(this, dark, WindowState == WindowState.Maximized);
    }

    /// <summary>由桥在界面切换主题时调用。</summary>
    internal void SetDarkTheme(bool dark)
    {
        if (_dark == dark) return;
        _dark = dark;
        SyncNativeTheme();
    }

    /* 托盘与"启动后自动关闭"（第 6 批）。
       CloseToTray：点关闭按钮时隐藏到托盘而不是退出。
       LaunchCloseLauncher：游戏启动后自动关闭启动器（释放内存）；
       若同时开了 CloseToTray，就只隐藏到托盘，用户还能从托盘叫回来。 */
    private TrayIcon? _tray;
    private bool _reallyExit;
    private bool _launchHookInstalled;

    private void EnsureTray()
    {
        if (_tray is not null) return;
        _tray = new TrayIcon(this, RestoreFromTray, ExitForReal);
        _tray.Install();
    }

    private void RestoreFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    /// <summary>从托盘菜单"退出"：绕过 CloseToTray 的拦截，真正退出。</summary>
    internal void ExitForReal()
    {
        _reallyExit = true;
        _tray?.Dispose();
        _tray = null;
        Close();
    }

    /// <summary>游戏进入 running 后按设置处理启动器窗口。</summary>
    private void InstallLaunchHook()
    {
        if (_launchHookInstalled) return;
        _launchHookInstalled = true;

        StarCoreHost.Event += (name, payload) =>
        {
            if (name != "launch.state") return;
            var state = (payload as LaunchState)?.State;

            // 游戏结束：如果启动器正在托盘里，把它弹回前台 ——
            // 玩家刚打完一局，下一步多半要看日志/换实例。
            if (state is "exited" or "crashed")
            {
                Dispatcher.BeginInvoke(() =>
                {
                    if (!IsVisible && _tray is not null)
                    {
                        RestoreFromTray();
                        Logger.Info("[window] 游戏已退出，启动器已从托盘恢复");
                    }
                });
                return;
            }

            if (state != "running") return;

            var st = SettingsService.Current;
            if (!st.LaunchCloseLauncher) return;

            Dispatcher.BeginInvoke(() =>
            {
                if (st.CloseToTray)
                {
                    EnsureTray();
                    Hide();
                    Logger.Info("[window] 游戏已启动，启动器已最小化到托盘");
                }
                else
                {
                    Logger.Info("[window] 游戏已启动，按设置关闭启动器");
                    ExitForReal();
                }
            });
        };
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // 关闭到托盘：拦下这次关闭，隐藏窗口，游戏继续运行
        if (!_reallyExit && SettingsService.Current.CloseToTray)
        {
            e.Cancel = true;
            EnsureTray();
            Hide();
            Logger.Info("[window] 已最小化到托盘（关闭按钮）");
            return;
        }

        try { _bridge?.Detach(); }
        catch { }

        // 若还有游戏在跑，交给用户决定
        var running = LaunchService.RunningInstanceId;
        if (running is not null)
        {
            var r = MessageBox.Show(
                "还有游戏正在运行。\n\n关闭启动器不会结束游戏进程，游戏会继续运行。确定关闭吗？",
                "StarCore", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (r != MessageBoxResult.Yes) e.Cancel = true;
        }

        _tray?.Dispose();
        Logger.Info("[window] 启动器关闭");
    }

    private void ShowFallback(string title, string detail)
    {
        try
        {
            Web.Visibility = Visibility.Collapsed;
            FallbackPanel.Visibility = Visibility.Visible;
            FallbackTitle.Text = title;
            FallbackDetail.Text = detail;
        }
        catch { /* 兜底界面都失败就只能空窗 */ }
    }

    private static string WireJson(object o) => System.Text.Json.JsonSerializer.Serialize(o, Core.App.Json.Options);

    private void OnQuitRequested() => Dispatcher.BeginInvoke(() =>
    {
        Logger.Info("[window] 已按设置关闭启动器");
        Close();
    });
}
