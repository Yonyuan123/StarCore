using System.Text;
using System.Text.RegularExpressions;
using StarCore.Core.App;
using StarCore.Core.Models;

namespace StarCore.Core.Services;

/// <summary>
/// 日志读取与崩溃分析。
///
/// 崩溃分析刻意做成**可解释的规则引擎**而不是黑盒：每条结论都给出
/// 依据的原文片段，用户能自己判断对不对。规则覆盖社区里最常见的
/// 崩溃类型（缺依赖、Java 版本不符、内存不足、显卡驱动等）。
/// </summary>
public static class LogService
{
    public sealed class AnalysisResult
    {
        public string Title { get; set; } = "未检测到明确的崩溃原因";
        public string? Exception { get; set; }
        public string? StackTrace { get; set; }
        public List<Reason> Reasons { get; set; } = new();
        public List<Suggestion> Suggestions { get; set; } = new();
    }

    public sealed class Reason
    {
        public string Title { get; set; } = "";
        public string Detail { get; set; } = "";
        /// <summary>high | medium | low</summary>
        public string Severity { get; set; } = "medium";
        public string? Evidence { get; set; }
    }

    public sealed class Suggestion
    {
        public string Title { get; set; } = "";
        public string? Action { get; set; }
    }

    /* ==================================================================
       列举
       ================================================================== */

    public static List<LogFileInfo> List()
    {
        var result = new List<LogFileInfo>();

        void AddDir(string dir, string kind, string pattern = "*.log")
        {
            try
            {
                if (!Directory.Exists(dir)) return;
                foreach (var f in Directory.EnumerateFiles(dir, pattern, SearchOption.AllDirectories))
                {
                    var fi = new FileInfo(f);
                    result.Add(new LogFileInfo
                    {
                        Name = Path.GetFileName(f),
                        Path = f,
                        Kind = kind,
                        SizeBytes = fi.Length,
                        ModifiedAt = fi.LastWriteTimeUtc,
                    });
                }
            }
            catch { }
        }

        AddDir(StarCorePaths.LogsRoot, "launcher");

        foreach (var inst in InstanceService.All)
        {
            AddDir(Path.Combine(inst.Path, "logs"), "launch", "*.log");
            AddDir(Path.Combine(inst.Path, "logs"), "launch", "*.log.gz");
            AddDir(Path.Combine(inst.Path, "crash-reports"), "crash", "*.txt");
        }

        return result.OrderByDescending(l => l.ModifiedAt ?? DateTimeOffset.MinValue).Take(200).ToList();
    }

    public static (List<string> lines, bool truncated) Read(string file, int maxLines = 5000)
    {
        if (!File.Exists(file)) return (new List<string> { "（日志文件不存在）" }, false);

        try
        {
            if (file.EndsWith(".gz", StringComparison.OrdinalIgnoreCase))
            {
                using var fs = File.OpenRead(file);
                using var gz = new System.IO.Compression.GZipStream(fs, System.IO.Compression.CompressionMode.Decompress);
                using var sr = new StreamReader(gz, Encoding.UTF8);
                return Tail(sr, maxLines);
            }

            // 先看行数决定是否需要截断，避免把几百 MB 全读进内存
            var all = File.ReadLines(file).Count();
            var lines = File.ReadLines(file);
            if (all <= maxLines) return (lines.ToList(), false);
            return (lines.Skip(all - maxLines).ToList(), true);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, $"[log] 读取失败 {file}");
            return (new List<string> { $"（读取失败：{ex.Message}）" }, false);
        }
    }

    private static (List<string>, bool) Tail(StreamReader sr, int maxLines)
    {
        var queue = new Queue<string>(maxLines + 1);
        var total = 0;
        string? line;
        while ((line = sr.ReadLine()) is not null)
        {
            total++;
            queue.Enqueue(line);
            if (queue.Count > maxLines) queue.Dequeue();
        }
        return (queue.ToList(), total > maxLines);
    }

    public static OpResult Delete(string file)
    {
        try
        {
            if (!File.Exists(file)) return OpResult.Fail("文件不存在。");
            File.Delete(file);
            return OpResult.Success("已删除");
        }
        catch (Exception ex)
        {
            return OpResult.Fail($"删除失败：{ex.Message}");
        }
    }

    public static OpResult ClearAll()
    {
        var n = 0;
        try
        {
            foreach (var f in List())
            {
                try { File.Delete(f.Path); n++; } catch { }
            }
            return OpResult.Success($"已清除 {n} 个日志文件");
        }
        catch (Exception ex)
        {
            return OpResult.Fail($"清除失败：{ex.Message}");
        }
    }

    /* ==================================================================
       崩溃分析
       ================================================================== */

    private static readonly (string Pattern, string Title, string Detail, string Severity, string Action)[] Rules =
    {
        (@"java\.lang\.ClassNotFoundException:\s*(\S+)",
            "缺少类或依赖",
            "游戏尝试加载一个不存在的类。通常是模组缺少前置，或模组与游戏版本不匹配。",
            "high", "检查模组列表里的前置依赖，或移除不兼容的模组"),

        (@"NoClassDefFoundError:\s*(\S+)",
            "缺少类或依赖",
            "引用的类在运行期不存在。常见于模组前置缺失或加载器版本过旧。",
            "high", "补齐前置模组，并确认加载器版本"),

        (@"fabric-language-kotlin|kotlin\.KotlinVersion|kotlin-stdlib",
            "缺少 Kotlin 运行时",
            "有模组是用 Kotlin 编写的，但没有安装 fabric-language-kotlin。",
            "high", "安装 fabric-language-kotlin 前置"),

        (@"UnsupportedClassVersionError|class file version (\d+)",
            "Java 版本不匹配",
            "模组或游戏要求的 Java 版本高于当前使用的版本。",
            "high", "在设置里为该实例更换更高版本的 Java"),

        (@"java\.lang\.OutOfMemoryError",
            "内存不足",
            "游戏在运行中耗尽了你分配的内存。模组越多需要的堆内存越大。",
            "high", "提高该实例的内存分配，或减少模组数量"),

        (@"The driver does not appear to support OpenGL|Pixel format not accelerated|EXCEPTION_ACCESS_VIOLATION.*nvoglv|ig\d+icd",
            "显卡驱动问题",
            "图形驱动未能提供游戏所需的 OpenGL 能力，或驱动崩溃。",
            "high", "更新显卡驱动；若是双显卡笔记本，请强制用独显运行"),

        (@"Mixin apply failed|MixinApplyError|InvalidMixinException",
            "Mixin 注入失败",
            "某个模组试图修改游戏代码但失败了，通常是模组之间冲突或版本不匹配。",
            "medium", "逐个禁用最近安装的模组以定位冲突源"),

        (@"DuplicateModsFoundException|duplicate mod|already registered",
            "模组重复或 ID 冲突",
            "安装了同一模组的多个版本，或两个模组注册了相同的 ID。",
            "medium", "删除重复的模组文件"),

        (@"Missing or unsupported mandatory dependencies|requires version .* or later",
            "缺少必要模组或依赖",
            "加载器明确报告缺少某个依赖，或依赖版本过低。",
            "high", "按提示安装或升级对应的依赖模组"),

        (@"FileNotFoundException.*\.jar|No such file or directory",
            "文件缺失",
            "游戏需要的某个文件不存在，可能是安装不完整或被安全软件删除。",
            "medium", "在「下载」页对该实例执行一次完整性校验"),

        (@"AccessDeniedException|拒绝访问|Permission denied",
            "文件权限不足",
            "游戏无法写入存档或配置文件，通常是杀毒软件拦截或目录权限受限。",
            "medium", "将游戏目录加入安全软件白名单，或改用非系统盘目录"),

        (@"Failed to download|Connection reset|UnknownHostException|SocketTimeoutException",
            "网络请求失败",
            "启动过程中有资源未能下载完成。",
            "low", "检查网络或代理设置，然后重新启动"),

        (@"The game crashed whilst|Exit code: -?\d+",
            "游戏异常退出",
            "游戏进程以非零状态码退出。",
            "medium", "查看下方堆栈中的第一条 Caused by"),

        (@"corrupted|zip END header not found|invalid LOC header",
            "文件损坏",
            "某个 jar 或资源包在下载过程中损坏了。",
            "high", "删除并重新下载最近安装的模组"),
    };

    public static AnalysisResult Analyze(string? file)
    {
        var result = new AnalysisResult();

        string content;
        if (!string.IsNullOrWhiteSpace(file) && File.Exists(file))
        {
            try { content = string.Join('\n', Read(file, 40000).lines); }
            catch { content = ""; }
        }
        else
        {
            // 未指定文件时，取最近一次启动日志
            var latest = List().FirstOrDefault();
            if (latest is null) return result;
            content = string.Join('\n', Read(latest.Path, 40000).lines);
        }

        if (string.IsNullOrWhiteSpace(content)) return result;

        // 主异常
        var ex = Regex.Match(content, @"^(?:java\.lang\.|net\.minecraft\.|org\.spongepowered\.)?[A-Za-z0-9_.$]*(?:Exception|Error):\s*.+$",
            RegexOptions.Multiline);
        if (ex.Success) result.Exception = ex.Value.Trim();

        // 提取第一条 Caused by 起 40 行作为堆栈
        var caused = Regex.Match(content, @"Caused by:.*", RegexOptions.Singleline);
        if (caused.Success)
        {
            var lines = caused.Value.Split('\n').Take(40);
            result.StackTrace = string.Join('\n', lines).Trim();
        }
        else if (ex.Success)
        {
            var idx = content.IndexOf(ex.Value, StringComparison.Ordinal);
            var tail = content[idx..];
            result.StackTrace = string.Join('\n', tail.Split('\n').Take(40));
        }

        // 逐条规则匹配
        foreach (var (pattern, title, detail, severity, action) in Rules)
        {
            Match m;
            try { m = Regex.Match(content, pattern, RegexOptions.IgnoreCase); }
            catch { continue; }
            if (!m.Success) continue;

            result.Reasons.Add(new Reason
            {
                Title = title,
                Detail = detail,
                Severity = severity,
                Evidence = Excerpt(content, m.Index),
            });
            result.Suggestions.Add(new Suggestion { Title = action, Action = ActionFor(title) });
        }

        if (result.Reasons.Count > 0)
            result.Title = result.Reasons[0].Title;

        // 兜底建议
        if (result.Suggestions.Count == 0)
        {
            result.Suggestions.Add(new Suggestion { Title = "把完整日志提交到社区求助，通常几分钟内就能定位", Action = "upload" });
            result.Suggestions.Add(new Suggestion { Title = "尝试禁用最近安装的模组后重新启动", Action = "mods" });
        }

        return result;
    }

    private static string ActionFor(string reasonTitle) => reasonTitle switch
    {
        "内存不足" => "memory",
        "Java 版本不匹配" => "java",
        "缺少类或依赖" or "缺少 Kotlin 运行时" or "缺少必要模组或依赖" or "Mixin 注入失败" or "模组重复或 ID 冲突" => "mods",
        "文件缺失" or "文件损坏" => "verify",
        "显卡驱动问题" => "opensite",
        _ => "logs",
    };

    private static string Excerpt(string content, int index)
    {
        var start = Math.Max(0, index - 120);
        var end = Math.Min(content.Length, index + 260);
        var text = content[start..end].Replace('\r', ' ').Replace('\n', ' ').Trim();
        return text.Length > 320 ? text[..320] + "…" : text;
    }

    /* ==================================================================
       诊断报告

       为什么要有这个：新手遇到崩溃时能提供的信息往往只有一句"打不开"，
       而排查需要的是环境、版本、Java、内存、模组清单、启动参数这一整套。
       让用户自己一项项去问、去截图，效率极低且经常问不全。
       所以做成"一键导出"，把该有的都装进去，用户只需要把 zip 发出来。
       ================================================================== */

    /// <summary>
    /// 导出诊断报告 zip 到指定目录（为空则放到桌面上）。
    ///
    /// 报告里**不会**包含账户凭据：token 一律脱敏，accounts.json 整份不打进去。
    /// 用户是要把它发到公开场合求助的，任何凭据泄漏都是不可接受的。
    /// </summary>
    public static async Task<OpResult> ExportReportAsync(
        long? instanceId, string? targetDir, CancellationToken ct = default)
    {
        try
        {
            var dir = string.IsNullOrWhiteSpace(targetDir)
                ? Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)
                : targetDir!;
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var zipPath = Path.Combine(dir, $"StarCore-诊断-{stamp}.zip");

            var settings = SettingsService.Current;
            // 没指定实例时退回当前活跃实例；一个都没有就按无实例处理
            var instance = instanceId is { } id
                ? InstanceService.Get(id)
                : (InstanceService.ActiveId is { } active && active > 0 ? InstanceService.Get(active) : null);

            var latest = List().FirstOrDefault();
            var analysis = Analyze(latest?.Path);

            // —— summary.txt：人读的部分 ——
            var sb = new StringBuilder();
            sb.AppendLine("StarCore 诊断报告");
            sb.AppendLine("========================================");
            sb.AppendLine($"生成时间    ：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"启动器版本  ：{StarCorePaths.ProductVersion}");
            sb.AppendLine($"数据目录    ：{StarCorePaths.DataRoot}");
            sb.AppendLine($"便携模式    ：{(StarCorePaths.IsPortable ? "是" : "否")}");
            sb.AppendLine();
            sb.AppendLine("— 运行环境 —");
            sb.AppendLine($"操作系统    ：{Environment.OSVersion}");
            sb.AppendLine($"架构        ：{System.Runtime.InteropServices.RuntimeInformation.OSArchitecture}");
            sb.AppendLine($"CPU 核心数  ：{Environment.ProcessorCount}");
            sb.AppendLine($"物理内存    ：{TotalMemoryMb()} MB");
            sb.AppendLine($".NET        ：{Environment.Version}");
            sb.AppendLine();
            sb.AppendLine("— 界面渲染 —");
            sb.AppendLine($"渲染级别    ：{Environment.GetEnvironmentVariable("STARCORE_RENDER_MODE") ?? "0（默认，硬件加速）"}");
            sb.AppendLine($"下载源      ：{DownloadSource.CurrentName}");
            sb.AppendLine($"并发下载数  ：{settings.MaxThreads}");
            sb.AppendLine();
            sb.AppendLine("— 实例 —");
            if (instance is null)
            {
                sb.AppendLine("（没有活跃实例）");
            }
            else
            {
                sb.AppendLine($"名称        ：{instance.Name}");
                sb.AppendLine($"游戏版本    ：{instance.VersionId ?? "未配置"}");
                sb.AppendLine($"加载器      ：{instance.Loader}" +
                              (string.IsNullOrEmpty(instance.LoaderVersion) ? "" : $" {instance.LoaderVersion}"));
                sb.AppendLine($"分配内存    ：{instance.MemoryMb} MB");
                sb.AppendLine($"实例目录    ：{instance.Path}");
                sb.AppendLine($"Java 路径   ：{instance.JavaPath ?? "（自动选择）"}");

                var picked = JavaService.Pick(instance.VersionId, instance.JavaPath);
                sb.AppendLine($"实际使用 Java：{(picked is null ? "未找到可用的运行时" : $"Java {picked.Major}（{picked.Path}）")}");
                sb.AppendLine($"所需 Java 主版本：{JavaService.RequiredMajor(instance.VersionId)}");
                sb.AppendLine($"窗口        ：{instance.WindowWidth}x{instance.WindowHeight}" +
                              (instance.Fullscreen ? "（全屏）" : ""));
                sb.AppendLine($"JVM 参数    ：{instance.JvmArgs}");
                sb.AppendLine($"游戏参数    ：{instance.GameArgs}");
            }
            sb.AppendLine();
            sb.AppendLine("— 崩溃分析 —");
            sb.AppendLine($"结论        ：{analysis.Title}");
            if (!string.IsNullOrEmpty(analysis.Exception))
            {
                sb.AppendLine();
                sb.AppendLine("主异常：");
                sb.AppendLine(analysis.Exception);
            }
            if (analysis.Reasons.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("可能原因：");
                foreach (var r in analysis.Reasons)
                {
                    sb.AppendLine($"  · [{r.Severity}] {r.Title}");
                    sb.AppendLine($"      {r.Detail}");
                    if (!string.IsNullOrEmpty(r.Evidence))
                        sb.AppendLine($"      依据：{Trim(r.Evidence, 200)}");
                }
            }
            if (analysis.Suggestions.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("建议：");
                foreach (var sg in analysis.Suggestions)
                    sb.AppendLine($"  · {sg.Title}{(string.IsNullOrEmpty(sg.Action) ? "" : $"（{sg.Action}）")}");
            }
            if (!string.IsNullOrEmpty(analysis.StackTrace))
            {
                sb.AppendLine();
                sb.AppendLine("堆栈（节选）：");
                sb.AppendLine(Trim(analysis.StackTrace, 3000));
            }

            // —— 其他附件 ——
            var modsText = instance is null ? "（没有活跃实例）" : ListContent(instance.Path, "mods");
            var settingsJson = SanitizedSettings(settings);

            using (var zip = System.IO.Compression.ZipFile.Open(zipPath, System.IO.Compression.ZipArchiveMode.Create))
            {
                void AddText(string name, string text)
                {
                    var entry = zip.CreateEntry(name, System.IO.Compression.CompressionLevel.Optimal);
                    using var w = new StreamWriter(entry.Open(), new UTF8Encoding(false));
                    w.Write(text);
                }

                AddText("summary.txt", sb.ToString());
                AddText("mods.txt", modsText);
                AddText("settings.json", settingsJson);
                AddText("analysis.json", Json.SerializeIndented(analysis));

                // 最近一份启动器日志
                if (latest is not null)
                {
                    try
                    {
                        var full = File.ReadAllText(latest.Path);
                        AddText("launcher.log", Trim(full, 400_000));
                    }
                    catch { }
                }

                // 游戏日志与崩溃报告（如果实例有的话）
                if (instance is not null)
                {
                    ct.ThrowIfCancellationRequested();
                    AddDirToZip(zip, Path.Combine(instance.Path, "logs"), "game-logs", "*.log");
                    AddDirToZip(zip, Path.Combine(instance.Path, "crash-reports"), "crash-reports", "*.txt");
                }
            }

            var size = new FileInfo(zipPath).Length;
            Logger.Info($"[diagnose] 已导出诊断报告：{zipPath}（{size / 1024} KB）");
            return OpResult.Success($"诊断报告已导出（{size / 1024} KB）", zipPath);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "[diagnose] 导出诊断报告失败");
            return OpResult.Fail($"导出失败：{ex.Message}");
        }
    }

    private static void AddDirToZip(
        System.IO.Compression.ZipArchive zip, string dir, string prefix, string pattern)
    {
        try
        {
            if (!Directory.Exists(dir)) return;

            // 只取最近 5 份，避免报告体积失控
            var files = Directory.EnumerateFiles(dir, pattern)
                .OrderByDescending(f => File.GetLastWriteTimeUtc(f))
                .Take(5);

            foreach (var f in files)
            {
                var entry = zip.CreateEntry($"{prefix}/{Path.GetFileName(f)}",
                    System.IO.Compression.CompressionLevel.Optimal);
                using var w = new StreamWriter(entry.Open(), new UTF8Encoding(false));
                w.Write(Trim(File.ReadAllText(f), 200_000));
            }
        }
        catch (Exception ex)
        {
            Logger.Debug($"[diagnose] 打包 {dir} 失败：{ex.Message}");
        }
    }

    private static string ListContent(string instancePath, string kind)
    {
        try
        {
            var dir = InstanceService.ContentFolder(instancePath, kind);
            if (!Directory.Exists(dir)) return "（目录不存在）";

            var files = Directory.EnumerateFiles(dir)
                .Select(f => $"{(File.Exists(f + ".disabled") || f.EndsWith(".disabled") ? "[已禁用] " : "")}{Path.GetFileName(f)}  ({new FileInfo(f).Length / 1024} KB)")
                .OrderBy(x => x)
                .ToList();

            return files.Count == 0 ? "（空）" : string.Join("\n", files);
        }
        catch (Exception ex)
        {
            return $"（读取失败：{ex.Message}）";
        }
    }

    /// <summary>导出用设置：把代理、密钥、账号相关的字段全部脱敏。</summary>
    private static string SanitizedSettings(Settings s)
    {
        var map = new Dictionary<string, object?>
        {
            ["theme"] = s.Theme,
            ["accent"] = s.Accent,
            ["background"] = s.Background,
            ["uiMotion"] = s.UiMotion,
            ["uiPerf"] = s.UiPerf,
            ["language"] = s.Language,
            ["downloadSource"] = s.DownloadSource,
            ["maxThreads"] = s.MaxThreads,
            ["keepLauncherOpen"] = s.KeepLauncherOpen,
            ["launchCloseLauncher"] = s.LaunchCloseLauncher,
            ["updateRepo"] = s.UpdateRepo,
            ["autoUpdate"] = s.AutoUpdate,
            ["translateProvider"] = s.TranslateProvider,
            ["translateModel"] = s.TranslateModel,
            ["skinSiteUrl"] = s.SkinSiteUrl,

            // 这些一律不导出原文
            ["proxy"] = Mask(s.Proxy),
            ["translateApiKey"] = Mask(s.TranslateApiKey),
            ["microsoftClientId"] = Mask(s.MicrosoftClientId),
            ["dataRoot"] = StarCorePaths.DataRoot,
        };

        return Json.SerializeIndented(map);
    }

    private static string Mask(string? value)
        => string.IsNullOrWhiteSpace(value) ? "" : "（已脱敏）";

    private static long TotalMemoryMb()
    {
        try
        {
            // GC 拿到的只是"可用给 .NET 的内存"，不等于物理内存；
            // 这里用 WMI 之外最可靠的办法：读 GC 的 total available memory，
            // 拿不到就退化成 0，不影响报告的可读性。
            return GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1024 / 1024;
        }
        catch { return 0; }
    }

    private static string Trim(string text, int max)
        => string.IsNullOrEmpty(text) || text.Length <= max ? text : text[..max] + "\n…（已截断）";
}
