using System.Diagnostics;
using System.IO;
using StarCore.Core.App;

namespace StarCore.Desktop;

/// <summary>
/// 界面渲染后端的降级阶梯。
///
/// 背景：Chromium 的 GPU / 工具进程会在几类环境下反复崩溃，界面一闪就没。
/// 运行中的 WebView2 无法安全切换渲染后端，所以唯一可靠的自愈方式是
/// "带标记重启自身"，让下一次启动采用下一级配置。
///
/// 阶梯顺序经过实测排序 —— **代价最小的调整放在最前**：
///   0  默认                —— 标准环境
///   1  关闭沙箱            —— 保留硬件加速。受限/被注入的环境里沙箱初始化失败
///                            才是崩溃主因，这一级往往能修好且不掉帧
///   2  软件渲染            —— 真正没有可用 GPU 时才用，代价很大
///   3  软件渲染 + 关沙箱   —— 兜底
///
/// 不预判"是不是虚拟机"：这类判断误报率高，先试最小的改动更稳。
/// </summary>
internal static class RenderBackend
{
    public const string ModeEnvVar = "STARCORE_RENDER_MODE";
    public const int FailureThreshold = 4;
    private const int MaxLevel = 3;

    private static bool _escalating;

    public static int Level
    {
        get
        {
            var raw = Environment.GetEnvironmentVariable(ModeEnvVar);
            return int.TryParse(raw, out var v) ? Math.Clamp(v, 0, MaxLevel) : 0;
        }
    }

    /// <summary>当前是否在软件光栅化。界面据此进入低开销模式。</summary>
    public static bool IsSoftware => Level >= 2;

    public static void ApplyTo(List<string> args)
    {
        switch (Level)
        {
            case 1:
                args.Add("--no-sandbox");
                args.Add("--disable-setuid-sandbox");
                Logger.Warn("[render] 渲染级别 1：关闭沙箱（保留硬件加速）");
                break;

            case 2:
                AddSoftware(args);
                Logger.Warn("[render] 渲染级别 2：软件渲染");
                break;

            case 3:
                AddSoftware(args);
                args.Add("--no-sandbox");
                args.Add("--disable-setuid-sandbox");
                Logger.Warn("[render] 渲染级别 3：软件渲染 + 关闭沙箱");
                break;

            default:
                break;
        }
    }

    private static void AddSoftware(List<string> args)
    {
        args.Add("--disable-gpu");
        args.Add("--disable-gpu-compositing");
        args.Add("--disable-accelerated-2d-canvas");
        args.Add("--use-angle=swiftshader");
    }

    public static bool EscalateAndRelaunch()
    {
        if (_escalating) return false;
        var next = Level + 1;
        if (next > MaxLevel) return false;
        _escalating = true;

        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe) || !File.Exists(exe))
            {
                Logger.Warn("[render] 无法定位自身可执行文件，跳过自动降级");
                return false;
            }

            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                WorkingDirectory = AppContext.BaseDirectory,
            };
            psi.Environment[ModeEnvVar] = next.ToString();

            var argv = Environment.GetCommandLineArgs();
            for (var i = 1; i < argv.Length; i++) psi.ArgumentList.Add(argv[i]);

            Logger.Warn($"[render] 界面子进程反复崩溃，正在以渲染级别 {next} 重启");
            Process.Start(psi);
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "[render] 自动降级重启失败");
            return false;
        }
    }
}
