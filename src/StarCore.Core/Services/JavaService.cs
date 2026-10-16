using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using StarCore.Core.App;
using StarCore.Core.Models;
using StarCore.Core.Platform;
using System.Text.Json;

namespace StarCore.Core.Services;

/// <summary>
/// Java 运行时的探测与登记。
///
/// 探测策略（由快到慢，命中即记录）：
///   1. 读 <c>&lt;JAVA_HOME&gt;/release</c> 文本文件 —— 无需启动进程，最快
///   2. 注册表 JavaSoft\JDK|JRE 的 CurrentVersion 与 JavaHome
///   3. 常见发行版的安装目录（Adoptium / Microsoft / Zulu / BellSoft / Oracle / Liberica / Corretto）
///   4. PATH 上的 java
/// 只有当 release 文件缺失时才真正执行 java 进程解析版本号。
/// </summary>
public static class JavaService
{
    private static readonly object Gate = new();
    private static List<JavaInfo> _cache = new();

    /// <summary>常见发行版的安装根目录（相对 Program Files 的路径片段）。</summary>
    private static readonly string[] VendorSubDirs =
    {
        @"Eclipse Adoptium",
        @"Microsoft",
        @"Java",
        @"Zulu",
        @"BellSoft",
        @"BellSoft\LibericaJDK",
        @"Amazon Corretto",
        @"Semeru",
        @"RedHat",
    };

    public static IReadOnlyList<JavaInfo> Cached
    {
        get { lock (Gate) return _cache.ToList(); }
    }

    /// <summary>全量扫描并更新缓存。耗时集中在文件系统遍历，通常在 1 秒内。</summary>
    public static List<JavaInfo> Scan()
    {
        var found = new Dictionary<string, JavaInfo>(StringComparer.OrdinalIgnoreCase);

        foreach (var home in CandidateHomes())
        {
            var exe = Path.Combine(home, "bin", PlatformServices.Current.JavaExecutable);
            var exeAlt = Path.Combine(home, "bin", PlatformServices.Current.JavaExecutableFallback);
            var real = File.Exists(exe) ? exe : File.Exists(exeAlt) ? exeAlt : null;
            if (real is null) continue;

            // 以 javaw 作为记录路径（启动游戏用 javaw，不弹控制台）
            var record = File.Exists(exe) ? exe : real;
            if (found.ContainsKey(record)) continue;

            var info = Inspect(home, record);
            if (info is not null && info.Major > 0) found[record] = info;
        }

        // 保留用户手动添加过、但不在扫描结果里的条目
        lock (Gate)
        {
            foreach (var manual in _cache.Where(j => j.Valid && !VendorSubDirs.Any(d => j.Path.Contains(d, StringComparison.OrdinalIgnoreCase))))
            {
                if (!found.ContainsKey(manual.Path) && File.Exists(manual.Path)) found[manual.Path] = manual;
            }
            _cache = found.Values.OrderByDescending(j => j.Major).ThenBy(j => j.Path).ToList();
        }

        Logger.Info($"[java] 扫描完成，共发现 {_cache.Count} 个可用运行时");
        return _cache.ToList();
    }

    public static List<JavaInfo> List()
    {
        lock (Gate)
        {
            if (_cache.Count == 0) return Scan();
            return _cache.ToList();
        }
    }

    /// <summary>校验单个可执行文件是否是可用的 Java，并登记。</summary>
    public static JavaInfo? Validate(string javaExe)
    {
        if (string.IsNullOrWhiteSpace(javaExe) || !File.Exists(javaExe)) return null;
        var home = Path.GetDirectoryName(Path.GetDirectoryName(javaExe)) ?? "";
        var info = Inspect(home, javaExe);
        if (info is null) return null;

        lock (Gate)
        {
            _cache.RemoveAll(j => string.Equals(j.Path, info.Path, StringComparison.OrdinalIgnoreCase));
            _cache.Insert(0, info);
        }
        return info;
    }

    public static void Remove(string path)
    {
        lock (Gate) _cache.RemoveAll(j => string.Equals(j.Path, path, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 按游戏版本推断需要的 Java 主版本。
    ///
    /// 只是**兜底**：现代版本 JSON 里都带 javaVersion.majorVersion，调用方应当优先用它。
    /// 这里必须覆盖三种版本号形态，漏掉任何一种都会给版本配错 Java：
    ///   · 1.20.1 / 1.21.4      —— 经典形态
    ///   · 26.3                 —— 新形态（年.月），全是现代版本
    ///   · 24w14a               —— 快照形态
    /// </summary>
    public static int RequiredMajor(string? versionId)
    {
        if (string.IsNullOrWhiteSpace(versionId)) return 17;

        // 快照形态：24w14a —— 2024 年的快照，必然要 Java 21
        if (Regex.IsMatch(versionId, @"^\d{2}w\d{2}")) return 21;

        var m = Regex.Match(versionId, @"^(\d+)\.(\d+)(?:\.(\d+))?");
        if (!m.Success) return 17;

        var major = int.Parse(m.Groups[1].Value);
        var minor = int.Parse(m.Groups[2].Value);
        var patch = m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : 0;

        // 新形态版本号（26.3 这种）：月份从 1 数到 12，永远小于 20，
        // 旧实现在这一支上一路落到 return 8，把现代版本配成 Java 8 ——
        // 这才是"所有实例都启动不了"最早埋下的那颗雷。
        if (major > 1) return 21;

        if (minor >= 21) return 21;
        // 1.20.5 才切到 Java 21；1.20 – 1.20.4 仍然是 Java 17
        if (minor == 20) return patch >= 5 ? 21 : 17;
        if (minor >= 17) return 17;
        return 8;
    }

    /// <summary>
    /// 挑选最合适的 Java。
    /// <paramref name="requiredMajor"/> 来自版本 JSON 的 javaVersion（权威）；
    /// 为 0 或 null 时才退回按版本号推断。
    /// </summary>
    public static JavaInfo? Pick(string? versionId, string? preferredPath, int? requiredMajor = null)
    {
        var all = List();
        if (all.Count == 0) return null;

        // 用户显式指定的路径优先级最高 —— 那是他的明确意图，不该被自动匹配覆盖
        if (!string.IsNullOrWhiteSpace(preferredPath))
        {
            var hit = all.FirstOrDefault(j => string.Equals(j.Path, preferredPath, StringComparison.OrdinalIgnoreCase));
            if (hit is not null) return hit;
        }

        var need = requiredMajor is > 0 ? requiredMajor.Value : RequiredMajor(versionId);

        // 优先精确匹配；其次在"不低于要求"的里面挑最小的（省内存、启动快）；
        // 实在没有够高的，就给最大的那个 —— 让游戏自己去报不兼容，
        // 总好过因为"找不到 Java"什么都不做。
        return all.FirstOrDefault(j => j.Major == need)
               ?? all.Where(j => j.Major >= need).OrderBy(j => j.Major).FirstOrDefault()
               ?? all.OrderByDescending(j => j.Major).FirstOrDefault();
    }

    /* ------------------------------------------------------------------ */

    /* ==================================================================
       运行时探测

       上一版是**白名单式**：写死几个厂商目录名（Eclipse Adoptium / Java / Zulu …），
       加上 JavaSoft 的三条注册表键。实测在用户机器上 7 个 Java 只找到 1 个：

         ✗ Program Files\Honor\HNPCAIService\jdk   ← 厂商白名单永远想不到这种名字
         ✗ Users\<user>\.jdks\temurin-24             ← IntelliJ 下载 JDK 的位置
         ✗ AppData\Roaming\.minecraft\runtime\*     ← Minecraft 官方启动器下载的运行时

       所以改成**发现式**：不认目录名，只在若干根目录下有限深度地找"含 bin\java.exe 的目录"。
       厂商白名单保留着当快速路径，但不再是唯一的希望。

       性能：根目录限定 + 深度 ≤2 + 总时间预算，实测扫描耗时在百毫秒量级，
       并且它跑在启动时的后台预热里，不阻塞界面。
       ================================================================== */

    /// <summary>扫描的时间预算。超时就用已找到的结果，不让探测拖住启动。</summary>
    private const int ScanBudgetMs = 2500;

    /// <summary>单次扫描的目录枚举上限，防止病态目录结构把时间吃光。</summary>
    private const int MaxDirsVisited = 20000;

    private static IEnumerable<string> CandidateHomes()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string? p)
        {
            if (string.IsNullOrWhiteSpace(p)) return;
            try
            {
                var full = Path.GetFullPath(p);
                if (Directory.Exists(full)) set.Add(full);
            }
            catch { }
        }

        /* ---------- 1) 显式来源 ---------- */

        Add(Environment.GetEnvironmentVariable("JAVA_HOME"));

        // 启动器自己下载的 Java。**必须排除下载中的临时目录** —— 这是一次真实事故：
        // 下载中断会留下 <name>.tmp-<guid>（只有 exe 没有 dll），
        // 被当成可用运行时后用户会看到 Windows 的「无法找到入口」模态弹窗，
        // 而那个弹框是阻塞的，超时机制救不回来。
        try
        {
            var runtimeRoot = Path.Combine(StarCorePaths.DataRoot, "runtime");
            if (Directory.Exists(runtimeRoot))
                foreach (var sub in Directory.EnumerateDirectories(runtimeRoot))
                    if (!IsTempRuntimeDir(sub)) Add(sub);
        }
        catch { /* 目录不可读时忽略 */ }

        CleanupStaleTempRuntimes();

        // 注册表 / 系统配置库里的 JDK —— 这部分是平台专有的，交给平台实现
        foreach (var p in PlatformServices.Current.JavaFromSystemDatabase()) Add(p);

        AddFromPath(set);

        /* ---------- 2) 发现式扫描 ---------- */

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var visited = 0;

        foreach (var (root, depth) in PlatformServices.Current.JavaProbeRoots())
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) continue;
            if (sw.ElapsedMilliseconds > ScanBudgetMs) break;
            Discover(root, depth, set, sw, ref visited);
        }

        Logger.Debug($"[java] 候选目录 {set.Count} 个，扫描耗时 {sw.ElapsedMilliseconds}ms，访问 {visited} 个目录");

        // 去掉"被另一个候选包含"的目录。
        // 典型情况：JDK 8 里自带的 ...\jdk-8.0.502.7-hotspot\jre 会被单独扫到，
        // 但它和父目录是同一个运行时，列两遍只会让用户困惑。
        return PruneNested(set);
    }

    /// <summary>在 root 下扫 maxDepth 层，任何含 bin\java.exe|javaw.exe 的目录都算一个候选。</summary>
    private static void Discover(
        string root, int maxDepth, HashSet<string> sink,
        System.Diagnostics.Stopwatch sw, ref int visited)
    {
        var queue = new Queue<(string Dir, int Depth)>();
        queue.Enqueue((root, 0));

        while (queue.Count > 0)
        {
            if (sw.ElapsedMilliseconds > ScanBudgetMs) return;
            if (visited >= MaxDirsVisited) return;

            var (dir, depth) = queue.Dequeue();
            visited++;

            try
            {
                if (HasJavaExe(dir)) sink.Add(dir);

                if (depth >= maxDepth) continue;
                foreach (var child in Directory.EnumerateDirectories(dir))
                {
                    // 跳过明显无意义的目录，能省下大量枚举
                    var name = Path.GetFileName(child);
                    if (name.StartsWith('.') && !name.Equals(".jdks", StringComparison.OrdinalIgnoreCase)
                                             && !name.Equals(".minecraft", StringComparison.OrdinalIgnoreCase)
                                             && !name.Equals(".hmcl", StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (name.Equals("Windows", StringComparison.OrdinalIgnoreCase) ||
                        name.Equals("WinSxS", StringComparison.OrdinalIgnoreCase) ||
                        name.Equals("node_modules", StringComparison.OrdinalIgnoreCase))
                        continue;

                    queue.Enqueue((child, depth + 1));
                }
            }
            catch (UnauthorizedAccessException) { /* 系统目录常见，跳过 */ }
            catch (DirectoryNotFoundException) { }
            catch (IOException) { }
        }
    }

    private static bool HasJavaExe(string home)
    {
        try
        {
            var bin = Path.Combine(home, "bin");
            return Directory.Exists(bin)
                   && (File.Exists(Path.Combine(bin, PlatformServices.Current.JavaExecutable))
                       || File.Exists(Path.Combine(bin, PlatformServices.Current.JavaExecutableFallback)));
        }
        catch { return false; }
    }

    /// <summary>移除被另一个候选目录包含的候选（JDK 内嵌的 jre、重复的嵌套安装等）。</summary>
    private static List<string> PruneNested(HashSet<string> homes)
    {
        var list = homes.OrderBy(h => h.Length).ToList();
        var kept = new List<string>();

        foreach (var candidate in list)
        {
            var sep = Path.DirectorySeparatorChar;
            var nested = kept.Any(parent =>
                candidate.Length > parent.Length + 1 &&
                candidate.StartsWith(parent, StringComparison.OrdinalIgnoreCase) &&
                candidate[parent.Length] == sep);

            if (!nested) kept.Add(candidate);
        }

        return kept;
    }

    /* ------------------------------------------------------------------
       注册表
       ------------------------------------------------------------------ */

    /* ------------------------------------------------------------------
       PATH
       ------------------------------------------------------------------ */

    private static void AddFromPath(HashSet<string> sink)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";

        foreach (var part in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var dir = part.Trim().Trim('"').TrimEnd('\\');
                if (dir.Length == 0) continue;

                // 原实现只判断 javaw.exe —— 有些 JDK 的精简安装或经过软链后
                // 只有 java.exe，于是整条 PATH 白扫。两个都要认。
                if (File.Exists(Path.Combine(dir, PlatformServices.Current.JavaExecutableFallback)) ||
                    File.Exists(Path.Combine(dir, PlatformServices.Current.JavaExecutable)))
                {
                    var home = Path.GetDirectoryName(dir.TrimEnd('\\'));
                    if (!string.IsNullOrEmpty(home) && Directory.Exists(home)) sink.Add(Path.GetFullPath(home));
                }
            }
            catch { }
        }
    }

    private static JavaInfo? Inspect(string home, string exe)
    {
        // 执行之前先做结构检查，避免触发 Windows 加载器的模态错误框
        if (!LooksLikeCompleteRuntime(home))
        {
            Logger.Warn($"[java] 跳过结构不完整的运行时（缺少 JVM 动态库）：{home}");
            return null;
        }

        var info = new JavaInfo { Path = exe, Managed = false };

        // 启动器自己下载的运行时一律实测一遍。
        // 只信 release 文件是不够的：那个文件可能已经解开、而 dll 还是坏的，
        // 于是版本号看着正常、一启动就报错。系统 JDK 则信任 metadata 以省去
        // 每次扫描都起进程的开销。
        var managed = home.StartsWith(
            Path.Combine(StarCorePaths.DataRoot, "runtime"), StringComparison.OrdinalIgnoreCase);

        var release = Path.Combine(home, "release");
        if (File.Exists(release))
        {
            try
            {
                foreach (var line in File.ReadLines(release))
                {
                    var idx = line.IndexOf('=');
                    if (idx <= 0) continue;
                    var key = line[..idx].Trim();
                    var val = line[(idx + 1)..].Trim().Trim('"');

                    switch (key)
                    {
                        case "JAVA_VERSION": info.Version = val; break;
                        case "IMPLEMENTOR": info.Vendor = val; break;
                        case "OS_ARCH": info.Arch = NormalizeArch(val); break;
                    }
                }
            }
            catch { /* 忽略，落到进程探测 */ }
        }

        if (string.IsNullOrEmpty(info.Version) || managed)
        {
            var fromProc = ProbeVersion(exe);
            if (fromProc is null)
            {
                if (managed) Logger.Warn($"[java] 自带运行时无法执行，已忽略：{home}");
                return null;
            }
            info.Version = fromProc.Value.version;
            info.Vendor = fromProc.Value.vendor;
            info.Managed = managed;
        }

        info.Major = ParseMajor(info.Version);
        if (string.IsNullOrEmpty(info.Arch)) info.Arch = NormalizeArch(System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString());
        if (string.IsNullOrEmpty(info.Vendor)) info.Vendor = "Unknown";
        info.Valid = info.Major > 0;
        return info;
    }

    /* ------------------------------------------------------------------
       临时目录的识别与清理
       ------------------------------------------------------------------ */

    /// <summary>下载中/解压中的临时目录。它们的名字带这类后缀，绝不能被当成可用运行时。</summary>
    private static bool IsTempRuntimeDir(string dir)
    {
        var name = Path.GetFileName(dir) ?? "";
        if (name.StartsWith('.')) return true;
        return name.Contains(".tmp-", StringComparison.OrdinalIgnoreCase)
               || name.Contains(".extract-", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 清掉上次运行残留的临时运行时目录。
    ///
    /// 为什么在启动时无条件清：这些目录只可能来自"上一次下载没跑完"，
    /// 而下载是一个进程内行为 —— 进程都没了，那个目录必然是垃圾。
    /// 留着它除了浪费几百 MB，还会把一个坏掉的 java.exe 暴露给运行时选择逻辑。
    /// </summary>
    public static void CleanupStaleTempRuntimes()
    {
        try
        {
            var runtimeRoot = Path.Combine(StarCorePaths.DataRoot, "runtime");
            if (!Directory.Exists(runtimeRoot)) return;

            foreach (var dir in Directory.EnumerateDirectories(runtimeRoot))
            {
                if (!IsTempRuntimeDir(dir)) continue;
                try
                {
                    Directory.Delete(dir, true);
                    Logger.Info($"[java] 已清理残留的临时运行时目录：{Path.GetFileName(dir)}");
                }
                catch (Exception ex)
                {
                    // 文件被占用也不影响大局，下次启动再试
                    Logger.Warn($"[java] 清理临时目录失败（下次启动重试）：{ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Debug($"[java] 扫描临时运行时目录失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 结构完整性检查：在**执行之前**先确认这确实是一个完整的 Java 运行时。
    ///
    /// 为什么必须做：一旦对一个残缺的 java.exe 执行，Windows 加载器会弹出一个
    /// 模态的"无法找到入口"对话框，而那个对话框**会阻塞我们的进程直到用户点确定**。
    /// 光靠超时是救不回来的（进程已经卡在系统调用里了）。
    /// 所以只能提前用文件存在性把它挡掉。
    /// </summary>
    private static bool LooksLikeCompleteRuntime(string home)
    {
        var bin = Path.Combine(home, "bin");
        if (!Directory.Exists(bin)) return false;

        // java 可执行文件必须在
        if (!File.Exists(Path.Combine(bin, "java.exe")) &&
            !File.Exists(Path.Combine(bin, PlatformServices.Current.JavaExecutable))) return false;

        // 真正的运行时一定带着 JVM 动态库之一。
        // 只看 exe 是不够的：残缺下载往往 exe 已落地而 dll 还没下完。
        string[] markers =
        {
            Path.Combine(bin, "jli.dll"),
            Path.Combine(bin, "server", "jvm.dll"),
            Path.Combine(bin, "client", "jvm.dll"),
            Path.Combine(bin, "hotspot", "jvm.dll"),
        };
        return markers.Any(File.Exists);
    }

    private static (string version, string vendor)? ProbeVersion(string exe)
    {
        try
        {
            var psi = new ProcessStartInfo(exe, "-XshowSettings:properties -version")
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return null;
            var text = p.StandardError.ReadToEnd() + p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(6000)) { try { p.Kill(); } catch { } return null; }

            var vm = Regex.Match(text, @"java\.version\s*=\s*(\S+)");
            var vendor = Regex.Match(text, @"java\.vendor\s*=\s*(.+)");
            if (!vm.Success) return null;
            return (vm.Groups[1].Value.Trim(), vendor.Success ? vendor.Groups[1].Value.Trim() : "Unknown");
        }
        catch (Exception ex)
        {
            Logger.Debug($"[java] 进程探测失败 {exe}：{ex.Message}");
            return null;
        }
    }

    private static int ParseMajor(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return 0;
        var v = version.Trim().TrimStart('"');
        var m = Regex.Match(v, @"^(\d+)");
        if (!m.Success) return 0;
        var n = int.Parse(m.Groups[1].Value);
        if (n > 1) return n;                       // 9, 11, 17, 21 …
        var m2 = Regex.Match(v, @"^1\.(\d+)");     // 1.8.0_xxx → 8
        return m2.Success ? int.Parse(m2.Groups[1].Value) : 8;
    }

    private static string NormalizeArch(string raw)
    {
        var s = raw.ToLowerInvariant();
        if (s.Contains("64") || s.Contains("x86_64") || s.Contains("amd64")) return "x64";
        if (s.Contains("86")) return "x86";
        if (s.Contains("arm64") || s.Contains("aarch64")) return "arm64";
        return raw;
    }

    /* ==================================================================
       Java 自动下载
       ------------------------------------------------------------------
       为什么必须做：Minecraft 1.17 起需要 Java 16+，1.20.5 起需要 Java 21。
       但绝大多数 Windows 用户机器上只有某个旧的 Java 8（或干脆没有）。
       不提供自动下载，用户就会卡在"启动失败：找不到合适的 Java"上无处可去 ——
       这正是 HMCL / PCL 都内置 Java 下载的原因。

       两条来源：
         · Mojang 官方运行时清单 —— 覆盖 Java 8/17/21/25，与官方启动器同源，最可靠；
         · Adoptium API —— 兜底，覆盖 Mojang 清单里没有的大版本（例如较老的 11）。
       ================================================================== */

    /// <summary>Mojang 官方运行时清单的固定 ID。这是官方启动器写死的常量，不要参数化。</summary>
    private const string MojangRuntimeManifest =
        "https://launchermeta.mojang.com/v1/products/java-runtime/2ec0cc96c44e5a76b9c8b7c39df7210883d12871/all.json";

    /// <summary>
    /// 后台下载一个 Java 运行时，立即返回 taskId。
    /// 界面拿到 taskId 就能显示进度，不必等下载完成。
    /// </summary>
    public static string StartDownload(int major, string? title = null)
    {
        var taskId = TaskCenter.Create(title ?? $"下载 Java {major}");
        _ = Task.Run(() => DownloadAsync(major, taskId, TaskCenter.Token(taskId)));
        return taskId;
    }

    /// <summary>下载并纳管一个 Java 运行时。失败时任务会被标记为失败。</summary>
    public static async Task<JavaInfo?> DownloadAsync(int major, string taskId, CancellationToken ct)
    {
        var root = Path.Combine(StarCorePaths.DataRoot, "runtime");
        Directory.CreateDirectory(root);

        try
        {
            TaskCenter.Start(taskId, "正在解析 Java 运行时清单…");

            // —— 先试 Mojang 官方清单 ——
            var fromMojang = await TryMojangRuntimeAsync(major, root, taskId, ct);
            if (fromMojang is not null)
            {
                TaskCenter.Complete(taskId, $"Java {fromMojang.Major} 已就绪");
                AfterInstall(fromMojang);
                return fromMojang;
            }

            // —— 兜底：Adoptium ——
            TaskCenter.Report(taskId, t => t.Detail = "官方清单里没有这个版本，改用 Adoptium…");
            var fromAdoptium = await TryAdoptiumAsync(major, root, taskId, ct);
            if (fromAdoptium is not null)
            {
                TaskCenter.Complete(taskId, $"Java {fromAdoptium.Major} 已就绪");
                AfterInstall(fromAdoptium);
                return fromAdoptium;
            }

            TaskCenter.Fail(taskId, $"没有找到可下载的 Java {major}。请手动安装后到「设置 → Java」添加。");
            return null;
        }
        catch (OperationCanceledException)
        {
            TaskCenter.Cancel(taskId);
            return null;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, $"[java] 下载 Java {major} 失败");
            TaskCenter.Fail(taskId, ex.Message);
            return null;
        }
    }

    /// <summary>
    /// 下载完成后重扫并把它记进"已知 Java"。
    /// 不重扫的话，用户在设置页看到的仍然是旧列表，会以为下载没成功。
    /// </summary>
    private static void AfterInstall(JavaInfo info)
    {
        try
        {
            var all = Scan();
            var found = all.FirstOrDefault(j => string.Equals(j.Path, info.Path, StringComparison.OrdinalIgnoreCase));
            if (found is not null) found.Managed = true;
            Logger.Info($"[java] 已下载并纳管 Java {info.Major}（{info.Path}）");
        }
        catch (Exception ex)
        {
            Logger.Warn($"[java] 下载后重扫失败（不影响使用）：{ex.Message}");
        }
    }

    /* ------------------------------------------------------------------
       Mojang 官方运行时
       ------------------------------------------------------------------ */

    private static async Task<JavaInfo?> TryMojangRuntimeAsync(
        int major, string root, string taskId, CancellationToken ct)
    {
        // 平台键：只处理 Windows（本项目就是 WinExe）
        var platform = Environment.Is64BitOperatingSystem ? "windows-x64" : "windows-x86";

        var manifestJson = await DownloadSource.GetStringAsync(MojangRuntimeManifest, ct);
        using var doc = JsonDocument.Parse(manifestJson);

        if (!doc.RootElement.TryGetProperty(platform, out var platformNode))
            return null;

        // 在平台下找主版本号匹配的运行时
        string? runtimeName = null;
        string? manifestUrl = null;

        foreach (var prop in platformNode.EnumerateObject())
        {
            if (prop.Value.ValueKind != JsonValueKind.Array) continue;
            foreach (var item in prop.Value.EnumerateArray())
            {
                var verName = item.TryGetProperty("version", out var v) && v.TryGetProperty("name", out var n)
                    ? n.GetString() : null;
                var mUrl = item.TryGetProperty("manifest", out var mf) && mf.TryGetProperty("url", out var u)
                    ? u.GetString() : null;
                if (mUrl is null) continue;

                // version.name 形如 "17.0.8"，取主版本号比对
                var maj = MajorOfVersionName(verName);
                if (maj == major)
                {
                    runtimeName = prop.Name;
                    manifestUrl = mUrl;
                    break;
                }
            }
            if (manifestUrl is not null) break;
        }

        if (manifestUrl is null) return null;

        TaskCenter.Report(taskId, t => t.Detail = $"正在获取 {runtimeName} 的文件清单…");

        var filesJson = await DownloadSource.GetStringAsync(manifestUrl, ct);
        using var filesDoc = JsonDocument.Parse(filesJson);
        if (!filesDoc.RootElement.TryGetProperty("files", out var files))
            return null;

        var dirName = runtimeName ?? $"java-{major}";
        var finalDir = Path.Combine(root, dirName);

        // 先下到临时目录，全部成功后再原子改名。
        // 直接下到目标目录的话，中途失败会留下一个"看起来存在但用不了"的运行时，
        // 启动时选中它就会莫名崩溃，而且用户完全不知道问题出在哪。
        var tempDir = finalDir + $".tmp-{Guid.NewGuid():N}";
        Directory.CreateDirectory(tempDir);

        try
        {
            var entries = new List<(string Rel, string Url, string? Sha1, bool Executable)>();
            foreach (var f in files.EnumerateObject())
            {
                var type = f.Value.TryGetProperty("type", out var ty) ? ty.GetString() : null;
                if (type != "file") continue;

                var raw = f.Value.TryGetProperty("downloads", out var dl) && dl.TryGetProperty("raw", out var r)
                    ? r : default;
                if (raw.ValueKind != JsonValueKind.Object) continue;

                var url = raw.TryGetProperty("url", out var uu) ? uu.GetString() : null;
                if (string.IsNullOrEmpty(url)) continue;

                entries.Add((
                    f.Name,
                    url!,
                    raw.TryGetProperty("sha1", out var sh) ? sh.GetString() : null,
                    f.Value.TryGetProperty("executable", out var ex) && ex.ValueKind == JsonValueKind.True));
            }

            var total = entries.Count;
            var done = 0;
            long bytes = 0;

            foreach (var (rel, url, sha1, _) in entries)
            {
                ct.ThrowIfCancellationRequested();

                var dest = Path.Combine(tempDir, rel.Replace('/', Path.DirectorySeparatorChar));
                var got = await DownloadSource.DownloadAsync(url, dest, ct);
                bytes += got;
                done++;

                if (done % 8 == 0 || done == total)
                    TaskCenter.Progress(taskId, done, total, bytes, 0, $"正在下载 {done}/{total} 个文件");
            }

            // 收尾：改名到最终位置
            if (Directory.Exists(finalDir)) Directory.Delete(finalDir, true);
            Directory.Move(tempDir, finalDir);
        }
        catch
        {
            SafeDelete(tempDir);
            throw;
        }

        var javaw = FindJavaExe(finalDir);
        if (javaw is null)
        {
            SafeDelete(finalDir);
            throw new InvalidOperationException("下载完成但没有找到 java 可执行文件，运行时包可能不完整。");
        }

        var info = Validate(javaw);
        if (info is null)
        {
            SafeDelete(finalDir);
            throw new InvalidOperationException("下载下来的 Java 无法执行，可能被杀毒软件拦截。");
        }

        return info;
    }

    /* ------------------------------------------------------------------
       Adoptium（兜底）
       ------------------------------------------------------------------ */

    private static async Task<JavaInfo?> TryAdoptiumAsync(
        int major, string root, string taskId, CancellationToken ct)
    {
        var arch = Environment.Is64BitOperatingSystem ? "x64" : "x86";
        var url = $"https://api.adoptium.net/v3/assets/latest/{major}/hotspot"
                  + $"?architecture={arch}&image_type=jdk&os=windows&vendor=eclipse";

        var json = await DownloadSource.GetStringAsync(url, ct);
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0)
            return null;

        var link = doc.RootElement[0].TryGetProperty("binary", out var b)
                   && b.TryGetProperty("package", out var pkg)
                   && pkg.TryGetProperty("link", out var lk)
            ? lk.GetString()
            : null;

        var name = doc.RootElement[0].TryGetProperty("binary", out var b2)
                   && b2.TryGetProperty("package", out var pkg2)
                   && pkg2.TryGetProperty("name", out var nm)
            ? nm.GetString()
            : null;

        if (string.IsNullOrEmpty(link)) return null;

        TaskCenter.Report(taskId, t => t.Detail = "正在下载 Adoptium 发行版…");

        var zip = Path.Combine(root, (name ?? $"temurin-{major}.zip")!);
        await DownloadSource.DownloadAsync(link!, zip, ct);

        var tempDir = Path.Combine(root, $".extract-{Guid.NewGuid():N}");
        var finalDir = Path.Combine(root, $"temurin-{major}-{arch}");

        try
        {
            TaskCenter.Report(taskId, t => t.Detail = "正在解压…");
            System.IO.Compression.ZipFile.ExtractToDirectory(zip, tempDir, true);

            // Adoptium 的 zip 里有一层版本号目录，把它提升上来，
            // 否则 candidate 扫描到的路径层级和别人不一致
            var inner = Directory.EnumerateDirectories(tempDir).ToList();
            var source = inner.Count == 1 && Directory.Exists(Path.Combine(inner[0], "bin"))
                ? inner[0] : tempDir;

            if (Directory.Exists(finalDir)) Directory.Delete(finalDir, true);
            Directory.Move(source, finalDir);
        }
        finally
        {
            SafeDelete(tempDir);
            try { if (File.Exists(zip)) File.Delete(zip); } catch { }
        }

        var javaw = FindJavaExe(finalDir);
        if (javaw is null) { SafeDelete(finalDir); return null; }

        return Validate(javaw);
    }

    /* ------------------------------------------------------------------
       工具
       ------------------------------------------------------------------ */

    private static int MajorOfVersionName(string? versionName)
    {
        if (string.IsNullOrWhiteSpace(versionName)) return 0;
        var span = versionName.Trim();
        // 老版本是 1.8.0_402 这种，主版本号在第二位
        if (span.StartsWith("1.", StringComparison.Ordinal))
        {
            var dot = span.IndexOf('.', 2);
            return int.TryParse(dot > 2 ? span[2..dot] : span[2..], out var legacy) ? legacy : 0;
        }
        var end = 0;
        while (end < span.Length && char.IsDigit(span[end])) end++;
        return end > 0 && int.TryParse(span[..end], out var maj) ? maj : 0;
    }

    /// <summary>在运行时目录里找 javaw.exe（找不到就退回 java.exe）。</summary>
    private static string? FindJavaExe(string runtimeDir)
    {
        var bin = Path.Combine(runtimeDir, "bin");
        foreach (var exe in new[] { PlatformServices.Current.JavaExecutable, PlatformServices.Current.JavaExecutableFallback })
        {
            var p = Path.Combine(bin, exe);
            if (File.Exists(p)) return p;
        }

        // 少数发行版会多套一层目录
        try
        {
            foreach (var sub in Directory.EnumerateDirectories(runtimeDir))
            {
                var p = Path.Combine(sub, "bin", PlatformServices.Current.JavaExecutable);
                if (File.Exists(p)) return p;
            }
        }
        catch { }

        return null;
    }

    private static void SafeDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
        catch (Exception ex) { Logger.Warn($"[java] 清理临时目录失败：{dir}（{ex.Message}）"); }
    }
}
