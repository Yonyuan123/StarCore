using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using StarCore.Core.App;
using StarCore.Core.Models;
using StarCore.Core.Services;

namespace StarCore.Core;

/// <summary>
/// 内核门面：把前端的方法名映射到具体服务。
///
/// 这一层刻意保持"薄而显式"—— 用 switch 而不是反射分发，
/// 好处是拼错方法名会立刻得到清晰的错误，而不是悄悄走到别处；
/// 也便于一眼看清整个前后端接口面。
/// </summary>
public static class StarCoreHost
{
    /// <summary>推送给界面的事件（name, payload）。由宿主转发到 WebView2。</summary>
    public static event Action<string, object?>? Event;

    [ThreadStatic] private static bool _emitting;

    public static void Emit(string name, object? payload)
    {
        // 事件订阅方（宿主桥）在异常分支里可能写日志，而日志又会触发事件，
        // 因此这里必须防重入；否则一次推送失败就会演变成栈溢出。
        if (_emitting) return;
        _emitting = true;
        try { Event?.Invoke(name, payload); }
        catch { /* 订阅方自己负责兜底，内核不因推送失败而中断 */ }
        finally { _emitting = false; }
    }

    /* ==================================================================
       初始化
       ================================================================== */

    public static void Initialize()
    {
        StarCorePaths.Initialize();
        Logger.Initialize("launcher");
        Logger.Info($"StarCore 内核启动 · 数据目录 {StarCorePaths.DataRoot} · 便携模式 {StarCorePaths.IsPortable}");

        SettingsService.Load();
        InstanceService.Initialize();
        AccountService.Initialize();
        VersionManifestService.LoadCache();

        // 服务事件 → 前端事件
        TaskCenter.Updated += t => Emit("task.update", t);
        LaunchService.StateChanged += s => Emit("launch.state", s);
        LaunchService.GameOutput += line => Emit("log.line", new { line, level = LevelOf(line) });

        Logger.LineWritten += (line, level) => Emit("log.line", new { line, level = level.ToString().ToLowerInvariant() });

        // 后台预热：Java 扫描与版本清单，两者都不阻塞界面
        _ = Task.Run(async () =>
        {
            try
            {
                // 回收上次进程没跑完留下的下载分片。分片名带唯一标识后不会互相覆盖，
                // 代价是需要有人来收尾 —— 这里按"超过 1 小时未改动"判定为垃圾。
                Http.SweepOrphanParts(StarCorePaths.InstancesRoot);
            }
            catch (Exception ex) { Logger.Debug($"[host] 清理下载分片失败：{ex.Message}"); }

            try
            {
                // 先清掉上次中断下载留下的临时运行时目录，
                // 否则它们会被扫描当成可用 Java —— 那会导致模板选中一个残缺的
                // java.exe，用户看到 Windows 的「无法找到入口」系统弹窗。
                JavaService.CleanupStaleTempRuntimes();
                JavaService.Scan();
                Emit("java.changed", new { javas = JavaService.Cached });
            }
            catch (Exception ex) { Logger.Warn($"[host] Java 预热失败：{ex.Message}"); }

            if (VersionManifestService.Stale)
            {
                try
                {
                    await VersionManifestService.RefreshAsync();
                    Emit("versions.changed", new { versions = VersionManifestService.Versions });
                }
                catch (Exception ex) { Logger.Warn($"[host] 版本清单预热失败：{ex.Message}"); }
            }
        });
    }

    private static string LevelOf(string line)
    {
        if (line.Contains("ERROR", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("Exception", StringComparison.OrdinalIgnoreCase)) return "error";
        if (line.Contains("WARN", StringComparison.OrdinalIgnoreCase)) return "warn";
        return "debug";
    }

    /* ==================================================================
       分发
       ================================================================== */

    public static async Task<object?> InvokeAsync(string method, JsonElement p, CancellationToken ct = default)
    {
        // 调用跟踪。Debug 级只落盘不推界面，正常使用不会打扰用户；
        // 但一旦出现"界面卡在某个操作"的反馈，日志里立刻能看出是哪个方法没回来。
        Logger.Debug($"[host] → {method}");
        var started = Environment.TickCount64;
        try
        {
            var result = await DispatchAsync(method, p, ct);
            var cost = Environment.TickCount64 - started;
            // 只有慢调用值得记；快调用上百条反而把日志淹了
            if (cost >= 200) Logger.Debug($"[host] ← {method} 用时 {cost}ms");
            return result;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, $"[host] ✗ {method}");
            throw;
        }
    }

    private static async Task<object?> DispatchAsync(string method, JsonElement p, CancellationToken ct)
    {
        return method switch
        {
            /* ---- 应用信息 ---- */
            "app.info" => BuildAppInfo(),
            "app.checkUpdate" => await CheckUpdateAsync(ct),
            "app.announcements" => Announcements(),
            "app.licenses" => Licenses(),

            /* ---- 设置 ---- */
            "settings.get" => SettingsService.Current,
            "settings.set" => ApplySettings(p),
            "settings.reset" => ResetSettings(),

            /* ---- 账户 ---- */
            "account.list" => new
            {
                profiles = AccountService.Profiles.Select(ToPublic).ToList(),
                activeUuid = AccountService.ActiveUuid,
            },
            "account.addOffline" => AddOffline(p),
            "account.remove" => RemoveAccount(p),
            "account.setActive" => SetActiveAccount(p),
            "account.refresh" => await RefreshAccountAsync(p, ct),
            "account.validateName" => ValidateName(p),
            "account.msStart" => await AccountService.StartDeviceCodeAsync(ct),
            "account.msPoll" => await AccountService.PollDeviceCodeAsync(Str(p, "deviceCode") ?? "", ct),
            "account.msProbe" => await MicrosoftAuth.ProbeAsync(ct),
            "account.skin.get" => AccountService.GetSkin(Str(p, "uuid") ?? AccountService.ActiveUuid ?? ""),
            "account.skin.upload" => await UploadSkinAsync(p, ct),
            "account.skin.setModel" => AccountService.SetSkinModel(Str(p, "uuid") ?? "", Str(p, "model") ?? "classic"),
            "account.cape.list" => await AccountService.ListCapesAsync(Str(p, "uuid") ?? "", ct),
            "account.cape.set" => SetCape(p),

            /* ---- 版本 ---- */
            "version.manifest.list" => new
            {
                versions = VersionManifestService.Versions,
                cachedAt = VersionManifestService.CachedAt,
                stale = VersionManifestService.Stale,
            },
            "version.manifest.refresh" => new { versions = await VersionManifestService.RefreshAsync(ct) },
            "version.install" => StartInstall(p),
            "version.loaders" => LoaderCatalog(),

            // 某个加载器在某个游戏版本下有哪些版本可选。
            // 与上面的 LoaderCatalog() 是两件事：那个返回"支持哪些加载器"（静态），
            // 这个返回"这个加载器有哪些版本"（联网）。之前加载器页把两者搞混了，
            // 结果下拉框里列的是加载器名字而不是加载器版本。
            "version.loaders.list" => await LoaderCatalogService.ListVersionsAsync(
                Str(p, "loader") ?? "fabric", Str(p, "versionId") ?? Str(p, "gameVersion") ?? "", ct),

            // —— 自定义媒体 ——
            "theme.importMedia" => MediaService.Import(Str(p, "path") ?? "", Str(p, "kind") ?? "image"),
            "theme.clearMedia" => MediaService.Remove(Str(p, "kind") ?? "image"),
            "theme.mediaInfo" => MediaService.Info(),

            "source.info" => new { name = DownloadSource.CurrentName, kind = DownloadSource.Kind.ToString() },
            "source.ping" => await DownloadSource.PingAllAsync(ct),

            /* ---- 游戏目录（HMCL 的多目录管理）---- */
            "gamedir.list" => new { dirs = SettingsService.Current.GameDirs },
            "gamedir.add" => GamedirAdd(Str(p, "path") ?? ""),
            "gamedir.remove" => GamedirRemove(Str(p, "path") ?? ""),

            /* ---- 实例 ---- */
            "instance.list" => new
            {
                instances = InstanceService.List(),
                activeId = InstanceService.ActiveId,
            },
            "instance.create" => CreateInstance(p),
            "instance.remove" => InstanceService.Remove(Long(p, "id") ?? 0, Bool(p, "deleteFiles") ?? false)
                ? OpResult.Success("已删除") : OpResult.Fail("删除失败。"),
            "instance.update" => InstanceService.Update(Long(p, "id") ?? 0, ToDict(p)),
            "instance.setIcon" => new { url = InstanceService.SetCustomIcon(Long(p, "id") ?? 0, Str(p, "path") ?? "") },
            "instance.setActive" => InstanceService.SetActive(Long(p, "id") ?? 0)
                ? OpResult.Success() : OpResult.Fail("实例不存在。"),
            "instance.duplicate" => InstanceService.Duplicate(Long(p, "id") ?? 0, Str(p, "name")),
            "instance.importModpack" => await ImportModpackAsync(p, ct),

            /* ---- Java ---- */
            "java.list" => new { javas = JavaService.List() },
            "java.scan" => new { javas = JavaService.Scan() },
            "java.validate" => new { java = JavaService.Validate(Str(p, "path") ?? "") },
            "java.test" => await JavaService.TestAsync(Str(p, "path") ?? "", ct),

            // Java 自动下载：立即返回 taskId，下载在后台跑。
            // 绝不能 await 完再返回 —— 一个 Java 运行时有一百多个文件，界面会卡死。
            "java.download" => new { taskId = JavaService.StartDownload(Int(p, "major") ?? 21) },
            "java.remove" => RemoveJava(p),

            /* ---- 启动 ---- */
            "launch.start" => await LaunchAsync(p, ct),
            "launch.inspect" => await InspectLaunchAsync(p, ct),
            "launch.kill" => LaunchService.Kill(Long(p, "instanceId") ?? 0)
                ? OpResult.Success() : OpResult.Fail("没有正在运行的游戏进程。"),

            /* ---- 内容 ---- */
            // —— 收藏夹（本地数据，不依赖任何账号）——
            // 前端错误上报。单独一个方法名是为了在日志里一眼区分"谁报的错"。
            "ui.error" => LogUiError(p),

            // 前端主动写日志。用于自检页、性能采样这类需要把结果落到文件里的场景 ——
            // 靠截图看"测试通过了几项"既慢又容易看错。
            "ui.log" => LogUi(p),

            "favorite.list" => FavoriteService.List(),
            "favorite.toggle" => FavoriteService.Toggle(new FavoriteItem
            {
                Id = Str(p, "id") ?? "",
                Slug = Str(p, "slug") ?? "",
                Title = Str(p, "title") ?? "",
                IconUrl = Str(p, "iconUrl"),
                Description = Str(p, "description"),
                Kind = Str(p, "kind") ?? "mod",
                Downloads = Long(p, "downloads") ?? 0,
            }),
            "favorite.remove" => FavoriteService.Remove(Str(p, "id") ?? ""),

            // —— 资源详情翻译 ——
            "resource.translate" => await TranslateService.TranslateAsync(
                Str(p, "text") ?? "", Str(p, "target") ?? "zh-CN", ct),

            "content.scan" => new { items = await ContentService.ScanAsync(Long(p, "instanceId") ?? 0, Str(p, "kind") ?? "mod", ct) },
            "content.toggle" => ContentService.Toggle(Long(p, "instanceId") ?? 0, Str(p, "kind") ?? "mod",
                Str(p, "path") ?? "", Bool(p, "enabled") ?? true),
            "content.import" => ContentService.Import(Long(p, "instanceId") ?? 0, Str(p, "kind") ?? "mod",
                StrArray(p, "paths")),
            "content.remove" => ContentService.Remove(Long(p, "instanceId") ?? 0, Str(p, "kind") ?? "mod", Str(p, "path") ?? ""),

            /* ---- 资源 ---- */
            "modrinth.search" => await ModrinthClient.SearchAsync(
                Str(p, "kind") ?? "mod", Str(p, "query"), Str(p, "gameVersion"), Str(p, "loader"),
                Str(p, "sort"), Int(p, "offset") ?? 0, Int(p, "limit") ?? 20, ct),
            "modrinth.project" => await ModrinthProjectAsync(p, ct),

            // 递归解析必需前置（只解 required，按"前置在前"排序）
            "modrinth.resolveDeps" => await DependencyResolver.PlanAsync(
                Str(p, "projectId") ?? Str(p, "id") ?? "",
                Str(p, "versionId"),
                Str(p, "gameVersion"), Str(p, "loader"),
                Long(p, "instanceId") ?? 0, ct),

            // 按解析结果批量安装（顺序执行，前置天然排在前面）
            "modrinth.installPlan" => StartInstallPlan(p),
            // 参数名要同时接受 id 与 projectId：
            // 详情页发的是 `id`，这里原来只读 `projectId`，于是 projectId 永远为空，
            // 请求打到了 /project//version，返回空列表 —— 表现就是详情页的「安装」按钮
            // 永久置灰、版本选择器一直显示占位文案。
            "modrinth.versions" => await ModrinthClient.VersionsAsync(
                Str(p, "projectId") ?? Str(p, "id") ?? "", Str(p, "gameVersion"), Str(p, "loader"), ct),
            "modrinth.install" => await ModrinthClient.InstallAsync(
                Str(p, "projectId") ?? "", Str(p, "versionId"), Long(p, "instanceId") ?? 0,
                Str(p, "kind") ?? "mod", Str(p, "gameVersion"), Str(p, "loader"), ct),

            /* ---- 日志 ---- */
            "log.list" => new { files = LogService.List() },
            "log.read" => ReadLog(p),
            "log.analyze" => LogService.Analyze(Str(p, "file")),

            // 导出诊断报告 zip（默认放桌面）。用户求助时只需要把这一个文件发出来。
            "log.export" => await LogService.ExportReportAsync(
                Long(p, "instanceId"), Str(p, "targetDir"), ct),
            "log.delete" => LogService.Delete(Str(p, "file") ?? ""),
            "log.clear" => LogService.ClearAll(),
            "task.cancel" => CancelTask(p),
            "task.list" => new { tasks = TaskCenter.All },

            _ => throw new InvalidOperationException($"未知的内核方法：{method}"),
        };
    }

    /* ==================================================================
       应用信息
       ================================================================== */

    public static AppInfo BuildAppInfo()
    {
        var totalMb = 0L;
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                totalMb = (long)(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1024 / 1024);
            }
        }
        catch { }

        return new AppInfo
        {
            Name = "StarCore",
            Version = StarCorePaths.ProductVersion,
            CoreVersion = typeof(StarCoreHost).Assembly.GetName().Version?.ToString(3) ?? "1.0.0",
            OsName = RuntimeInformation.OSDescription,
            Arch = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant(),
            Locale = "zh-CN",
            DataDir = StarCorePaths.DataRoot,
            TotalMemoryMb = totalMb,
            Portable = StarCorePaths.IsPortable,
            DotNetVersion = RuntimeInformation.FrameworkDescription,
        };
    }

    /// <summary>
    /// 检查启动器更新。
    ///
    /// 为什么用 GitHub Releases 当"更新服务器"：它零运维、零成本、天然带版本归档与
    /// 变更日志，而且公开接口不需要密钥 —— 对一个开源启动器来说这是最合理的选择。
    /// 仓库地址放在设置里，用户/发行方可以换成自己的 fork。
    /// </summary>
    private static async Task<object> CheckUpdateAsync(CancellationToken ct)
    {
        var repo = (SettingsService.Current.UpdateRepo ?? "").Trim().Trim('/');
        var current = StarCorePaths.ProductVersion;

        if (repo.Length == 0 || !repo.Contains('/'))
        {
            return new
            {
                hasUpdate = false,
                configured = false,
                currentVersion = current,
                error = "尚未配置更新源。在「设置 → 通用 → 更新源」填入 GitHub 仓库（用户名/仓库名）即可。",
            };
        }

        try
        {
            // GitHub 的 Releases 接口：只取 latest，够用且不会被限流得太惨
            var url = $"https://api.github.com/repos/{repo}/releases/latest";
            var json = await DownloadSource.GetStringAsync(url, 4 * 1024 * 1024, ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var tag = (root.TryGetProperty("tag_name", out var t) ? t.GetString() : null) ?? "";
            var name = (root.TryGetProperty("name", out var n) ? n.GetString() : null) ?? tag;
            var notes = (root.TryGetProperty("body", out var b) ? b.GetString() : null) ?? "";
            var htmlUrl = (root.TryGetProperty("html_url", out var h) ? h.GetString() : null) ?? "";
            var publishedAt = root.TryGetProperty("published_at", out var p) ? p.GetString() : null;

            // 取第一个安装包附件（按约定用 .exe 优先）
            string? downloadUrl = null;
            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var a in assets.EnumerateArray())
                {
                    var an = (a.TryGetProperty("name", out var anEl) ? anEl.GetString() : null) ?? "";
                    var au = a.TryGetProperty("browser_download_url", out var auEl) ? auEl.GetString() : null;
                    if (au is null) continue;
                    if (an.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) { downloadUrl = au; break; }
                    downloadUrl ??= au;
                }
            }

            // 版本号可能带 v 前缀，比较时去掉
            var latest = tag.TrimStart('v', 'V').Trim();
            var currentClean = current.TrimStart('v', 'V').Trim();
            var newer = VersionComparer.Compare(latest, currentClean) > 0;

            Logger.Info($"[update] 当前 {current}，最新 {latest}（{repo}）→ {(newer ? "有更新" : "已是最新")}");

            return new
            {
                hasUpdate = newer,
                configured = true,
                currentVersion = current,
                latestVersion = latest,
                title = name,
                notes,
                downloadUrl = downloadUrl ?? "",
                htmlUrl,
                publishedAt,
                repo,
            };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // 网络不可达时如实说明，而不是伪装成"已是最新"—— 后者会让用户以为一切正常
            Logger.Warn($"[update] 检查更新失败：{ex.Message}");
            return new
            {
                hasUpdate = false,
                configured = true,
                currentVersion = current,
                repo,
                error = $"无法访问更新源：{ex.Message}",
            };
        }
    }

    /// <summary>
    /// 公告与更新记录。
    /// 尚未部署官方公告服务时返回内置的"首次运行须知"，而不是空数组 ——
    /// 用户第一次打开公告页不应该看到一片空白。
    /// </summary>
    private static object Announcements()
    {
        var now = DateTimeOffset.Now;
        var items = new List<object>
        {
            new
            {
                id = "welcome",
                type = "notice",
                title = "欢迎使用 StarCore",
                author = "StarCore Project",
                publishedAt = now,
                body = "StarCore 是一个从零实现的 Minecraft 启动器，专注于三件事：干净的界面、可靠的安装、透明的日志。\n\n"
                     + "开始之前，你需要先做两件事：在「账户」页添加一个账户（微软正版或离线都可以），在「下载」页安装一个游戏版本。"
                     + "之后回到「实例」页点击启动即可。\n\n"
                     + "目前所有数据都保存在本机，不联网上报任何信息。",
                url = "",
            },
            new
            {
                id = "offline-note",
                type = "notice",
                title = "关于离线账户的说明",
                author = "StarCore Project",
                publishedAt = now.AddDays(-1),
                body = "离线账户仅用于单机游玩，无法进入正版服务器。若你需要联机，请在「账户」页使用微软账户登录。\n\n"
                     + "离线账户的 UUID 采用与官方一致的 v3 方案（基于用户名哈希），因此同一用户名在任何启动器里得到的 UUID 都相同，"
                     + "存档不会因为换启动器而错乱。",
                url = "",
            },
            new
            {
                id = "optifine-note",
                type = "notice",
                title = "OptiFine 需要手动安装",
                author = "StarCore Project",
                publishedAt = now.AddDays(-2),
                body = "OptiFine 官方没有公开的下载直链，启动器无法代你下载。请在 optifine.net 下载安装器后手动执行。\n\n"
                     + "如果你的目标是提升帧率而非使用 OptiFine 独有的光影功能，推荐改用 Iris + Sodium：它们都在 Modrinth 上，"
                     + "可以在「资源」页直接搜索安装。",
                url = "",
            },
        };

        return new { items };
    }

    private static object SetCape(JsonElement p)
    {
        // 披风的"应用"需要 Mojang 侧接口，当前版本只记录选择并同步到账户信息，
        // 保证界面状态一致；不做静默失败。
        var uuid = Str(p, "uuid") ?? AccountService.ActiveUuid ?? "";
        var capeId = Str(p, "capeId");
        Logger.Info($"[account] 用户选择披风 {capeId ?? "(无)"} for {uuid}");
        Emit("accounts.changed", null);
        return OpResult.Success("已记录披风选择。披风的启用需要在游戏内或官网完成。");
    }

    private static object Licenses() => new
    {
        items = new[]
        {
            new { name = "Microsoft.Web.WebView2", license = "BSD-3-Clause", url = "https://github.com/MicrosoftEdge/WebView2Feedback" },
            new { name = "Lucide Icons", license = "ISC", url = "https://lucide.dev" },
            new { name = "Noto Sans SC", license = "SIL OFL 1.1", url = "https://fonts.google.com/noto" },
            new { name = "Bricolage Grotesque", license = "SIL OFL 1.1", url = "https://fonts.google.com/specimen/Bricolage+Grotesque" },
            new { name = "JetBrains Mono", license = "SIL OFL 1.1", url = "https://www.jetbrains.com/lp/mono/" },
        },
    };

    /* ==================================================================
       设置
       ================================================================== */

    private static Settings ApplySettings(JsonElement p)
    {
        var dict = new Dictionary<string, object?>();
        if (p.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in p.EnumerateObject())
            {
                dict[prop.Name] = prop.Value.ValueKind switch
                {
                    JsonValueKind.String => prop.Value.GetString(),
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    JsonValueKind.Number => prop.Value.TryGetInt32(out var i) ? i : prop.Value.GetDouble(),
                    _ => null,
                };
            }
        }
        return SettingsService.Patch(dict);
    }

    private static Settings ResetSettings()
    {
        SettingsService.Reset();
        Emit("settings.changed", SettingsService.Current);
        return SettingsService.Current;
    }

    /* ==================================================================
       账户
       ================================================================== */

    /// <summary>投影成前端可见的字段 —— 凭据（令牌）绝不出内核。</summary>
    private static object ToPublic(AccountInfo a) => new
    {
        uuid = a.Uuid,
        userName = a.UserName,
        profileType = a.ProfileType,
        skinUrl = a.SkinUrl is null ? null : WebAssets.Normalize(a.SkinUrl),
        skinFace = File.Exists(Path.Combine(StarCorePaths.CacheRoot, "skins", $"{a.Uuid}_face.png"))
            ? WebAssets.Data(Path.Combine(StarCorePaths.CacheRoot, "skins", $"{a.Uuid}_face.png")) : null,
        capeUrl = a.CapeUrl is null ? null : WebAssets.Normalize(a.CapeUrl),
        skinModel = a.SkinModel,
        addedAt = a.AddedAt,
    };

    private static object AddOffline(JsonElement p)
    {
        var (ok, error, _) = AccountService.AddOffline(Str(p, "name") ?? "");
        if (!ok) return OpResult.Fail(error ?? "创建失败。");
        Emit("accounts.changed", null);
        return OpResult.Success("账户已创建");
    }

    private static object RemoveAccount(JsonElement p)
    {
        var ok = AccountService.Remove(Str(p, "uuid") ?? "");
        if (ok) Emit("accounts.changed", null);
        return ok ? OpResult.Success("账户已移除") : OpResult.Fail("账户不存在。");
    }

    private static object SetActiveAccount(JsonElement p)
    {
        var ok = AccountService.SetActive(Str(p, "uuid") ?? "");
        if (ok) Emit("accounts.changed", null);
        return ok ? OpResult.Success() : OpResult.Fail("账户不存在。");
    }

    private static async Task<object> RefreshAccountAsync(JsonElement p, CancellationToken ct)
    {
        var uuid = Str(p, "uuid") ?? AccountService.ActiveUuid ?? "";
        var ok = await AccountService.RefreshAsync(uuid, ct);
        if (ok) Emit("accounts.changed", null);
        return ok ? OpResult.Success("已刷新") : OpResult.Fail("刷新失败，可能需要重新登录。");
    }

    private static object ValidateName(JsonElement p)
    {
        var name = (Str(p, "name") ?? "").Trim();
        var valid = System.Text.RegularExpressions.Regex.IsMatch(name, "^[A-Za-z0-9_]{3,16}$");
        return new
        {
            valid,
            message = valid ? null : "名称需为 3–16 位英文字母、数字或下划线。",
            uuid = valid ? AccountService.OfflineUuid(name) : null,
        };
    }

    private static async Task<object> UploadSkinAsync(JsonElement p, CancellationToken ct)
    {
        var uuid = Str(p, "uuid") ?? AccountService.ActiveUuid ?? "";
        var builtin = Str(p, "builtin");
        var path = Str(p, "path");

        // 内置皮肤：在缓存目录里准备一份，避免为几个演示皮肤打包图片资源
        if (!string.IsNullOrEmpty(builtin) && string.IsNullOrEmpty(path))
        {
            path = await EnsureBuiltinSkinAsync(builtin, ct);
        }

        var r = await AccountService.UploadSkinAsync(uuid, path, Str(p, "model") ?? "classic", ct);
        Emit("accounts.changed", null);
        return r;
    }

    /// <summary>
    /// 内置皮肤解析点。
    /// 当前版本不随包分发演示皮肤（避免内置任何"看起来像别人的账号"的素材），
    /// 因此这里只记录并返回 null，由上层提示用户上传 PNG。
    /// 保留这个扩展点，是为了将来能接入官方皮肤包而不改动调用方。
    /// </summary>
    private static Task<string?> EnsureBuiltinSkinAsync(string id, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Logger.Debug($"[account] 请求内置皮肤 {id}：当前版本需由用户自行上传 PNG");
        return Task.FromResult<string?>(null);
    }

    /* ==================================================================
       实例与安装
       ================================================================== */

    private static object GamedirAdd(string path)
    {
        path = path.Trim().TrimEnd('\\', '/');
        if (path.Length == 0 || !Directory.Exists(path)) return OpResult.Fail("目录不存在。");
        var dirs = SettingsService.Current.GameDirs;
        if (!dirs.Contains(path, StringComparer.OrdinalIgnoreCase)) dirs.Add(path);
        SettingsService.Save();
        Emit("settings.changed", SettingsService.Current);
        return OpResult.Success($"已添加 {path}");
    }

    private static object GamedirRemove(string path)
    {
        var dirs = SettingsService.Current.GameDirs;
        dirs.RemoveAll(d => string.Equals(d, path.Trim(), StringComparison.OrdinalIgnoreCase));
        SettingsService.Save();
        Emit("settings.changed", SettingsService.Current);
        return OpResult.Success("已移除");
    }

    private static object CreateInstance(JsonElement p)
    {
        var inst = InstanceService.Create(
            Str(p, "name") ?? "新实例",
            Str(p, "versionId"),
            Str(p, "loader") ?? "vanilla",
            Str(p, "loaderVersion"));
        Emit("instances.changed", null);
        // Create 之后立刻 Get 一定能取到；取不到说明索引被并发改坏了，
        // 这里给一个明确的异常而不是把 null 当结果返回（前端会以为创建成功）。
        return InstanceService.Get(inst.Id)
               ?? throw new InvalidOperationException($"实例 #{inst.Id} 创建后立即丢失，实例索引可能已损坏。");
    }

    private static object StartInstall(JsonElement p)
    {
        var versionId = Str(p, "versionId") ?? "";
        if (string.IsNullOrWhiteSpace(versionId))
            return OpResult.Fail("请先选择要安装的版本。");

        var loader = Str(p, "loader");
        var loaderVersion = Str(p, "loaderVersion");

        // 一键安装。
        //
        // 之前必须先手动建实例、再选中、才能装东西，这是纯粹的仪式感 ——
        // PCL / HMCL 都是"点下载就直接开装"，实例是安装过程的产物而不是前置条件。
        // 因此这里把 instanceId 变成可选：不给就现场建一个，并按 PCL 的规则命名。
        var instanceId = Long(p, "instanceId") ?? 0;
        var inst = instanceId > 0 ? InstanceService.Get(instanceId) : null;

        var created = false;
        if (inst is null)
        {
            // 用户显式指定了实例名就尊重它，否则按「版本-加载器_版本号」生成
            var desired = Str(p, "instanceName");
            if (string.IsNullOrWhiteSpace(desired))
                desired = InstanceService.SuggestName(versionId, loader, loaderVersion);

            inst = InstanceService.Create(
                InstanceService.UniqueName(desired),
                versionId,
                loader ?? "vanilla",
                loaderVersion);
            instanceId = inst.Id;
            created = true;
            Logger.Info($"[install] 一键安装自动创建实例 #{inst.Id}「{inst.Name}」");
        }

        // 未指定加载器时沿用实例上已有的（用户选中的实例可能已经配了 Fabric）
        loader ??= inst.Loader;
        if (string.IsNullOrWhiteSpace(loader) || loader == "vanilla") loader = "vanilla";

        var title = loader == "vanilla"
            ? $"安装 {versionId}"
            : $"安装 {versionId} · {loader}";

        var taskId = TaskCenter.Create(title);
        var finalInstanceId = instanceId;
        var finalLoader = loader;
        var finalLoaderVersion = loaderVersion;

        // 后台跑：立即返回 taskId 与实例信息，界面不用干等安装完成
        _ = Task.Run(() => InstallService.InstallAsync(
            finalInstanceId, versionId, finalLoader, finalLoaderVersion, taskId, CancellationToken.None));

        if (created) Emit("instances.changed", null);

        return new
        {
            taskId,
            instanceId,
            instanceName = inst.Name,
            created,
        };
    }

    private static object LoaderCatalog() => new
    {
        loaders = new object[]
        {
            new { id = "vanilla",  name = "原版",      desc = "不带任何加载器，最纯粹的体验。", requiresMods = false },
            new { id = "fabric",   name = "Fabric",    desc = "轻量、更新最快，适合新版本与轻量模组。", requiresMods = false },
            new { id = "quilt",    name = "Quilt",     desc = "Fabric 的社区分支，兼容大部分 Fabric 模组。", requiresMods = false },
            new { id = "forge",    name = "Forge",     desc = "生态最广的老牌加载器，大型整合包首选。", requiresMods = false },
            new { id = "neoforge", name = "NeoForge",  desc = "Forge 的现代分支，新版本主流选择。", requiresMods = false },
            new { id = "optifine", name = "OptiFine",  desc = "以性能优化著称，需从官网获取安装器。", requiresMods = false },
        },
    };

    private static async Task<object> ImportModpackAsync(JsonElement p, CancellationToken ct)
    {
        var path = Str(p, "path");
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return OpResult.Fail("整合包文件不存在。");

        var result = await ModrinthClient.ImportModpackFileAsync(path, null, ct);
        Emit("instances.changed", null);
        return result;
    }

    /* ==================================================================
       Java / 启动 / 日志
       ================================================================== */

    private static object RemoveJava(JsonElement p)
    {
        JavaService.Remove(Str(p, "path") ?? "");
        Emit("java.changed", new { javas = JavaService.Cached });
        return OpResult.Success("已移除");
    }

    private static async Task<object> LaunchAsync(JsonElement p, CancellationToken ct)
    {
        var id = Long(p, "instanceId") ?? InstanceService.ActiveId ?? 0;
        if (id == 0) throw new InvalidOperationException("请先选择一个实例。");
        var pid = await LaunchService.StartAsync(id, ct);
        return new { pid };
    }

    /// <summary>
    /// 只看不启动：返回将要执行的完整命令行。
    /// 用户报"启动不了"时，先看这条命令就能把问题范围缩小到"命令行错"还是"Java/游戏本身错"。
    /// </summary>
    private static async Task<object> InspectLaunchAsync(JsonElement p, CancellationToken ct)
    {
        var id = Long(p, "instanceId") ?? InstanceService.ActiveId ?? 0;
        if (id == 0) throw new InvalidOperationException("请先选择一个实例。");

        var plan = await LaunchService.InspectAsync(id, ct);
        return new
        {
            instanceId = plan.InstanceId,
            launchVersionId = plan.LaunchVersionId,
            javaPath = plan.Java.Path,
            javaMajor = plan.Java.Major,
            requiredJavaMajor = plan.DeclaredJavaMajor,
            mainClass = plan.MainClass,
            classPath = plan.ClassPath,
            commandLine = plan.CommandLine,
            jvmArgs = plan.JvmArgs,
            gameArgs = plan.GameArgs,
        };
    }

    private static object ReadLog(JsonElement p)
    {
        var file = Str(p, "file");
        var max = Int(p, "maxLines") ?? 5000;

        if (string.IsNullOrWhiteSpace(file))
        {
            var latest = LogService.List().FirstOrDefault();
            file = latest?.Path;
            if (file is null) return new { file = (string?)null, lines = Array.Empty<string>(), truncated = false };
        }

        var (lines, truncated) = LogService.Read(file, max);
        return new { file, lines, truncated };
    }

    private static object CancelTask(JsonElement p)
    {
        var id = Str(p, "id") ?? "";
        TaskCenter.Cancel(id);
        return OpResult.Success();
    }

    private static async Task<object> ModrinthProjectAsync(JsonElement p, CancellationToken ct)
    {
        var id = Str(p, "id") ?? "";
        var proj = await ModrinthClient.ProjectAsync(id, ct);
        if (proj is null) return OpResult.Fail("项目不存在或资源库不可达。");

        var root = proj.Value;

        // 前置依赖必须单独查：Modrinth 的 /project/{id} 里**没有** dependencies 字段，
        // 依赖是挂在每个版本上的。之前直接读 root["dependencies"]，结果永远是空，
        // 界面上所有资源都显示成"无前置"——那是假的。
        var dependencies = new List<object>();
        try
        {
            var versions = await ModrinthClient.VersionsAsync(id, null, null, ct);
            var latest = versions.FirstOrDefault();

            // 优先用与"最新正式版"匹配的那一条；没有正式版就用第一条
            var chosen = versions.FirstOrDefault(v => v.VersionType == "release") ?? latest;

            if (chosen is not null && chosen.Dependencies.Count > 0)
            {
                // dependency_type 有三类要区分展示：必需 / 可选 / 不兼容
                var wantIds = chosen.Dependencies
                    .Where(d => !string.IsNullOrEmpty(d.ProjectId))
                    .Select(d => d.ProjectId)
                    .Distinct()
                    .ToList();

                var titles = new Dictionary<string, (string Title, string? Icon)>();

                // 一次最多查 100 个，超出的分批
                for (var i = 0; i < wantIds.Count; i += 100)
                {
                    var batch = wantIds.Skip(i).Take(100).ToList();
                    var url = "https://api.modrinth.com/v2/projects?ids=" +
                              Uri.EscapeDataString("[" + string.Join(",", batch.Select(x => '"' + x + '"')) + "]");
                    try
                    {
                        var text = await Http.GetStringAsync(url, ct);
                        using var doc = JsonDocument.Parse(text);
                        if (doc.RootElement.ValueKind != JsonValueKind.Array) continue;
                        foreach (var item in doc.RootElement.EnumerateArray())
                        {
                            var pid = item.TryGetProperty("id", out var pidEl) ? pidEl.GetString() : null;
                            if (pid is null) continue;
                            titles[pid] = (
                                item.TryGetProperty("title", out var t) ? t.GetString() ?? pid : pid,
                                item.TryGetProperty("icon_url", out var ic) ? ic.GetString() : null);
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Debug($"[modrinth] 前置依赖名称查询失败（降级显示 id）：{ex.Message}");
                    }
                }

                foreach (var d in chosen.Dependencies)
                {
                    if (string.IsNullOrEmpty(d.ProjectId)) continue;
                    var known = titles.TryGetValue(d.ProjectId, out var meta);
                    dependencies.Add(new
                    {
                        projectId = d.ProjectId,
                        title = known ? meta.Title : d.ProjectId,
                        iconUrl = known ? meta.Icon : null,
                        // 前端据此分色：必需=accent，可选=中性，不兼容=danger
                        type = string.IsNullOrEmpty(d.DependencyType) ? "required" : d.DependencyType,
                        versionId = d.VersionId,
                    });
                }

                // 同一个前置可能出现多条，去重后给前端
                dependencies = dependencies
                    .GroupBy(x => x.GetType().GetProperty("projectId")!.GetValue(x))
                    .Select(g => g.First())
                    .ToList();
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"[modrinth] 解析前置依赖失败（不影响详情展示）：{ex.Message}");
        }

        // Modrinth 的 /project/{id} **不返回 author**（只有 team id）。
        // 作者要从 /team/{id}/members 里找 role == "Owner" 的那位。
        // 之前直接读 root["author"]，永远是空 —— 于是详情页一律显示「作者 未知」。
        string? author = null;
        try
        {
            var teamId = root.TryGetProperty("team", out var tm) ? tm.GetString() : null;
            if (!string.IsNullOrEmpty(teamId))
            {
                var membersJson = await Http.GetStringAsync(
                    $"https://api.modrinth.com/v2/team/{Uri.EscapeDataString(teamId)}/members", ct);
                using var membersDoc = JsonDocument.Parse(membersJson);
                if (membersDoc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    string? owner = null, first = null;
                    foreach (var m in membersDoc.RootElement.EnumerateArray())
                    {
                        var role = m.TryGetProperty("role", out var r) ? r.GetString() : null;
                        var name = m.TryGetProperty("user", out var u) && u.TryGetProperty("username", out var un)
                            ? un.GetString() : null;
                        if (string.IsNullOrEmpty(name)) continue;
                        first ??= name;
                        if (string.Equals(role, "Owner", StringComparison.OrdinalIgnoreCase)) owner = name;
                    }
                    author = owner ?? first;
                }
            }
        }
        catch (Exception ex)
        {
            // 取不到作者不影响详情页其它内容，静默降级
            Logger.Warn($"[modrinth] 作者查询失败：{ex.Message}");
        }
        var debugTeam = root.TryGetProperty("team", out var _t) ? _t.GetString() : null;
        Logger.Debug($"[modrinth] 作者解析：{id} -> team={debugTeam ?? "(无)"} author={author ?? "(空)"}");

        var detail = new
        {
            id,
            title = root.TryGetProperty("title", out var t2) ? t2.GetString() : id,
            description = root.TryGetProperty("description", out var dsc) ? dsc.GetString() : "",
            author,
            body = root.TryGetProperty("body", out var b) ? b.GetString() : "",
            iconUrl = root.TryGetProperty("icon_url", out var ic2) ? ic2.GetString() : null,
            downloads = root.TryGetProperty("downloads", out var dl) && dl.TryGetInt64(out var dlv) ? dlv : 0,
            follows = root.TryGetProperty("followers", out var fo) && fo.TryGetInt64(out var fv) ? fv : 0,
            updated = root.TryGetProperty("updated", out var up) ? up.GetString() : null,
            license = root.TryGetProperty("license", out var lc) && lc.TryGetProperty("name", out var ln) ? ln.GetString() : null,
            categories = StrListOf(root, "categories"),
            // Modrinth 返回的 game_versions 是**升序**（实测 Fabric API 前六条是
            // 18w49a/18w50a/19w02a…），直接渲染意味着标签墙最前面全是 2018 年的快照。
            // 用户关心的是"支不支持我现在的版本"，所以倒序，把最新的放最前。
            gameVersions = VersionComparer.SortDescending(StrListOf(root, "game_versions")),
            loaders = StrListOf(root, "loaders"),
            // project_type 让前端知道这是模组还是光影，用于图标与文案
            projectType = root.TryGetProperty("project_type", out var pt) ? pt.GetString() : null,
            dependencies,
        };
        return detail;
    }

    private static List<string> StrListOf(JsonElement obj, string name)
    {
        var list = new List<string>();
        if (obj.ValueKind == JsonValueKind.Object &&
            obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array)
        {
            foreach (var i in v.EnumerateArray())
                if (i.ValueKind == JsonValueKind.String) list.Add(i.GetString()!);
        }
        return list;
    }

    /* ==================================================================
       参数读取工具
       ================================================================== */

    private static string? Str(JsonElement p, string name)
        => p.ValueKind == JsonValueKind.Object && p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    private static int? Int(JsonElement p, string name)
    {
        if (p.ValueKind != JsonValueKind.Object || !p.TryGetProperty(name, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i)) return i;
        if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out var s)) return s;
        return null;
    }

    private static long? Long(JsonElement p, string name)
    {
        if (p.ValueKind != JsonValueKind.Object || !p.TryGetProperty(name, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var i)) return i;
        if (v.ValueKind == JsonValueKind.String && long.TryParse(v.GetString(), out var s)) return s;
        return null;
    }

    private static bool? Bool(JsonElement p, string name)
    {
        if (p.ValueKind != JsonValueKind.Object || !p.TryGetProperty(name, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => v.GetString()?.Equals("true", StringComparison.OrdinalIgnoreCase),
            _ => null,
        };
    }

    private static List<string> StrArray(JsonElement p, string name)
    {
        var list = new List<string>();
        if (p.ValueKind == JsonValueKind.Object && p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array)
        {
            foreach (var i in v.EnumerateArray())
                if (i.ValueKind == JsonValueKind.String) list.Add(i.GetString()!);
        }
        return list;
    }

    private static IDictionary<string, object?> ToDict(JsonElement p)
    {
        var dict = new Dictionary<string, object?>();
        if (p.ValueKind != JsonValueKind.Object || !p.TryGetProperty("patch", out var patch)) return dict;
        if (patch.ValueKind != JsonValueKind.Object) return dict;

        foreach (var prop in patch.EnumerateObject())
        {
            dict[prop.Name] = prop.Value.ValueKind switch
            {
                JsonValueKind.String => prop.Value.GetString(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Number => prop.Value.TryGetInt32(out var i) ? i : prop.Value.GetDouble(),
                _ => null,
            };
        }
        return dict;
    }

    /// <summary>
    /// 记录界面抛出的异常。
    /// 用 Warn/Error 两级：脚本加载失败（多半是路径或打包问题）算 Error，
    /// 运行期报错也算 Error —— 这两种都会直接导致界面不可用。
    /// </summary>
    private static object LogUiError(JsonElement p)
    {
        var kind = Str(p, "kind") ?? "error";
        var message = Str(p, "message") ?? "(无消息)";
        var source = Str(p, "source");
        var line = Long(p, "line") ?? 0;
        var stack = Str(p, "stack");

        var where = string.IsNullOrEmpty(source) ? "" : $" @ {source}:{line}";
        var detail = string.IsNullOrWhiteSpace(stack) ? "" : $"\n{stack}";

        switch (kind)
        {
            case "longtask":
                Logger.Warn($"[ui] {message}（{Str(p, "stack")}）");
                break;
            case "script-load":
                Logger.Error(new Exception(message), $"[ui] 脚本加载失败：{source}");
                break;
            case "rejection":
                Logger.Error(new Exception(message), $"[ui] 未处理的 Promise 拒绝{detail}");
                break;
            default:
                Logger.Error(new Exception(message), $"[ui] 界面报错{where}{detail}");
                break;
        }

        return new { ok = true };
    }

    /// <summary>界面主动上报的一条日志。level: info | warn | error。</summary>
    private static object LogUi(JsonElement p)
    {
        var message = Str(p, "message") ?? "";
        if (message.Length == 0) return new { ok = false };

        switch ((Str(p, "level") ?? "info").ToLowerInvariant())
        {
            case "error": Logger.Error(new Exception(message), "[ui]"); break;
            case "warn": Logger.Warn($"[ui] {message}"); break;
            default: Logger.Info($"[ui] {message}"); break;
        }
        return new { ok = true };
    }

    /// <summary>
    /// 批量安装一份依赖清单。
    ///
    /// 必须**顺序**装而不是并行：前置没落地就开始装依赖它的模组，
    /// 虽然这里的安装只是写文件、不会立即报错，但失败时用户看到的状态会乱，
    /// 而且任务进度会跳动得无法理解。顺序装的总耗时对几个文件来说可以忽略。
    /// </summary>
    private static object StartInstallPlan(JsonElement p)
    {
        var instanceId = Long(p, "instanceId") ?? 0;
        var kind = Str(p, "kind") ?? "mod";
        var gameVersion = Str(p, "gameVersion");
        var loader = Str(p, "loader");

        if (instanceId <= 0) return OpResult.Fail("请先选择要安装到的实例。");
        if (!p.TryGetProperty("items", out var itemsEl) || itemsEl.ValueKind != JsonValueKind.Array)
            return OpResult.Fail("缺少要安装的清单。");

        var items = new List<(string ProjectId, string? VersionId, string Title)>();
        foreach (var it in itemsEl.EnumerateArray())
        {
            var pid = Str(it, "projectId") ?? Str(it, "id");
            if (string.IsNullOrWhiteSpace(pid)) continue;
            items.Add((pid!, Str(it, "versionId"), Str(it, "title") ?? pid!));
        }

        if (items.Count == 0) return OpResult.Fail("清单里没有可安装的项。");

        var taskId = TaskCenter.Create($"安装 {items.Count} 项内容");
        _ = Task.Run(async () =>
        {
            var ok = 0;
            try
            {
                TaskCenter.Start(taskId, "准备中…");

                for (var i = 0; i < items.Count; i++)
                {
                    var (pid, vid, title) = items[i];
                    TaskCenter.Report(taskId, t => t.Detail = $"{title}（{i + 1}/{items.Count}）");

                    try
                    {
                        var r = await ModrinthClient.InstallAsync(
                            pid, vid, instanceId, kind, gameVersion, loader, CancellationToken.None);
                        if (r is null || r.Ok) ok++;
                        else Logger.Warn($"[deps] 安装 {title} 失败：{r.Message}");
                    }
                    catch (Exception ex)
                    {
                        // 单项失败不中断整批：能装上的先装上，最后统一告知结果
                        Logger.Warn($"[deps] 安装 {title} 异常：{ex.Message}");
                    }

                    TaskCenter.Progress(taskId, i + 1, items.Count, 0, 0, $"{title}（{i + 1}/{items.Count}）");
                }

                TaskCenter.Complete(taskId, ok == items.Count
                    ? $"{ok} 项全部安装完成"
                    : $"完成 {ok}/{items.Count} 项，其余可在内容列表中重试");

                Emit("content.changed", new { instanceId });
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "[deps] 批量安装失败");
                TaskCenter.Fail(taskId, ex.Message);
            }
        });

        return new { taskId, count = items.Count };
    }
}
