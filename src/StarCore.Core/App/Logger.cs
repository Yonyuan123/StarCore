namespace StarCore.Core.App;

public enum LogLevel { Trace, Debug, Info, Warn, Error }

/// <summary>
/// 双层日志：写文件（供「日志」页读取与崩溃排查）+ 推事件（供界面实时显示）。
/// 单条写入用锁保护，避免下载线程与 UI 线程并发写坏文件。
/// </summary>
public static class Logger
{
    private static readonly object Gate = new();
    private static string? _file;
    private static readonly List<string> Buffer = new();

    /// <summary>
    /// 重入防护。
    /// 日志会触发 LineWritten，订阅方（宿主桥）在推送失败时又可能写日志，
    /// 同一条线程上就会形成"日志 → 事件 → 日志"的自激递归，直接把栈打爆。
    /// 这里用一个线程静态标志把这条路切断。
    /// </summary>
    [ThreadStatic] private static bool _dispatching;

    /// <summary>日志行产生时触发（参数为已格式化的一行）。</summary>
    public static event Action<string, LogLevel>? LineWritten;

    public static LogLevel MinimumLevel { get; set; } = LogLevel.Debug;

    public static void Initialize(string? tag = null)
    {
        lock (Gate)
        {
            var name = $"{DateTime.Now:yyyy-MM-dd_HH-mm-ss}{(string.IsNullOrEmpty(tag) ? "" : "-" + tag)}.log";
            _file = Path.Combine(StarCorePaths.LogsRoot, name);
            try { File.WriteAllText(_file, ""); } catch { /* 日志失败不能影响主流程 */ }
        }
        Info($"日志已启动：{_file}");
    }

    public static string? CurrentFile => _file;

    public static void Trace(string msg) => Write(LogLevel.Trace, msg);
    public static void Debug(string msg) => Write(LogLevel.Debug, msg);
    public static void Info(string msg) => Write(LogLevel.Info, msg);
    public static void Warn(string msg) => Write(LogLevel.Warn, msg);

    public static void Error(string msg) => Write(LogLevel.Error, msg);

    public static void Error(Exception ex, string context)
        => Write(LogLevel.Error, $"{context}{Environment.NewLine}{ex}");

    public static void Write(LogLevel level, string message)
    {
        if (level < MinimumLevel) return;

        var line = $"[{DateTime.Now:HH:mm:ss.fff}] [{Tag(level)}] {message}";

        lock (Gate)
        {
            if (_file is not null)
            {
                try { File.AppendAllText(_file, line + Environment.NewLine); }
                catch { /* 忽略：磁盘满或占用不应导致业务中断 */ }
            }
            else
            {
                Buffer.Add(line);
                if (Buffer.Count > 2000) Buffer.RemoveAt(0);
            }
        }

        // 只把需要用户看见的级别推给界面。
        // Debug/Trace 每秒可能几十条，既无必要也拖慢渲染；它们仍然完整落盘。
        if (level < LogLevel.Warn) return;
        if (_dispatching) return;

        _dispatching = true;
        try { LineWritten?.Invoke(line, level); }
        catch { /* 订阅方异常不影响日志 */ }
        finally { _dispatching = false; }
    }

    private static string Tag(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRACE",
        LogLevel.Debug => "DEBUG",
        LogLevel.Info => "INFO ",
        LogLevel.Warn => "WARN ",
        _ => "ERROR",
    };
}
