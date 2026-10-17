using System.Diagnostics;
using System.Text;
using System.Text.Json;
using StarCore.Core.App;
using StarCore.Core.Models;

namespace StarCore.Core.Services;

/// <summary>
/// 组装启动命令行并托管游戏进程。
///
/// 这里做的其实是"把版本 JSON 翻译成一条 javaw 命令行"：
///   1. 展开继承链拿到最终描述
///   2. 按规则筛选库、拼出 classpath
///   3. 替换 ${...} 占位符
///   4. 启动进程、把输出同时写进日志文件与界面
/// </summary>
public static class LaunchService
{
    private static readonly Dictionary<long, Process> Running = new();
    private static readonly Dictionary<long, DateTimeOffset> StartedAt = new();
    private static readonly object Gate = new();

    /// <summary>状态变化时推送（前端据此更新按钮与状态条）。</summary>
    public static event Action<LaunchState>? StateChanged;

    public static LaunchState Current { get; private set; } = new();

    public static bool IsRunning(long instanceId)
    {
        lock (Gate) return Running.TryGetValue(instanceId, out var p) && !p.HasExited;
    }

    public static long? RunningInstanceId
    {
        get
        {
            lock (Gate) return Running.FirstOrDefault(kv => !kv.Value.HasExited).Key is var k && k != 0 ? k : null;
        }
    }

    private static void Push(LaunchState s)
    {
        Current = s;
        try { StateChanged?.Invoke(s); } catch (Exception ex) { Logger.Warn($"[launch] 推送状态失败：{ex.Message}"); }
    }

    /* ==================================================================
       启动
       ================================================================== */

    /* ==================================================================
       启动计划

       把"从实例到一条完整命令行"的全部推导集中在这里，好处有两个：
         · StartAsync 只剩"起进程、接输出、等退出"，读起来是一条直线；
         · 可以只看不启动 —— Inspect 走的就是同一条推导路径，
           于是"用户报告的命令行"和"真正跑的命令行"不可能不一致。
       ================================================================== */

    /// <summary>一次启动所需的全部要素。</summary>
    public sealed record LaunchPlan(
        long InstanceId,
        string LaunchVersionId,
        JavaInfo Java,
        int DeclaredJavaMajor,
        string ClassPath,
        string NativesDir,
        string MainClass,
        IReadOnlyList<string> JvmArgs,
        IReadOnlyList<string> GameArgs,
        string CommandLine,
        IReadOnlyList<string> Problems);

    /// <summary>只推导、不启动：把将要执行的完整命令行交出来，供排查与自检使用。</summary>
    public static async Task<LaunchPlan> InspectAsync(long instanceId, CancellationToken ct = default)
    {
        var inst = InstanceService.Get(instanceId)
                   ?? throw new InvalidOperationException("实例不存在。");

        if (string.IsNullOrWhiteSpace(inst.VersionId))
            throw new InvalidOperationException("该实例还没有安装游戏版本。");

        // 没有账户也要能看命令行 —— 用占位会话，否则"未登录"会把体检也一起挡掉，
        // 而那恰恰是用户最需要看命令行的时候。
        var account = AccountService.BuildSession(AccountService.ActiveUuid ?? "")
                      ?? PlaceholderSession(inst);

        return await BuildPlanAsync(inst, account, ct);
    }

    private static AccountService.LaunchSession PlaceholderSession(InstanceInfo inst) => new()
    {
        Name = "（未登录）",
        Uuid = "00000000000000000000000000000000",
        AccessToken = "0",
        ClientId = "starcore",
        Xuid = "0",
        UserType = "legacy",
    };

    /// <summary>把实例翻译成一条完整的命令行。所有启动路径都必须经过这里。</summary>
    private static async Task<LaunchPlan> BuildPlanAsync(
        InstanceInfo inst, AccountService.LaunchSession account, CancellationToken ct)
    {
        // 1) 实际要启动的版本 id（加载器会生成自己的版本目录）
        var launchVersionId = ResolveLaunchVersionId(inst);

        // 1.5) 安装完整性体检。
        // 必须放在展开版本描述之前：ResolveVersionAsync 会去网上补缺失的版本 JSON，
        // 于是"版本 JSON 在、主 jar 不在"这种半成品能一路走到进程启动，最后死在
        // Java 的「找不到或无法加载主类」上 —— 那个报错对用户毫无指向性。
        var problems = DiagnoseInstall(inst, launchVersionId);
        if (problems.Count > 0)
            throw new InvalidOperationException(
                $"这个实例的安装不完整：{string.Join("；", problems)}。"
                + "请到「下载」页对它重新执行一次安装（已下好的文件会自动跳过，只补缺的部分）。");

        // 2) 展开版本描述
        var version = await InstallService.ResolveVersionAsync(inst.Path, launchVersionId, ct);

        // 3) Java —— 版本 JSON 里的 javaVersion.majorVersion 才是权威要求，
        //    只有老版本（没有该字段）才退回按版本号推断。
        var declaredJava = ReadDeclaredJavaMajor(version);
        var java = JavaService.Pick(launchVersionId, inst.JavaPath, declaredJava)
                   ?? throw new InvalidOperationException("没有找到可用的 Java 运行时，请先在设置中添加。");

        // 4) classpath
        var (classPath, nativeDir) = BuildClasspath(inst, version, launchVersionId);

        // 5) 参数
        var mainClass = version.TryGetProperty("mainClass", out var mc)
            ? mc.GetString() ?? "net.minecraft.client.main.Main"
            : "net.minecraft.client.main.Main";

        var assetsIndex = version.TryGetProperty("assetIndex", out var ai) && ai.TryGetProperty("id", out var aid)
            ? aid.GetString() ?? "legacy"
            : "legacy";

        var vars = BuildVariables(inst, account, launchVersionId, classPath, nativeDir, assetsIndex);
        var jvm = CollectArguments(version, "jvm", vars);
        var game = CollectArguments(version, "game", vars);

        // 老版本用 minecraftArguments 单串，且没有 jvm 段
        if (jvm.Count == 0)
        {
            jvm.AddRange(new[]
            {
                $"-Djava.library.path={nativeDir}",
                "-Dminecraft.launcher.brand=StarCore",
                $"-Dminecraft.launcher.version={StarCorePaths.ProductVersion}",
                "-cp", classPath,
            });
        }
        if (game.Count == 0 && version.TryGetProperty("minecraftArguments", out var legacy)
                            && legacy.ValueKind == JsonValueKind.String)
        {
            game.AddRange(SplitArgs(legacy.GetString()!));
        }

        // Java 8 不认识模块系统的开关；用户手动把 Java 指到 8 时先摘干净，
        // 否则 JVM 直接「Unrecognized option: --add-opens」退出。
        if (java.Major < 9) jvm = StripModuleArgs(jvm);

        // 用户自定义参数放在最前，方便覆盖默认值
        var finalJvm = new List<string>();
        if (!string.IsNullOrWhiteSpace(inst.JvmArgs)) finalJvm.AddRange(SplitArgs(inst.JvmArgs));
        finalJvm.Add($"-Xmx{Math.Max(512, inst.MemoryMb)}M");
        finalJvm.Add($"-Xms{Math.Min(2048, Math.Max(512, inst.MemoryMb / 4))}M");

        /* ⚠️ 这一行是整个「所有实例都启动不了、退出码 1」的根因，改动前请先读这段话。
           版本 JSON 的 jvm 段里带着 -cp ${classpath}、-Djava.library.path=…、
           -Dminecraft.launcher.brand=… 等全部关键开关。原实现在上面算出了 jvm，
           但 finalJvm 是另起一个 List 重新拼的，从头到尾没引用 jvm ——
           于是命令行只剩「-Xmx -Xms 主类 …」，Java 找不到 classpath，
           直接报「找不到或无法加载主类 net.minecraft.client.main.Main」并退出码 1。 */
        finalJvm.AddRange(jvm);

        if (inst.Fullscreen)
        {
            game.Add("--fullscreen");
        }
        else if (!game.Contains("--width"))
        {
            game.Add("--width"); game.Add(inst.WindowWidth.ToString());
            game.Add("--height"); game.Add(inst.WindowHeight.ToString());
        }
        if (!string.IsNullOrWhiteSpace(inst.GameArgs)) game.AddRange(SplitArgs(inst.GameArgs));

        var cmdLine = FormatCommandLine(java.Path, finalJvm, mainClass, game);

        return new LaunchPlan(
            inst.Id, launchVersionId, java, declaredJava, classPath, nativeDir,
            mainClass, finalJvm, game, cmdLine, problems);
    }

    /// <summary>把命令行的四个部分拼成一行可读文本（含引号处理）。</summary>
    private static string FormatCommandLine(
        string javaPath, IEnumerable<string> jvm, string mainClass, IEnumerable<string> game)
    {
        var sb = new StringBuilder();
        sb.Append('"').Append(javaPath).Append('"');
        foreach (var a in jvm) sb.Append(' ').Append(a.Contains(' ') ? $"\"{a}\"" : a);
        sb.Append(' ').Append(mainClass);
        foreach (var a in game) sb.Append(' ').Append(a.Contains(' ') ? $"\"{a}\"" : a);
        return sb.ToString();
    }

    /// <summary>
    /// 启动实例。命令行由 <see cref="BuildPlanAsync"/> 推导 ——
    /// 这里只负责"起进程、接输出、等退出"。
    /// </summary>
    /// <returns>游戏进程的 PID。</returns>
    public static async Task<int> StartAsync(long instanceId, CancellationToken ct = default)
    {
        var inst = InstanceService.Get(instanceId)
                   ?? throw new InvalidOperationException("实例不存在。");

        if (IsRunning(instanceId))
            throw new InvalidOperationException("该实例已经在运行中。");

        if (string.IsNullOrWhiteSpace(inst.VersionId))
            throw new InvalidOperationException("该实例还没有安装游戏版本。");

        var account = AccountService.BuildSession(AccountService.ActiveUuid ?? "")
                      ?? throw new InvalidOperationException("请先添加一个账户。");

        Push(new LaunchState { State = "preparing", InstanceId = instanceId });

        var plan = await BuildPlanAsync(inst, account, ct);

        // 启动前命令
        // 游戏运行目录：默认隔离在实例目录里（mods/存档/配置全套独立）；
        // 用户在实例概览里切换为"共用"后，改用指定的共用目录。
        var gameDir = inst.GameDirMode == "shared" && !string.IsNullOrWhiteSpace(inst.SharedGameDir)
            ? inst.SharedGameDir!
            : inst.Path;

        await RunHookAsync(inst.PreLaunchCommand, gameDir, ct);

        var psi = new ProcessStartInfo(plan.Java.Path)
        {
            WorkingDirectory = gameDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in plan.JvmArgs) psi.ArgumentList.Add(a);
        psi.ArgumentList.Add(plan.MainClass);
        foreach (var a in plan.GameArgs) psi.ArgumentList.Add(a);

        Logger.Info($"[launch] Java：{plan.Java.Path}（{plan.Java.Vendor} {plan.Java.Major}）");
        var cpCount = plan.ClassPath.Length == 0 ? 0 : plan.ClassPath.Split(';').Length;
        Logger.Info($"[launch] 主类：{plan.MainClass}，库 {cpCount} 项，内存 {inst.MemoryMb}MB");
        Logger.Debug($"[launch] 命令行：{plan.CommandLine}");

        var startTime = DateTimeOffset.Now;
        var proc = Process.Start(psi) ?? throw new InvalidOperationException("无法启动游戏进程。");

        lock (Gate) { Running[instanceId] = proc; StartedAt[instanceId] = startTime; }

        Push(new LaunchState { State = "running", InstanceId = instanceId, Pid = proc.Id, StartedAt = startTime });

        // 8) 接管输出
        var gameLog = Path.Combine(inst.Path, "logs", $"starcore-{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.log");
        Directory.CreateDirectory(Path.GetDirectoryName(gameLog)!);

        proc.OutputDataReceived += (_, e) => OnLine(e.Data, gameLog);
        proc.ErrorDataReceived += (_, e) => OnLine(e.Data, gameLog, "STDERR");
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        // 9) 结束处理
        _ = Task.Run(async () =>
        {
            try { await proc.WaitForExitAsync(); }
            catch { /* 进程被强杀 */ }

            var end = DateTimeOffset.Now;
            InstanceService.MarkPlayed(instanceId, startTime, end);

            lock (Gate) { Running.Remove(instanceId); StartedAt.Remove(instanceId); }

            var exit = -1;
            try { exit = proc.ExitCode; } catch { }

            // 退出码为 0 是正常退出；非 0 视为崩溃（Minecraft 正常退出总是 0）
            var crashed = exit != 0;
            Push(new LaunchState
            {
                State = crashed ? "crashed" : "exited",
                InstanceId = instanceId,
                ExitCode = exit,
                StartedAt = startTime,
                Message = crashed ? $"游戏以退出码 {exit} 结束" : "游戏已退出",
            });

            Logger.Info($"[launch] 游戏结束，退出码 {exit}，时长 {(end - startTime).TotalMinutes:F1} 分钟");

            await RunHookAsync(inst.PostLaunchCommand, inst.Path, CancellationToken.None);

            if (inst.PostLaunchCommand is null && SettingsService.Current.LaunchCloseLauncher)
            {
                // 由宿主决定是否真的退出（内核不直接控制 UI 生命周期）
                try { QuitRequested?.Invoke(); } catch { }
            }

            proc.Dispose();
        });

        return proc.Id;
    }

    /// <summary>宿主订阅它来响应"启动完成后关闭启动器"。</summary>
    public static event Action? QuitRequested;

    private static void OnLine(string? line, string logFile, string? tag = null)
    {
        if (line is null) return;
        var text = tag is null ? line : $"[{tag}] {line}";

        try { File.AppendAllText(logFile, text + Environment.NewLine); } catch { }

        var level = text.Contains("ERROR", StringComparison.OrdinalIgnoreCase) ||
                    text.Contains("Exception", StringComparison.OrdinalIgnoreCase)
            ? LogLevel.Error
            : text.Contains("WARN", StringComparison.OrdinalIgnoreCase) ? LogLevel.Warn : LogLevel.Debug;

        Logger.Write(level, text);
        try { GameOutput?.Invoke(text); } catch { }
    }

    /// <summary>游戏原始输出行（前端日志页实时显示）。</summary>
    public static event Action<string>? GameOutput;

    public static bool Kill(long instanceId)
    {
        Process? p;
        lock (Gate) Running.TryGetValue(instanceId, out p);
        if (p is null || p.HasExited) return false;

        try
        {
            p.Kill(true);
            Logger.Info($"[launch] 已强制结束游戏进程（实例 #{instanceId}）");
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "[launch] 结束进程失败");
            return false;
        }
    }

    /* ==================================================================
       组装
       ================================================================== */

    /// <summary>
    /// 实际要启动的版本目录名。加载器会生成自己的版本目录，
    /// 例如 1.20.1 装 Forge 后目录是 1.20.1-forge-47.4.0。
    /// </summary>
    public static string ResolveLaunchVersionId(InstanceInfo inst)
    {
        var baseId = inst.VersionId ?? "";
        if (inst.Loader is "vanilla" or null or "") return baseId;

        if (inst.Loader is "fabric" or "quilt")
        {
            if (!string.IsNullOrWhiteSpace(inst.LoaderVersion) && inst.LoaderVersion.Contains(baseId, StringComparison.OrdinalIgnoreCase))
                return inst.LoaderVersion!;
        }

        var dir = InstanceService.VersionDir(inst.Path);
        if (Directory.Exists(dir))
        {
            try
            {
                var match = Directory.EnumerateDirectories(dir)
                    .Select(Path.GetFileName)
                    .Where(n => n is not null
                                && n.StartsWith(baseId, StringComparison.OrdinalIgnoreCase)
                                && n.Contains(inst.Loader, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(n => n!.Length)
                    .FirstOrDefault();
                if (match is not null) return match;
            }
            catch { }
        }

        return baseId;
    }

    /* ==================================================================
       启动前体检
       ================================================================== */

    /// <summary>
    /// 检查"这个实例现在能不能启动"，返回人话描述的问题列表（空 = 没问题）。
    /// 目的是把"缺文件"翻译成用户能看懂、能自己解决的提示。
    /// </summary>
    private static List<string> DiagnoseInstall(InstanceInfo inst, string launchVersionId)
    {
        var problems = new List<string>();

        try
        {
            var vdir = InstanceService.VersionDir(inst.Path);

            // 版本描述：启动时要用它翻译命令行，必须有
            if (!File.Exists(Path.Combine(vdir, launchVersionId, $"{launchVersionId}.json")))
                problems.Add($"缺少版本描述 {launchVersionId}.json");

            // 客户端主 jar：加载器版本有自己的目录，但 jar 往往在它继承的原版目录里，
            // 所以两个位置都要看。
            var jars = new List<string> { Path.Combine(vdir, launchVersionId, $"{launchVersionId}.jar") };
            if (!string.IsNullOrWhiteSpace(inst.VersionId))
                jars.Add(Path.Combine(vdir, inst.VersionId, $"{inst.VersionId}.jar"));

            if (!jars.Any(File.Exists))
                problems.Add($"缺少客户端主文件（{launchVersionId}.jar）");

            // 依赖库：一个 jar 都没有，说明整批库都没落地
            var libDir = InstanceService.LibraryDir(inst.Path);
            var hasAnyLib = Directory.Exists(libDir)
                            && Directory.EnumerateFiles(libDir, "*.jar", SearchOption.AllDirectories).Any();
            if (!hasAnyLib) problems.Add("依赖库目录为空");
        }
        catch (Exception ex)
        {
            // 体检本身出错不应该挡住启动，记一笔就够了
            Logger.Warn($"[launch] 安装体检失败（已跳过）：{ex.Message}");
        }

        return problems;
    }

    /// <summary>读版本 JSON 声明的 Java 主版本要求；没有该字段就返回 0。</summary>
    private static int ReadDeclaredJavaMajor(JsonElement version)
    {
        if (!version.TryGetProperty("javaVersion", out var jv) || jv.ValueKind != JsonValueKind.Object) return 0;
        if (!jv.TryGetProperty("majorVersion", out var mv)) return 0;
        return mv.TryGetInt32(out var n) ? n : 0;
    }

    /// <summary>
    /// 摘掉老 Java 不认识的模块系统开关（--add-opens / --add-exports / --add-modules /
    /// --enable-native-access 等）。这些开关的取值是紧跟其后的独立参数
    /// （形如 java.base/java.lang=ALL-UNNAMED），必须连同取值一起摘，
    /// 否则那个取值会被 Java 当成主类名。
    /// </summary>
    private static List<string> StripModuleArgs(List<string> args)
    {
        string[] prefixes =
        {
            "--add-opens", "--add-exports", "--add-modules", "--add-reads",
            "--enable-native-access", "--sun-misc-unsafe-memory-access",
        };

        var result = new List<string>(args.Count);
        for (var i = 0; i < args.Count; i++)
        {
            var a = args[i];
            if (prefixes.Any(p => a.StartsWith(p, StringComparison.Ordinal)))
            {
                // 单参数形式（--add-opens=x=y）已经自带取值，不用看下一个
                if (!a.Contains('=') && i + 1 < args.Count && !args[i + 1].StartsWith('-')) i++;
                continue;
            }
            result.Add(a);
        }
        return result;
    }

    private static (string classpath, string nativeDir) BuildClasspath(InstanceInfo inst, JsonElement version, string launchVersionId)
    {
        var libRoot = InstanceService.LibraryDir(inst.Path);
        var entries = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (version.TryGetProperty("libraries", out var libs) && libs.ValueKind == JsonValueKind.Array)
        {
            foreach (var lib in libs.EnumerateArray())
            {
                // IsUsable 而不是 IsAllowed：后者只看 rules，分不出
                // natives-windows 与 natives-windows-arm64（两者 rules 相同），
                // 会把异架构的 jar 也挂进 classpath。
                if (!InstallService.IsUsable(lib)) continue;
                if (!lib.TryGetProperty("downloads", out var ld) || ld.ValueKind != JsonValueKind.Object) continue;
                if (!ld.TryGetProperty("artifact", out var art) || art.ValueKind != JsonValueKind.Object) continue;
                if (!art.TryGetProperty("path", out var p) || p.ValueKind != JsonValueKind.String) continue;

                var full = Path.Combine(libRoot, p.GetString()!);
                if (File.Exists(full) && seen.Add(full)) entries.Add(full);
            }
        }

        // 客户端主 jar 永远排在最后
        var jar = Path.Combine(InstanceService.VersionDir(inst.Path), launchVersionId, $"{launchVersionId}.jar");
        if (File.Exists(jar)) entries.Add(jar);

        // 老版本用空格分隔，1.6 之后统一用分号（Windows）
        var cp = string.Join(';', entries);
        var natives = Path.Combine(InstanceService.VersionDir(inst.Path), launchVersionId, "natives");
        return (cp, natives);
    }

    private static Dictionary<string, string> BuildVariables(
        InstanceInfo inst, AccountService.LaunchSession account, string versionId,
        string classPath, string nativeDir, string assetsIndex)
    {
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["${auth_player_name}"] = account.Name,
            ["${version_name}"] = versionId,
            ["${game_directory}"] = inst.Path,
            ["${assets_root}"] = Path.Combine(inst.Path, "assets"),
            ["${assets_index_name}"] = assetsIndex,
            ["${auth_uuid}"] = account.Uuid.Replace("-", ""),
            ["${auth_access_token}"] = account.AccessToken,
            ["${auth_session}"] = account.AccessToken,
            ["${user_type}"] = account.UserType,
            ["${version_type}"] = "release",
            ["${natives_directory}"] = nativeDir,
            ["${launcher_name}"] = "StarCore",
            ["${launcher_version}"] = StarCorePaths.ProductVersion,
            ["${classpath}"] = classPath,
            ["${classpath_separator}"] = ";",
            ["${library_directory}"] = InstanceService.LibraryDir(inst.Path),
            ["${clientid}"] = account.ClientId,
            ["${auth_xuid}"] = account.Xuid,
            ["${resolution_width}"] = inst.WindowWidth.ToString(),
            ["${resolution_height}"] = inst.WindowHeight.ToString(),
            ["${user_properties}"] = "{}",
            ["${game_assets}"] = Path.Combine(inst.Path, "assets", "virtual", "legacy"),
        };
    }

    /// <summary>按规则筛选并替换占位符，产出一段参数列表。</summary>
    private static List<string> CollectArguments(JsonElement version, string kind, Dictionary<string, string> vars)
    {
        var result = new List<string>();
        if (!version.TryGetProperty("arguments", out var args) || args.ValueKind != JsonValueKind.Object) return result;
        if (!args.TryGetProperty(kind, out var arr) || arr.ValueKind != JsonValueKind.Array) return result;

        foreach (var item in arr.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                result.Add(Substitute(item.GetString()!, vars));
                continue;
            }

            if (item.ValueKind != JsonValueKind.Object) continue;

            // 带 rules 的条件参数
            if (item.TryGetProperty("rules", out var rules) && rules.ValueKind == JsonValueKind.Array)
            {
                if (!EvalRules(rules)) continue;
            }

            if (!item.TryGetProperty("value", out var val)) continue;

            if (val.ValueKind == JsonValueKind.String)
            {
                result.Add(Substitute(val.GetString()!, vars));
            }
            else if (val.ValueKind == JsonValueKind.Array)
            {
                foreach (var v in val.EnumerateArray())
                {
                    if (v.ValueKind == JsonValueKind.String) result.Add(Substitute(v.GetString()!, vars));
                }
            }
        }

        return result;
    }

    private static bool EvalRules(JsonElement rules)
    {
        var allowed = false;
        foreach (var rule in rules.EnumerateArray())
        {
            var action = rule.TryGetProperty("action", out var a) ? a.GetString() : "allow";
            if (!RuleMatches(rule)) continue;
            allowed = action == "allow";
        }
        return allowed;
    }

    private static bool RuleMatches(JsonElement rule)
    {
        if (rule.TryGetProperty("os", out var os) && os.ValueKind == JsonValueKind.Object)
        {
            if (os.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String &&
                !string.Equals(n.GetString(), "windows", StringComparison.OrdinalIgnoreCase))
                return false;

            if (os.TryGetProperty("arch", out var arch) && arch.ValueKind == JsonValueKind.String)
            {
                var want = arch.GetString() ?? "";
                if (want == "x86" && Environment.Is64BitOperatingSystem) return false;
            }
        }

        if (rule.TryGetProperty("features", out var feat) && feat.ValueKind == JsonValueKind.Object)
        {
            // demo / custom_resolution / is_demo_user 等特性我们一律视为关闭
            foreach (var f in feat.EnumerateObject())
            {
                if (f.Name is "is_demo_user") { if (f.Value.ValueKind == JsonValueKind.True) return false; }
                else return false;
            }
        }

        return true;
    }

    private static string Substitute(string template, Dictionary<string, string> vars)
    {
        if (!template.Contains("${", StringComparison.Ordinal)) return template;
        var sb = new StringBuilder(template);
        foreach (var (k, v) in vars) sb.Replace(k, v);
        return sb.ToString();
    }

    /// <summary>把命令行字符串切成参数列表，正确处理引号。</summary>
    public static List<string> SplitArgs(string line)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(line)) return result;

        var sb = new StringBuilder();
        var quoted = false;

        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '"') { quoted = !quoted; continue; }
            if (char.IsWhiteSpace(c) && !quoted)
            {
                if (sb.Length > 0) { result.Add(sb.ToString()); sb.Clear(); }
                continue;
            }
            sb.Append(c);
        }
        if (sb.Length > 0) result.Add(sb.ToString());
        return result;
    }

    private static async Task RunHookAsync(string? command, string workDir, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(command)) return;
        try
        {
            var psi = new ProcessStartInfo("cmd.exe", $"/c {command}")
            {
                WorkingDirectory = workDir,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p is not null) await p.WaitForExitAsync(ct);
            Logger.Info($"[launch] 已执行钩子命令：{command}");
        }
        catch (Exception ex)
        {
            Logger.Warn($"[launch] 钩子命令执行失败（已忽略）：{ex.Message}");
        }
    }
}
