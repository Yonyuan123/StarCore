/*
   StarCore 无界面诊断工具
   ------------------------------------------------------------------
   用法：
     dotnet run --project _tools/diag                 # 体检全部实例
     dotnet run --project _tools/diag -- --id 9       # 只看某个实例
     dotnet run --project _tools/diag -- --java       # 只列 Java 运行时

   它做的事和启动器点「启动」之前做的事完全一致（走同一个 BuildPlanAsync），
   所以这里打印出来的命令行，就是真正会被执行的那一条。
*/

using StarCore.Core.App;
using StarCore.Core.Platform;
using StarCore.Core.Services;

var args2 = args.ToArray();
var onlyId = 0L;
var javaOnly = false;
var repair = false;
var launch = false;
var chunkTest = false;
var msaProbe = false;
var launchSeconds = 45;
var chunkParallelism = 8;
string? testUrl = null;

for (var i = 0; i < args2.Length; i++)
{
    if ((args2[i] is "--id" or "-i") && i + 1 < args2.Length && long.TryParse(args2[i + 1], out var v)) onlyId = v;
    if (args2[i] is "--java") javaOnly = true;
    if (args2[i] is "--repair") repair = true;
    if (args2[i] is "--launch") launch = true;
    if (args2[i] is "--chunk-test") chunkTest = true;
    if (args2[i] is "--msa") msaProbe = true;
    if (args2[i] is "--seconds" && i + 1 < args2.Length && int.TryParse(args2[i + 1], out var sec)) launchSeconds = sec;
    if (args2[i] is "--parallel" && i + 1 < args2.Length && int.TryParse(args2[i + 1], out var par)) chunkParallelism = par;
    if (args2[i] is "--url" && i + 1 < args2.Length) testUrl = args2[i + 1];
}

Console.OutputEncoding = System.Text.Encoding.UTF8;

Section("初始化内核");
StarCorePaths.Initialize();
Logger.Initialize("diag");
SettingsService.Load();
InstanceService.Initialize();
AccountService.Initialize();
VersionManifestService.LoadCache();

Info($"数据目录   {StarCorePaths.DataRoot}");
Info($"便携模式   {StarCorePaths.IsPortable}");
Info($"平台实现   {PlatformServices.Current.Name}（{PlatformServices.Current.GetType().Name}）");
Info($"数据源     {DownloadSource.CurrentName}");
Info($"内存上限   {SettingsService.Current.MaxThreads} 线程");
Info($"内置应用身份 {Mask(StarCore.Core.Services.MicrosoftAuth.DefaultClientId)}（混淆存放，解码后回验）");

if (javaOnly || onlyId == 0 && args2.Length == 0)
{
    Section("Java 运行时");
    var javas = JavaService.Scan();
    if (javas.Count == 0) Warn("一个都没找到");
    foreach (var j in javas)
        Info($"[{j.Major,3}] {j.Vendor,-22} {j.Version,-14} {j.Path}");

    // 直接验证版本号推断表，这是"给版本配错 Java"最容易出错的地方
    Section("Java 版本要求推断");
    foreach (var id in new[] { "1.8.9", "1.12.2", "1.16.5", "1.17.1", "1.20.1", "1.20.4", "1.20.5", "1.21.4", "26.3", "26.4-snapshot-3", "24w14a" })
        Info($"{id,-18} → Java {JavaService.RequiredMajor(id)}");
}

if (msaProbe)
{
    Section("微软登录链路自检");
    var probe = await StarCore.Core.Services.MicrosoftAuth.ProbeAsync();
    Verdict("个人账户端点可用（Minecraft 必须走这条）", probe.ConsumerOk);
    Verdict("组织账户端点可用（只作对照）", probe.OrganizationOk);
    Verdict($"Mojang 已放行这个应用（{probe.ClientIdMasked}）", probe.GameApiOk);
    Console.WriteLine();
    Console.WriteLine("  结论：" + (probe.Message ?? "(无)"));
    Console.WriteLine();

    // 回归检查：设备码只能用一次。换票失败后如果又把同一个码拿去问，
    // 用户会看到 AADSTS70000「already been used」，把真正的失败原因盖掉。
    // 这里用一个假码验证"第二次轮询回放结论、不打扰微软"这条保证。
    Section("设备码结算回放（回归检查）");
    const string fake = "diag-fake-device-code";
    var first = await StarCore.Core.Services.MicrosoftAuth.PollAsync(fake);
    var second = await StarCore.Core.Services.MicrosoftAuth.PollAsync(fake);
    Console.WriteLine($"  第一次：status={first.Status}  message={first.Message}");
    Console.WriteLine($"  第二次：status={second.Status}  message={second.Message}");
    Verdict("第一次给出终局结论", first.Status == "error");
    Verdict("第二次原样回放（不再打微软）", second.Status == first.Status && second.Message == first.Message);
    Verdict("没有把 AADSTS70000 甩给用户", first.Message?.Contains("AADSTS70000") != true);
    Console.WriteLine();
    return;
}

if (javaOnly) return;

// --chunk-test：单独把分块下载跑一遍。
// 分块下载是安装链路里最容易出竞态的一环（多线程写分片、合并、清理），
// 但它埋在"装一个版本"里，出问题只能看到一句含糊的警告。这里把它拎出来单测。
if (chunkTest)
{
    Section("分块下载单测");
    var dir = Path.Combine(Path.GetTempPath(), $"starcore-chunk-{Guid.NewGuid():N}");
    Directory.CreateDirectory(dir);
    Info($"临时目录 {dir}");

    // 用一个真实存在、且明显大于阈值的大文件：1.21.1 的客户端 jar（约 25 MB）
    // 默认打镜像站；用 --url 可以换成官方源做对照 ——
    // "分块比单流快"这个结论必须在健康源上验证，镜像站被限流时两边一样慢。
    const string mirrorUrl = "https://bmclapi2.bangbang93.com/version/1.21.1/client";
    var url = testUrl ?? mirrorUrl;
    var dest = Path.Combine(dir, "client.jar");
    Info($"源 {url}" + (testUrl is null ? "（镜像，--url 可换）" : ""));

    long got = 0;
    var sw = System.Diagnostics.Stopwatch.StartNew();
    try
    {
        got = await Http.DownloadAsync(url, dest, CancellationToken.None, null, chunkParallelism, _ => { });
        sw.Stop();
    }
    catch (Exception ex)
    {
        Warn($"下载抛异常：{ex.GetType().Name}: {ex.Message}");
    }

    var actual = File.Exists(dest) ? new FileInfo(dest).Length : 0;
    Verdict("文件已生成", actual > 0);
    Verdict("大小与返回值一致", actual == got && got > 0);
    Console.WriteLine($"     返回 {got / 1024 / 1024.0:F2} MB，实际 {actual / 1024 / 1024.0:F2} MB，用时 {sw.ElapsedMilliseconds} ms");

    var leftovers = Directory.EnumerateFiles(dir).Where(f => !f.EndsWith("client.jar", StringComparison.OrdinalIgnoreCase)).ToList();
    Verdict("没有残留分片", leftovers.Count == 0);
    if (leftovers.Count > 0) Warn("残留：" + string.Join(", ", leftovers.Select(Path.GetFileName)));

    try { Directory.Delete(dir, true); } catch { }
    Console.WriteLine();
}

Section("热点方法耗时");
{
    // instance.list 是前端每次进页面、每次 instances.changed 都会调的方法，
    // 它内部的目录体积统计要给几万个文件各做一次 stat。这里量冷/热两次，
    // 直接看缓存有没有生效。
    var sw = System.Diagnostics.Stopwatch.StartNew();
    var first = InstanceService.List().Count;
    var cold = sw.ElapsedMilliseconds;

    sw.Restart();
    InstanceService.List();
    var warm = sw.ElapsedMilliseconds;

    Info($"instance.list  冷 {cold} ms → 热 {warm} ms（{first} 个实例）");
    Info($"               热路径应接近 0 —— 体积统计命中 TTL 缓存后不再遍历目录");

    sw.Restart();
    var javas = JavaService.Scan();
    Info($"java.scan      {sw.ElapsedMilliseconds} ms（{javas.Count} 个运行时）");
}

// --launch：真的把游戏起起来。这是唯一能证明"命令行 + natives + Java 都对"的方法 ——
// 只要它不再报「找不到或无法加载主类」，就说明前面的推导链路是通的。
if (launch)
{
    Section("真实启动");
    var inst = InstanceService.Get(onlyId > 0 ? onlyId : InstanceService.ActiveId ?? 0);
    if (inst is null) { Warn("没有找到要启动的实例"); return; }

    Info($"启动实例 #{inst.Id}「{inst.Name}」，最多观察 {launchSeconds} 秒…");

    LaunchService.GameOutput += line => Console.WriteLine("  [游戏] " + line);

    var pid = 0;
    try
    {
        pid = await LaunchService.StartAsync(inst.Id, CancellationToken.None);
        Info($"已启动，PID {pid}");
    }
    catch (Exception ex)
    {
        Warn($"启动失败：{ex.Message}");
        return;
    }

    var t0 = DateTime.UtcNow;
    while ((DateTime.UtcNow - t0).TotalSeconds < launchSeconds && LaunchService.IsRunning(inst.Id))
        await Task.Delay(500);

    if (LaunchService.IsRunning(inst.Id))
    {
        Info($"观察窗口结束，游戏进程仍在运行（PID {pid}）—— 这通常意味着已经过了类加载阶段。");
        LaunchService.Kill(inst.Id);
    }
    else
    {
        Warn("游戏进程已退出，请在上面找退出原因。");
    }
    return;
}

Section("实例体检");
var instances = InstanceService.List();
Info($"共 {instances.Count} 个实例");

var targets = onlyId > 0 ? instances.Where(x => x.Id == onlyId).ToList() : instances;
if (targets.Count == 0) { Warn($"没有找到实例 #{onlyId}"); return; }

// --repair：对目标实例重跑一次安装。已下好的文件会被跳过，只补缺的部分 +
// 重新解压原生库，所以用它验证"修复"路径最合适。
if (repair)
{
    Section("修复安装");
    foreach (var inst in targets)
    {
        if (string.IsNullOrWhiteSpace(inst.VersionId)) { Warn($"实例 #{inst.Id} 没有版本，跳过"); continue; }

        var taskId = $"diag-repair-{inst.Id}";
        Info($"开始修复实例 #{inst.Id}「{inst.Name}」（{inst.VersionId} / {inst.Loader}）…");

        var lastLine = "";
        TaskCenter.Updated += t =>
        {
            if (t.Id != taskId) return;
            var line = $"{t.State,-8} {t.DoneFiles}/{t.TotalFiles} {t.Progress:F0}%  {t.Detail}";
            if (line == lastLine) return;
            lastLine = line;
            Console.WriteLine("     " + line);
        };

        await InstallService.InstallAsync(
            inst.Id, inst.VersionId, inst.Loader, inst.LoaderVersion, taskId, CancellationToken.None);
    }
}

var okCount = 0;
foreach (var inst in targets)
{
    Console.WriteLine();
    Info($"──── 实例 #{inst.Id}「{inst.Name}」版本={inst.VersionId ?? "(未配置)"} 加载器={inst.Loader ?? "-"}");
    Info($"     目录 {inst.Path}");
    Info($"     已安装={inst.Installed} 模组={inst.ModCount} 体积={Fmt(inst.SizeBytes)}");

    if (string.IsNullOrWhiteSpace(inst.VersionId))
    {
        Warn("     跳过：该实例还没有游戏版本");
        continue;
    }

    try
    {
        var plan = await LaunchService.InspectAsync(inst.Id);
        okCount++;

        Info($"     Java        {plan.Java.Path}");
        Info($"     Java 主版本  {plan.Java.Major}（版本 JSON 声明 {plan.DeclaredJavaMajor}）");
        Info($"     启动版本 id  {plan.LaunchVersionId}");
        Info($"     主类        {plan.MainClass}");

        var cpCount = plan.ClassPath.Length == 0 ? 0 : plan.ClassPath.Split(';').Length;
        Info($"     classpath   {cpCount} 项");

        // 这三条就是"启动不了"最常见的三个原因，直接在这里判死
        var hasCp = plan.JvmArgs.Contains("-cp") || plan.JvmArgs.Any(a => a.StartsWith("-cp"));
        var hasLibPath = plan.JvmArgs.Any(a => a.StartsWith("-Djava.library.path="));
        var cpHasValue = false;
        for (var i = 0; i < plan.JvmArgs.Count; i++)
            if (plan.JvmArgs[i] is "-cp" or "-classpath" && i + 1 < plan.JvmArgs.Count && plan.JvmArgs[i + 1].Length > 0)
                cpHasValue = true;

        Verdict("命令行含 -cp", hasCp && cpHasValue);
        Verdict("命令行含 -Djava.library.path", hasLibPath);
        Verdict("classpath 非空", cpCount > 0);

        Console.WriteLine();
        Console.WriteLine("  完整命令行：");
        Console.WriteLine("  " + plan.CommandLine);
    }
    catch (Exception ex)
    {
        Warn($"     无法启动：{ex.Message}");
    }
}

Section("汇总");
Info($"可启动 {okCount} / {targets.Count}");
Console.WriteLine();

// ---- 小工具 ----

void Section(string t)
{
    Console.WriteLine();
    Console.WriteLine("════════════════════════════════════════════════════════════");
    Console.WriteLine($"  {t}");
    Console.WriteLine("════════════════════════════════════════════════════════════");
}

void Info(string t) => Console.WriteLine("  " + t);
void Warn(string t) => Console.WriteLine("  ⚠ " + t);

void Verdict(string label, bool ok)
{
    Console.WriteLine($"     {(ok ? "✓" : "✗")} {label}");
}

// 只露前 8 位与后 4 位，既能核对又不用把整串打出来
static string Mask(string s)
    => s.Length <= 12 ? "(长度异常)" : $"{s[..8]}…{s[^4..]}（长度 {s.Length}）";

static string Fmt(long bytes)
{
    string[] u = { "B", "KB", "MB", "GB", "TB" };
    double v = bytes;
    var i = 0;
    while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
    return $"{v:F1} {u[i]}";
}
