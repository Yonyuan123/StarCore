using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using StarCore.Core.App;
using StarCore.Core.Models;
using StarCore.Core.Platform;

namespace StarCore.Core.Services;

/// <summary>
/// 版本安装：拉取版本描述 → 下载核心 jar、库、资源索引 → 解压原生库。
///
/// 三种加载器的接入方式不同，这里按"元数据来源"分类：
///   * 原版         —— 直接用 Mojang 的版本 JSON
///   * Fabric/Quilt —— 从各自 meta 服务拿到一个 inheritsFrom 的版本 JSON，再补原版
///   * Forge/NeoForge/OptiFine —— 下载官方安装器并执行（这是它们唯一受支持的安装方式）
///
/// 全部下载都走 <see cref="Http.DownloadAsync"/>，它天然支持"已存在且大小一致就跳过"，
/// 因此中断后重新点击安装只会补齐缺失的文件。
/// </summary>
public static class InstallService
{
    public static async Task InstallAsync(
        long instanceId, string versionId, string? loader, string? loaderVersion,
        string taskId, CancellationToken ct)
    {
        var inst = InstanceService.Get(instanceId)
                   ?? throw new InvalidOperationException("实例不存在。");

        try
        {
            // 登记归属：删实例时要靠它把这个任务停掉，
            // 否则下载线程手里的句柄会让目录删不干净。
            TaskCenter.SetOwner(taskId, instanceId);
            TaskCenter.Start(taskId, "准备中…");
            EnsureBaseDirs(inst.Path);

            loader ??= "vanilla";

            // ---- Fabric / Quilt：先落地它们的版本 JSON ----
            if (loader is "fabric" or "quilt" or "legacyfabric")
            {
                var loaderJson = await FetchModdedProfileAsync(loader, versionId, loaderVersion, ct);
                var loaderId = loaderJson.GetProperty("id").GetString()!;
                var dir = Path.Combine(InstanceService.VersionDir(inst.Path), loaderId);
                Directory.CreateDirectory(dir);
                await File.WriteAllTextAsync(Path.Combine(dir, $"{loaderId}.json"), loaderJson.GetRawText(), ct);

                TaskCenter.Report(taskId, t => t.Detail = $"已获取 {loader} {loaderId} 的版本描述");
                await InstallVersionTreeAsync(inst.Path, loaderId, taskId, ct);
                InstanceService.Update(instanceId, new Dictionary<string, object?>
                {
                    ["loaderVersion"] = loaderId,
                });
            }
            else if (loader is "forge" or "neoforge" or "optifine")
            {
                await RunInstallerLoaderAsync(inst, versionId, loader, loaderVersion, taskId, ct);
            }
            else if (loader is "liteloader")
            {
                // 明确拒绝，而不是让用户等一个永远不会完成的安装。
                // LiteLoader 官方已停止维护多年，也没有稳定的公开安装器直链。
                throw new InvalidOperationException(
                    "LiteLoader 已多年未更新，且没有可用的自动安装方式。请改用 Fabric 或 NeoForge。");
            }
            else
            {
                await InstallVersionTreeAsync(inst.Path, versionId, taskId, ct);
            }

            // 安装成功后才把版本与加载器钉到实例上。
            // 顺序很重要：先装完再写，中途失败时实例仍是"未配置"而不是"配置了一个装了一半的版本"，
            // 用户重新安装时不会被半成品状态误导。
            InstanceService.Update(instanceId, new Dictionary<string, object?>
            {
                ["versionId"] = versionId,
                ["loader"] = loader,
                ["loaderVersion"] = loaderVersion,
            });

            TaskCenter.Complete(taskId, "安装完成");
            Logger.Info($"[install] 实例 #{instanceId} 安装完成：{versionId} / {loader}");

            // 装完之后体积变了（而且刚解压了原生库），清掉缓存让列表显示新值
            InstanceService.InvalidateStats(instanceId);

            // 通知界面：实例元数据变了（名称/图标/版本都要跟着刷新）
            StarCoreHost.Emit("instances.changed", null);
            StarCoreHost.Emit("install.done", new
            {
                instanceId,
                versionId,
                loader,
                loaderVersion,
                taskId,
            });
        }
        catch (OperationCanceledException)
        {
            TaskCenter.Cancel(taskId);
            Logger.Info($"[install] 安装已取消：实例 #{instanceId}");
        }
        catch (Exception ex)
        {
            TaskCenter.Fail(taskId, ex.Message);
            Logger.Error(ex, $"[install] 安装失败：实例 #{instanceId}");

            // 失败也要通知界面，否则"安装完成后弹窗问是否启动"的流程会永远等不到回应
            StarCoreHost.Emit("install.failed", new { instanceId, versionId, loader, taskId, message = ex.Message });
        }
    }

    /* ==================================================================
       进度上报

       为什么要单独一个类：分块下载每写 80KB 就会回调一次，一个 25MB 的文件
       会产生三百多次回调。每次都去推事件的话，界面光处理进度就忙不过来，
       反而更卡。这里攒着，每 250ms 才真正推一次 —— 人眼分辨不出更细的差别，
       但日志与渲染开销能降两个数量级。
       ================================================================== */

    private sealed class ProgressPump
    {
        private readonly string _taskId;
        private readonly int _total;
        private readonly long _totalBytes;

        private long _bytes;
        private int _done;
        private long _lastPush;

        public ProgressPump(string taskId, int total, long totalBytes)
        {
            _taskId = taskId;
            _total = total;
            _totalBytes = totalBytes;
        }

        /// <summary>写入了一段数据（分块与单流都会调）。</summary>
        public void Add(long delta)
        {
            Interlocked.Add(ref _bytes, delta);
            Push(false, null);
        }

        /// <summary>一个文件完成。</summary>
        public void FileDone(string label)
        {
            Interlocked.Increment(ref _done);
            Push(true, label);
        }

        private void Push(bool force, string? label)
        {
            var now = Environment.TickCount64;
            if (!force && now - _lastPush < 250) return;
            _lastPush = now;

            TaskCenter.Progress(
                _taskId,
                Volatile.Read(ref _done),
                _total,
                Volatile.Read(ref _bytes),
                _totalBytes,
                label);
        }
    }

    /// <summary>
    /// 决定单个文件内部分几块。
    ///
    /// 大文件很少时（例如只剩 client.jar 没下）才把预算都给它，
    /// 这样单文件下载也能吃满带宽，而不是"32 个线程里 31 个空转、剩下那个跑单流"。
    ///
    /// 为什么上限是 8：实测（`_tools/diag --chunk-test`，官方源、25.59MB 的 client.jar）
    ///   8 块并行 → 26.4s（约 0.97 MB/s）
    ///   单流     → 108.4s（约 0.24 MB/s）
    /// 即 4.1 倍。注意这是**每文件**的上限，不是全网预算：
    /// 真实最坏情况是 28 个小文件 + 4 个大文件 × 8 块 ≈ 60 条连接，
    /// 刚好被 <see cref="Http"/> 的 MaxConnectionsPerServer = 64 兜住。
    /// 如果哪天把 MaxConnectionsPerServer 调低，这里也要跟着收敛，否则
    /// 超出的请求会在连接池里排队，看起来就像"下载卡住了"。
    /// </summary>
    private static int ChunkParallelism(int fileParallel, int bigFileCount)
    {
        if (bigFileCount <= 0) return 1;
        if (bigFileCount >= fileParallel) return 1;   // 大文件自己就够占满预算了
        return Math.Clamp(fileParallel / bigFileCount, 2, 8);
    }

    private static void EnsureBaseDirs(string instancePath)
    {
        foreach (var d in new[] { "versions", "libraries", "assets", "assets/indexes", "assets/objects", "mods", "logs", "config" })
            Directory.CreateDirectory(Path.Combine(instancePath, d));
    }

    /* ==================================================================
       通用安装流程：解析 inheritsFrom 链，下载全部依赖
       ================================================================== */

    /// <summary>把版本 JSON 的继承链展开成一份"合并后"的描述。</summary>
    public static async Task<JsonElement> ResolveVersionAsync(string instancePath, string versionId, CancellationToken ct)
    {
        var chain = new List<JsonElement>();
        var current = versionId;
        var guard = 0;

        while (!string.IsNullOrEmpty(current) && guard++ < 8)
        {
            var json = await LoadVersionJsonAsync(instancePath, current, ct)
                       ?? throw new InvalidOperationException($"找不到版本描述：{current}");
            chain.Add(json);

            current = json.TryGetProperty("inheritsFrom", out var p) && p.ValueKind == JsonValueKind.String
                ? p.GetString() ?? ""
                : "";
        }

        if (chain.Count == 1) return chain[0];
        return MergeChain(chain);
    }

    private static async Task<JsonElement?> LoadVersionJsonAsync(string instancePath, string id, CancellationToken ct)
    {
        // 本地优先
        var local = Path.Combine(InstanceService.VersionDir(instancePath), id, $"{id}.json");
        if (File.Exists(local)) return Json.DeserializeElement(await File.ReadAllTextAsync(local, ct));

        // 回落到官方
        try
        {
            var detail = await VersionManifestService.FetchDetailAsync(id, ct);
            if (detail is null) return null;
            var dir = Path.Combine(InstanceService.VersionDir(instancePath), id);
            Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(Path.Combine(dir, $"{id}.json"), detail.Value.GetRawText(), ct);
            return detail;
        }
        catch (Exception ex)
        {
            Logger.Warn($"[install] 拉取版本描述失败 {id}：{ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 合并继承链。子版本（索引小的）优先：id / mainClass / 各种参数取自子，
    /// libraries 取并集（子在前），这样后装库不会覆盖先装的。
    /// </summary>
    private static JsonElement MergeChain(List<JsonElement> chain)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();

            // 标量：从子到父，先出现的胜出
            foreach (var key in new[] { "id", "mainClass", "assets", "assetIndex", "type", "releaseTime", "time", "minimumLauncherVersion", "downloads", "javaVersion", "logging" })
            {
                foreach (var el in chain)
                {
                    if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty(key, out var v))
                    {
                        w.WritePropertyName(key);
                        v.WriteTo(w);
                        break;
                    }
                }
            }

            // arguments：逐段拼接（game / jvm）
            w.WritePropertyName("arguments");
            w.WriteStartObject();
            foreach (var kind in new[] { "game", "jvm" })
            {
                w.WritePropertyName(kind);
                w.WriteStartArray();
                for (var i = chain.Count - 1; i >= 0; i--)
                {
                    if (chain[i].TryGetProperty("arguments", out var args) &&
                        args.ValueKind == JsonValueKind.Object &&
                        args.TryGetProperty(kind, out var arr) && arr.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var a in arr.EnumerateArray()) a.WriteTo(w);
                    }
                }
                w.WriteEndArray();
            }
            w.WriteEndObject();

            // libraries：并集，按 group:artifact 去重（子优先）
            w.WritePropertyName("libraries");
            w.WriteStartArray();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var el in chain)
            {
                if (!el.TryGetProperty("libraries", out var libs) || libs.ValueKind != JsonValueKind.Array) continue;
                foreach (var lib in libs.EnumerateArray())
                {
                    var key = LibraryKey(lib);
                    if (!seen.Add(key)) continue;
                    // 不属于当前平台的库（尤其是异架构原生库）不该进合并结果，
                    // 否则它们会被下载、也会出现在 classpath 里。
                    if (!IsUsable(lib)) continue;
                    lib.WriteTo(w);
                }
            }
            w.WriteEndArray();

            w.WriteEndObject();
        }

        return Json.DeserializeElement(System.Text.Encoding.UTF8.GetString(ms.ToArray()));
    }

    public static string LibraryKey(JsonElement lib)
    {
        if (lib.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String)
        {
            var name = n.GetString() ?? "";
            if (lib.TryGetProperty("natives", out var nt) && nt.ValueKind == JsonValueKind.Object)
            {
                // native 库按 name + 平台区分，避免不同平台的同名库互相覆盖
                var suffix = string.Concat(nt.EnumerateObject().Select(p => p.Value.GetString()));
                return name + "|" + suffix;
            }
            return name;
        }
        return Guid.NewGuid().ToString();
    }

    /// <summary>下载某个版本的完整文件树。</summary>
    private static async Task InstallVersionTreeAsync(string instancePath, string versionId, string taskId, CancellationToken ct)
    {
        var version = await ResolveVersionAsync(instancePath, versionId, ct);
        var versionDir = Path.Combine(InstanceService.VersionDir(instancePath), versionId);
        Directory.CreateDirectory(versionDir);

        // 1) 收集所有下载任务
        var jobs = new List<(string Url, string Dest, long Size, string Label)>();

        // 客户端主 jar
        if (version.TryGetProperty("downloads", out var dl) &&
            dl.TryGetProperty("client", out var client) &&
            client.TryGetProperty("url", out var curl) && curl.ValueKind == JsonValueKind.String)
        {
            var size = client.TryGetProperty("size", out var sz) && sz.TryGetInt64(out var s) ? s : 0;
            jobs.Add((curl.GetString()!, Path.Combine(versionDir, $"{versionId}.jar"), size, $"{versionId}.jar"));
        }

        // 资源索引
        string? assetIndexId = null;
        if (version.TryGetProperty("assetIndex", out var ai) &&
            ai.TryGetProperty("url", out var aurl) && aurl.ValueKind == JsonValueKind.String)
        {
            assetIndexId = ai.TryGetProperty("id", out var aid) ? aid.GetString() : null;
            var size = ai.TryGetProperty("size", out var asz) && asz.TryGetInt64(out var s2) ? s2 : 0;
            jobs.Add((aurl.GetString()!, Path.Combine(instancePath, "assets", "indexes", $"{assetIndexId}.json"), size, "资源索引"));
        }
        else
        {
            assetIndexId = version.TryGetProperty("assets", out var a) ? a.GetString() : null;
        }

        // 库与原生库
        var nativeJobs = new List<(string JarPath, string ExtractTo)>();
        if (version.TryGetProperty("libraries", out var libs) && libs.ValueKind == JsonValueKind.Array)
        {
            foreach (var lib in libs.EnumerateArray())
            {
                if (!IsUsable(lib)) continue;

                // 新版格式：平台信息在**库名**的分类符里（…:natives-windows）。
                // 这类 jar 不会走 downloads.classifiers，而是当普通 artifact 出现，
                // 需要我们自己认出来并解压。
                if (NativeClassifierOf(lib) is not null)
                {
                    if (TryQueueArtifact(instancePath, lib, jobs, out var nativeJar))
                        nativeJobs.Add((nativeJar, NativeRoot(versionDir)));
                    continue;
                }

                if (lib.TryGetProperty("downloads", out var ld) && ld.ValueKind == JsonValueKind.Object)
                {
                    TryQueueArtifact(instancePath, lib, jobs, out _);

                    // 旧版格式：原生库单独放在 downloads.classifiers 里，由 natives 指定平台
                    var classifier = NativeClassifier(lib);
                    if (classifier is not null &&
                        ld.TryGetProperty("classifiers", out var cls) &&
                        cls.ValueKind == JsonValueKind.Object &&
                        cls.TryGetProperty(classifier, out var c) &&
                        c.ValueKind == JsonValueKind.Object &&
                        c.TryGetProperty("url", out var cu) && cu.ValueKind == JsonValueKind.String)
                    {
                        var cpath = c.TryGetProperty("path", out var cp) ? cp.GetString()! : $"{classifier}.jar";
                        var dest = Path.Combine(InstanceService.LibraryDir(instancePath), cpath);
                        var size = c.TryGetProperty("size", out var csz) && csz.TryGetInt64(out var s4) ? s4 : 0;
                        jobs.Add((cu.GetString()!, dest, size, Path.GetFileName(cpath)));
                        nativeJobs.Add((dest, NativeRoot(versionDir)));
                    }
                }
                else if (lib.TryGetProperty("name", out var rawName) && rawName.ValueKind == JsonValueKind.String &&
                         lib.TryGetProperty("url", out var mvn) && mvn.ValueKind == JsonValueKind.String)
                {
                    // 老版本格式：用 maven 坐标拼路径
                    var mavenPath = MavenToPath(rawName.GetString()!);
                    if (mavenPath is not null)
                        jobs.Add((Http.Combine(mvn.GetString()!, mavenPath), Path.Combine(InstanceService.LibraryDir(instancePath), mavenPath), 0, Path.GetFileName(mavenPath)));
                }
            }
        }

        // 2) 并行下载
        const long bigFileThreshold = 2 * 1024 * 1024;

        TaskCenter.Report(taskId, t => { t.TotalFiles = jobs.Count; t.Progress = 0; });
        var totalBytes = jobs.Sum(j => j.Size);
        var fileParallel = Math.Clamp(SettingsService.Current.MaxThreads, 1, 64);
        var chunkParallel = ChunkParallelism(fileParallel, jobs.Count(j => j.Size >= bigFileThreshold));
        var pump = new ProgressPump(taskId, jobs.Count, totalBytes);

        Logger.Debug($"[install] 下载 {jobs.Count} 个文件，文件级并发 {fileParallel}，单文件分块 {chunkParallel}");

        /* ✦ 失败必须收集起来，绝不能再"吞掉异常然后报安装完成"。
           上一版这里只 Logger.Warn 一句就继续走，于是：
             文件没下下来 → 安装仍然报成功 → 实例被打上 versionId
             → 用户点启动 → 命令行里连主 jar 都没有 → Java 报「找不到主类」退出码 1。
           用户看到的是"装好了却启动不了"，而日志里翻不到任何致命错误。
           宁可安装明确失败、让用户重试，也不要留下一个假装装好的实例。 */
        var failed = new System.Collections.Concurrent.ConcurrentBag<(string Label, string Message)>();

        await Parallel.ForEachAsync(
            jobs,
            new ParallelOptions { MaxDegreeOfParallelism = fileParallel, CancellationToken = ct },
            async (job, token) =>
            {
                try
                {
                    await DownloadSource.DownloadAsync(
                        job.Url, job.Dest, token,
                        job.Size > 0 ? job.Size : null,
                        chunkParallel,
                        pump.Add);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Logger.Warn($"[install] 下载失败 {job.Label}：{ex.Message}");
                    failed.Add((job.Label, ex.Message));
                }

                pump.FileDone(job.Label);
            });

        if (!failed.IsEmpty)
        {
            // 只报前几条，够定位问题又不至于把日志刷爆
            var sample = string.Join("、", failed.Take(3).Select(f => f.Label));
            throw new InvalidOperationException(
                $"有 {failed.Count} 个文件下载失败（例如：{sample}）。"
                + "已下好的文件会被保留，请检查网络或切换下载源后重新安装。");
        }

        // 3) 解压原生库
        TaskCenter.Report(taskId, t => t.Detail = "正在解压原生库…");
        foreach (var (jar, to) in nativeJobs) ExtractNatives(jar, to);

        // 4) 资源文件
        if (!string.IsNullOrEmpty(assetIndexId))
        {
            var indexFile = Path.Combine(instancePath, "assets", "indexes", $"{assetIndexId}.json");
            if (File.Exists(indexFile))
            {
                await DownloadAssetsAsync(instancePath, indexFile, taskId, ct);
            }
        }

        TaskCenter.Report(taskId, t => t.Detail = "安装完成");
    }

    private static async Task DownloadAssetsAsync(string instancePath, string indexFile, string taskId, CancellationToken ct)
    {
        JsonElement objects;
        try
        {
            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(indexFile, ct));
            if (!doc.RootElement.TryGetProperty("objects", out var o)) return;
            objects = o.Clone();
        }
        catch (Exception ex)
        {
            Logger.Warn($"[install] 资源索引解析失败：{ex.Message}");
            return;
        }

        // 资源根地址固定用官方 URL，由 DownloadSource 决定实际走镜像还是直连；
        // 它同时负责"镜像上没有这个文件时回落官方"，比手写 if 更可靠。
        const string baseUrl = "https://resources.download.minecraft.net";

        var jobs = new List<(string Url, string Dest, long Size)>();
        foreach (var p in objects.EnumerateObject())
        {
            var hash = p.Value.TryGetProperty("hash", out var hv) ? hv.GetString() : null;
            var size = p.Value.TryGetProperty("size", out var sv) && sv.TryGetInt64(out var s) ? s : 0;
            if (string.IsNullOrEmpty(hash) || hash.Length < 2) continue;

            var sub = hash[..2];
            var dest = Path.Combine(instancePath, "assets", "objects", sub, hash);
            jobs.Add((Http.Combine(baseUrl, $"{sub}/{hash}"), dest, size));
        }

        if (jobs.Count == 0) return;

        var done = 0;
        var assetFailures = 0;
        TaskCenter.Report(taskId, t => { t.TotalFiles = jobs.Count + t.DoneFiles; t.Detail = $"正在下载 {jobs.Count} 个资源文件…"; });

        await Parallel.ForEachAsync(
            jobs,
            new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(SettingsService.Current.MaxThreads, 1, 64), CancellationToken = ct },
            async (job, token) =>
            {
                try
                {
                    // 资源文件绝大多数很小，不分块（传 1），避免为一堆 4KB 的贴图
                    // 各发一次 Range 探测请求 —— 那才是真正的浪费。
                    await DownloadSource.DownloadAsync(job.Url, job.Dest, token, job.Size > 0 ? job.Size : null, 1);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Interlocked.Increment(ref assetFailures);
                    Logger.Debug($"[install] 资源下载失败 {job.Dest}：{ex.Message}");
                }

                var d = Interlocked.Increment(ref done);
                if (d % 64 == 0) TaskCenter.Report(taskId, t => t.Detail = $"资源文件 {d}/{jobs.Count}");
            });

        // 资源文件有几千个，个别失败很常见（CDN 抖动），不该因此让整个安装失败；
        // 但成批失败说明链路有问题，那必须让用户知道。
        var failureRate = assetFailures / (double)jobs.Count;
        if (failureRate > 0.05)
        {
            throw new InvalidOperationException(
                $"资源文件下载失败过多（{assetFailures}/{jobs.Count}）。请检查网络或切换下载源后重新安装。");
        }
        if (assetFailures > 0)
        {
            Logger.Warn($"[install] 有 {assetFailures} 个资源文件未下成功（占比 {failureRate:P1}），游戏内可能出现缺音效/缺贴图");
        }
    }

    /* ==================================================================
       加载器：Fabric / Quilt
       ================================================================== */

    private static async Task<JsonElement> FetchModdedProfileAsync(string loader, string gameVersion, string? loaderVersion, CancellationToken ct)
    {
        // Fabric 与 Legacy Fabric 的 meta 结构完全相同，只是域名不同。
        // 后者用于 1.13 及更早的旧版本。
        if (loader is "fabric" or "legacyfabric")
        {
            var host = loader == "fabric" ? "https://meta.fabricmc.net" : "https://meta.legacyfabric.net";
            var display = loader == "fabric" ? "Fabric" : "Legacy Fabric";

            loaderVersion ??= (await PickFabricLoaderAsync(loader, gameVersion, ct))
                ?? throw new InvalidOperationException($"{display} 目前没有支持 {gameVersion} 的版本。");

            var url = $"{host}/v2/versions/loader/{gameVersion}/{loaderVersion}/profile/json";
            return Json.DeserializeElement(await DownloadSource.GetStringAsync(url, ct));
        }

        // Quilt 的 loader 版本号带前缀，需要先查列表
        if (string.IsNullOrWhiteSpace(loaderVersion))
        {
            var list = Json.DeserializeElement(await DownloadSource.GetStringAsync("https://meta.quiltmc.org/v3/versions/loader", ct));
            if (list.ValueKind == JsonValueKind.Array && list.GetArrayLength() > 0)
                loaderVersion = list[0].TryGetProperty("version", out var v) ? v.GetString() : null;
        }
        if (string.IsNullOrWhiteSpace(loaderVersion))
            throw new InvalidOperationException("无法确定 Quilt 加载器版本。");

        var qurl = $"https://meta.quiltmc.org/v3/versions/loader/{gameVersion}/{loaderVersion}/profile/json";
        return Json.DeserializeElement(await DownloadSource.GetStringAsync(qurl, ct));
    }

    private static async Task<string?> PickFabricLoaderAsync(string gameVersion, CancellationToken ct)
        => await PickFabricLoaderAsync("fabric", gameVersion, ct);

    private static async Task<string?> PickFabricLoaderAsync(string loader, string gameVersion, CancellationToken ct)
    {
        try
        {
            var host = loader == "legacyfabric" ? "https://meta.legacyfabric.net" : "https://meta.fabricmc.net";
            var arr = Json.DeserializeElement(await DownloadSource.GetStringAsync(
                $"{host}/v2/versions/loader/{gameVersion}", ct));
            if (arr.ValueKind != JsonValueKind.Array) return null;
            foreach (var item in arr.EnumerateArray())
            {
                if (item.TryGetProperty("loader", out var l) && l.TryGetProperty("version", out var v))
                    return v.GetString();
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"[install] 查询 Fabric 版本失败：{ex.Message}");
        }
        return null;
    }

    /* ==================================================================
       加载器：Forge / NeoForge / OptiFine —— 执行官方安装器
       ================================================================== */

    private static async Task RunInstallerLoaderAsync(
        InstanceInfo inst, string gameVersion, string loader, string? loaderVersion,
        string taskId, CancellationToken ct)
    {
        var java = JavaService.Pick(gameVersion, inst.JavaPath)
                   ?? throw new InvalidOperationException("安装该加载器需要 Java，但系统里没有可用的 Java。请先在设置中添加。");

        var installerUrl = await ResolveInstallerUrlAsync(gameVersion, loader, loaderVersion, ct);
        var dir = Path.Combine(StarCorePaths.CacheRoot, "installers");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, Path.GetFileName(new Uri(installerUrl).AbsolutePath));

        TaskCenter.Report(taskId, t => t.Detail = $"正在下载 {loader} 安装器…");
        await DownloadSource.DownloadAsync(installerUrl, file, ct);

        // 原版必须先装好，安装器才能在其之上安装加载器
        TaskCenter.Report(taskId, t => t.Detail = "正在安装基础版本…");
        await InstallVersionTreeAsync(inst.Path, gameVersion, taskId, ct);

        TaskCenter.Report(taskId, t => t.Detail = $"正在执行 {loader} 安装器…");
        var args = loader switch
        {
            "neoforge" => new[] { "-jar", file, "--installClient", inst.Path },
            "forge" => new[] { "-jar", file, "--installClient", inst.Path },
            _ => new[] { "-jar", file },
        };

        var psi = new ProcessStartInfo(java.Path)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = inst.Path,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var p = Process.Start(psi) ?? throw new InvalidOperationException("无法启动安装器进程。");
        var output = new System.Text.StringBuilder();
        p.OutputDataReceived += (_, e) => { if (e.Data is not null) { output.AppendLine(e.Data); TaskCenter.Report(taskId, t => t.Detail = e.Data.Length > 90 ? e.Data[..90] : e.Data); } };
        p.ErrorDataReceived += (_, e) => { if (e.Data is not null) output.AppendLine(e.Data); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();

        await p.WaitForExitAsync(ct);
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"安装器退出码 {p.ExitCode}。\n{output.ToString().Split('\n').TakeLast(12).Aggregate((a, b) => a + "\n" + b)}");
    }

    private static async Task<string> ResolveInstallerUrlAsync(string gameVersion, string loader, string? loaderVersion, CancellationToken ct)
    {
        var shortVer = gameVersion.StartsWith("1.") ? gameVersion[2..] : gameVersion;

        switch (loader)
        {
            case "forge":
            {
                if (string.IsNullOrWhiteSpace(loaderVersion))
                {
                    var promo = Json.DeserializeElement(await DownloadSource.GetStringAsync(
                        "https://files.minecraftforge.net/net/minecraftforge/forge/promotions_slim.json", ct));
                    if (promo.TryGetProperty("promos", out var promos) &&
                        promos.TryGetProperty($"{gameVersion}-recommended", out var r))
                        loaderVersion = r.GetString();
                    loaderVersion ??= $"{gameVersion}-latest";
                }
                var full = loaderVersion!.Contains('-') ? loaderVersion : $"{gameVersion}-{loaderVersion}";
                return $"https://maven.minecraftforge.net/net/minecraftforge/forge/{full}/forge-{full}-installer.jar";
            }

            case "neoforge":
            {
                if (string.IsNullOrWhiteSpace(loaderVersion))
                {
                    // 关键：官方 maven.neoforged.net 在部分网络下不可达，
                    // 所以这里直接走 BMCL 的镜像路径拿元数据。
                    var xml = await DownloadSource.GetStringAsync(
                        "https://maven.neoforged.net/releases/net/neoforged/neoforge/maven-metadata.xml", ct);
                    var m = System.Text.RegularExpressions.Regex.Matches(xml, @"<version>(.*?)</version>");
                    var candidates = m.Select(x => x.Groups[1].Value)
                        .Where(v => v.StartsWith(shortVer + "."))
                        .ToList();
                    loaderVersion = candidates.LastOrDefault()
                                    ?? m.Cast<System.Text.RegularExpressions.Match>().LastOrDefault()?.Groups[1].Value
                                    ?? throw new InvalidOperationException("无法确定 NeoForge 版本。");
                }
                return $"https://maven.neoforged.net/releases/net/neoforged/neoforge/{loaderVersion}/neoforge-{loaderVersion}-installer.jar";
            }

            case "optifine":
            {
                // OptiFine 没有公开的稳定直链，必须由用户提供安装器
                throw new InvalidOperationException(
                    "OptiFine 未提供公开下载直链。请从 optifine.net 下载安装器后，用它在本实例目录上执行安装；或改用 Iris + Sodium 方案。");
            }

            default:
                throw new InvalidOperationException($"暂不支持的加载器：{loader}");
        }
    }

    /* ==================================================================
       工具
       ================================================================== */

    /// <summary>按当前平台求值 Minecraft 的 rules 数组。</summary>
    public static bool IsAllowed(JsonElement lib)
    {
        if (!lib.TryGetProperty("rules", out var rules) || rules.ValueKind != JsonValueKind.Array)
            return true;

        var allowed = false;
        foreach (var rule in rules.EnumerateArray())
        {
            var action = rule.TryGetProperty("action", out var a) ? a.GetString() : "allow";
            if (!MatchesRule(rule)) continue;
            allowed = action == "allow";
        }
        return allowed;
    }

    /* ==================================================================
       这个库到底该不该用

       rules 只管平台与特性，管不了架构 —— 新版版本 JSON 里
       `…:natives-windows` 与 `…:natives-windows-arm64` 的 rules 完全一致
       （都是 allow windows），区别只在库名的分类符里。
       所以"可用性"必须在 rules 之外再判一次分类符，否则 arm64 的 dll
       会被当成可用原生库下下来、塞进 classpath。
       ================================================================== */

    /// <summary>库名里的分类符（<c>group:artifact:version:classifier</c> 的第 4 段）。</summary>
    public static string? ClassifierOf(JsonElement lib)
    {
        if (!lib.TryGetProperty("name", out var n) || n.ValueKind != JsonValueKind.String) return null;
        var parts = (n.GetString() ?? "").Split(':');
        return parts.Length >= 4 ? parts[3] : null;
    }

    /// <summary>如果是"原生库"，返回它的分类符；普通库返回 null。</summary>
    public static string? NativeClassifierOf(JsonElement lib)
    {
        var c = ClassifierOf(lib);
        return c is not null && c.StartsWith("natives-", StringComparison.OrdinalIgnoreCase) ? c : null;
    }

    /// <summary>这个库是否适用于当前平台（rules + 原生库架构都要过）。</summary>
    public static bool IsUsable(JsonElement lib)
    {
        if (!IsAllowed(lib)) return false;

        var native = NativeClassifierOf(lib);
        if (native is null) return true;

        // natives-windows-arm64 vs natives-windows：必须精确匹配本机架构。
        // 多下、多挂一个错误架构的 jar 不只是浪费，还可能让 LWJGL 解压出
        // 一个加载不起来的 dll，报出与真实原因毫无关系的 UnsatisfiedLinkError。
        var want = PlatformServices.Current.NativePlatformSuffix;
        if (string.IsNullOrEmpty(want)) return false;
        return string.Equals(native, "natives-" + want, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>把一个库的 artifact 加进下载队列；返回它的落地路径（用于解压原生库）。</summary>
    private static bool TryQueueArtifact(
        string instancePath, JsonElement lib, List<(string Url, string Dest, long Size, string Label)> jobs,
        out string dest)
    {
        dest = "";
        if (!lib.TryGetProperty("downloads", out var ld) || ld.ValueKind != JsonValueKind.Object) return false;
        if (!ld.TryGetProperty("artifact", out var art) || art.ValueKind != JsonValueKind.Object) return false;
        if (!art.TryGetProperty("path", out var ap) || ap.ValueKind != JsonValueKind.String) return false;
        if (!art.TryGetProperty("url", out var au) || au.ValueKind != JsonValueKind.String) return false;
        if (string.IsNullOrEmpty(au.GetString())) return false;

        var rel = ap.GetString()!;
        dest = Path.Combine(InstanceService.LibraryDir(instancePath), rel);
        var size = art.TryGetProperty("size", out var asz) && asz.TryGetInt64(out var s) ? s : 0;
        jobs.Add((au.GetString()!, dest, size, Path.GetFileName(rel)));
        return true;
    }

    /// <summary>
    /// 原生库的落地目录（层级按源码版本 JSON 的约定）：
    ///
    ///   版本 JSON 里 java.library.path 写的是 <c>${natives_directory}/java</c>，
    ///   JNA / LWJGL / Netty 还各自要一个可写的 <c>/jna</c>、<c>/lwjgl</c>、<c>/netty</c> 目录
    ///   用来**自己**从 classpath 的 jar 里解压 dll。老版本模板则直接用
    ///   <c>${natives_directory}</c>（没有后缀）。
    ///
    ///   所以这里同时准备两套：平铺的那份给老模板，<c>/java</c> 那份给新模板。
    ///   合计十几 MB，换来"两种模板都能跑"，比按版本号判断格式可靠得多。
    /// </summary>
    private static string NativeRoot(string versionDir) => Path.Combine(versionDir, "natives");

    private static readonly string[] NativeSubDirs = { "java", "jna", "lwjgl", "netty" };

    /// <summary>确保原生库的各级目录都在。写权限也要提前暴露问题，而不是等游戏启动才失败。</summary>
    private static IEnumerable<string> EnsureNativeDirs(string nativesRoot)
    {
        Directory.CreateDirectory(nativesRoot);
        yield return nativesRoot;
        foreach (var sub in NativeSubDirs)
        {
            var d = Path.Combine(nativesRoot, sub);
            Directory.CreateDirectory(d);
            yield return d;
        }
    }

    private static bool MatchesRule(JsonElement rule)
    {
        if (!rule.TryGetProperty("os", out var os) || os.ValueKind != JsonValueKind.Object)
        {
            // 没有 os 条件：只有 features 条件时也认为是匹配的（features 我们在启动时恒为 false）
            return true;
        }

        if (os.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String)
        {
            if (!string.Equals(name.GetString(), "windows", StringComparison.OrdinalIgnoreCase)) return false;
        }

        if (os.TryGetProperty("arch", out var arch) && arch.ValueKind == JsonValueKind.String)
        {
            var want = arch.GetString() ?? "";
            var is64 = Environment.Is64BitOperatingSystem;
            if (want == "x86" && is64) return false;
        }

        if (os.TryGetProperty("version", out var ver) && ver.ValueKind == JsonValueKind.String)
        {
            // Windows 版本正则，本机必然满足主流的 10/11，直接放行
        }

        return true;
    }

    private static string? NativeClassifier(JsonElement lib)
    {
        if (!lib.TryGetProperty("natives", out var natives) || natives.ValueKind != JsonValueKind.Object) return null;
        if (!natives.TryGetProperty("windows", out var w) || w.ValueKind != JsonValueKind.String) return null;
        return w.GetString()?.Replace("${arch}", Environment.Is64BitProcess ? "64" : "32");
    }

    /// <summary>
    /// 把原生库 jar 里的动态库解出来。
    ///
    /// 只往两个地方放：
    ///   · <paramref name="nativesRoot"/>          —— 老模板的 <c>${natives_directory}</c>
    ///   · <paramref name="nativesRoot"/>/java     —— 新模板的 <c>${natives_directory}/java</c>
    ///
    /// 剩下的 /jna、/lwjgl、/netty 只需要"存在且可写"：它们分别是 JNA 的临时目录、
    /// LWJGL 与 Netty 的**自解压目的地**，那三个库自己会从 classpath 的 jar 里把 dll 解出来。
    /// 而且 dll 已经躺在 java.library.path 上了（就是 /java），LWJGL 会直接加载、根本不解压。
    /// </summary>
    private static void ExtractNatives(string jarPath, string nativesRoot)
    {
        if (!File.Exists(jarPath))
        {
            // 缺原生库是硬伤：游戏会在加载 LWJGL 时崩，报 UnsatisfiedLinkError。
            // 这里必须留痕，否则用户只看到一个莫名其妙的游戏崩溃。
            Logger.Warn($"[install] 原生库缺失，未能解压：{Path.GetFileName(jarPath)}");
            return;
        }

        try
        {
            var targets = EnsureNativeDirs(nativesRoot)
                .Where(d => string.Equals(d, nativesRoot, StringComparison.OrdinalIgnoreCase)
                            || string.Equals(Path.GetFileName(d), "java", StringComparison.OrdinalIgnoreCase))
                .ToArray();

            using var zip = ZipFile.OpenRead(jarPath);
            foreach (var entry in zip.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name)) continue;
                // 排除签名与元数据，只要真正的原生库
                if (entry.FullName.StartsWith("META-INF/", StringComparison.OrdinalIgnoreCase)) continue;
                if (!entry.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) &&
                    !entry.Name.EndsWith(".so", StringComparison.OrdinalIgnoreCase) &&
                    !entry.Name.EndsWith(".dylib", StringComparison.OrdinalIgnoreCase)) continue;

                foreach (var dir in targets)
                    entry.ExtractToFile(Path.Combine(dir, entry.Name), true);
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"[install] 解压原生库失败 {Path.GetFileName(jarPath)}：{ex.Message}");
        }
    }

    private static string? MavenToPath(string coordinate)
    {
        var parts = coordinate.Split(':');
        if (parts.Length < 3) return null;
        var group = parts[0].Replace('.', '/');
        var artifact = parts[1];
        var version = parts[2];
        var classifier = parts.Length > 3 ? "-" + parts[3] : "";
        return $"{group}/{artifact}/{version}/{artifact}-{version}{classifier}.jar";
    }
}
