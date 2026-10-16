using System.Text.Json;
using StarCore.Core.App;
using StarCore.Core.Models;

namespace StarCore.Core.Services;

/// <summary>
/// 前置依赖解析。
///
/// 为什么单独成文件：这是"点一下就装好"体验的关键一环。
/// 装一个 Sodium 需要 Fabric API，装 Iris 又需要 Sodium —— 用户不知道这些，
/// 装完发现游戏起不来只会觉得启动器有毛病。PCL / HMCL 都会自动把前置带上。
///
/// 三条设计原则：
///   1. **只自动装 required**。optional 与 incompatible 一律不碰 ——
///      自动帮用户装"可选"的东西是越权，装了不兼容的更糟。
///   2. **前置必须排在依赖它的东西之前**。用后序遍历天然得到这个顺序，
///      反过来的话 Forge 会因为前置还没落地而报错。
///   3. **已经装过的不重复装**。按文件名判断（模组文件名含版本号，通常唯一），
///      这是最便宜的判据，也不需要打开 jar 读元数据。
/// </summary>
public static class DependencyResolver
{
    /// <summary>依赖链最大深度。正常模组不会超过 3 层，超过基本是数据有环。</summary>
    private const int MaxDepth = 6;

    /// <summary>单次解析最多返回多少项，防止某个坏数据把队列撑爆。</summary>
    private const int MaxItems = 60;

    public sealed class PlanItem
    {
        public string ProjectId { get; set; } = "";
        public string VersionId { get; set; } = "";
        public string VersionNumber { get; set; } = "";
        public string Title { get; set; } = "";
        public string? IconUrl { get; set; }
        public string FileName { get; set; } = "";
        public string Url { get; set; } = "";
        public long Size { get; set; }
        /// <summary>是根项目本身还是它的前置。</summary>
        public bool IsRoot { get; set; }
        /// <summary>目标实例里已经有同名的文件了。</summary>
        public bool AlreadyInstalled { get; set; }
        /// <summary>谁要求装它（用于界面上说明"为什么列在这里"）。</summary>
        public string? RequiredBy { get; set; }
    }

    public static async Task<object> PlanAsync(
        string projectId, string? versionId, string? gameVersion, string? loader,
        long instanceId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(projectId))
            return OpResult.Fail("缺少资源 ID。");

        var ordered = new List<PlanItem>();
        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var resolved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            await WalkAsync(
                projectId, versionId, null, null,
                gameVersion, loader, instanceId,
                ordered, visiting, resolved, 0, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Logger.Warn($"[deps] 解析前置失败：{ex.Message}");
            return OpResult.Fail($"解析前置依赖失败：{ex.Message}");
        }

        // 补上标题与图标（逐个查太慢，批量查一次）
        await FillMetaAsync(ordered, ct);

        var missing = ordered.Where(i => !i.AlreadyInstalled).ToList();

        return new
        {
            items = ordered,
            total = ordered.Count,
            missing = missing.Count,
            alreadyInstalled = ordered.Count - missing.Count,
            // 让界面能直接说"需要额外安装 N 个前置"
            dependencies = missing.Count(i => !i.IsRoot),
        };
    }

    /// <summary>后序遍历：先把所有前置走完，再把自己加进结果，天然得到"前置在前"的顺序。</summary>
    private static async Task WalkAsync(
        string projectId, string? pinnedVersionId, string? requiredBy, string? requiredByTitle,
        string? gameVersion, string? loader, long instanceId,
        List<PlanItem> ordered, HashSet<string> visiting, HashSet<string> resolved,
        int depth, CancellationToken ct)
    {
        if (depth > MaxDepth) return;
        if (ordered.Count >= MaxItems) return;
        if (resolved.Contains(projectId)) return;

        // 数据里存在互相依赖的环（少见但真实存在），碰到就直接返回，别无限递归
        if (!visiting.Add(projectId)) return;

        try
        {
            var version = await PickVersionAsync(projectId, pinnedVersionId, gameVersion, loader, ct);
            if (version?.Primary is null)
            {
                Logger.Debug($"[deps] {projectId} 没有与当前版本/加载器兼容的文件，跳过");
                return;
            }

            // 先递归处理前置
            foreach (var dep in version.Dependencies)
            {
                if (!string.Equals(dep.DependencyType, "required", StringComparison.OrdinalIgnoreCase))
                    continue;   // optional / incompatible 一律不自动装
                if (string.IsNullOrWhiteSpace(dep.ProjectId)) continue;

                await WalkAsync(
                    dep.ProjectId, string.IsNullOrWhiteSpace(dep.VersionId) ? null : dep.VersionId,
                    projectId, version.Name,
                    gameVersion, loader, instanceId,
                    ordered, visiting, resolved, depth + 1, ct);
            }

            resolved.Add(projectId);

            ordered.Add(new PlanItem
            {
                ProjectId = projectId,
                VersionId = version.Id,
                VersionNumber = version.VersionNumber,
                Title = version.Name,
                FileName = version.Primary.FileName,
                Url = version.Primary.Url,
                Size = version.Primary.Size,
                IsRoot = requiredBy is null,
                RequiredBy = requiredBy,
                AlreadyInstalled = IsAlreadyInstalled(instanceId, version.Primary.FileName),
            });
        }
        finally
        {
            visiting.Remove(projectId);
        }
    }

    private static async Task<ModrinthClient.VersionInfo?> PickVersionAsync(
        string projectId, string? pinnedVersionId, string? gameVersion, string? loader, CancellationToken ct)
    {
        var versions = await ModrinthClient.VersionsAsync(projectId, gameVersion, loader, ct);
        if (versions.Count == 0)
        {
            // 用当前版本/加载器过滤后什么都没剩，退回不过滤再试一次。
            // 前置模组经常只给"1.20+"这种宽泛声明，严格过滤会把它们全部滤掉。
            versions = await ModrinthClient.VersionsAsync(projectId, null, null, ct);
            if (versions.Count == 0) return null;
        }

        if (!string.IsNullOrWhiteSpace(pinnedVersionId))
        {
            var pinned = versions.FirstOrDefault(v => v.Id == pinnedVersionId);
            if (pinned is not null) return pinned;
        }

        // 优先正式版，其次最新的
        return versions.FirstOrDefault(v => string.Equals(v.VersionType, "release", StringComparison.OrdinalIgnoreCase))
               ?? versions[0];
    }

    /// <summary>目标实例里是否已经有同名文件。按文件名判断是最便宜的判据，且够准。</summary>
    private static bool IsAlreadyInstalled(long instanceId, string fileName)
    {
        if (instanceId <= 0 || string.IsNullOrWhiteSpace(fileName)) return false;

        try
        {
            var inst = InstanceService.Get(instanceId);
            if (inst is null) return false;

            foreach (var kind in new[] { "mods", "resourcepacks", "shaderpacks" })
            {
                var dir = InstanceService.ContentFolder(inst.Path, kind);
                if (!Directory.Exists(dir)) continue;

                if (File.Exists(Path.Combine(dir, fileName))) return true;
                if (File.Exists(Path.Combine(dir, fileName + ".disabled"))) return true;
            }
        }
        catch { }

        return false;
    }

    /// <summary>批量补齐标题与图标，避免界面上出现一排光秃秃的 project id。</summary>
    private static async Task FillMetaAsync(List<PlanItem> items, CancellationToken ct)
    {
        var ids = items.Select(i => i.ProjectId).Distinct().ToList();
        if (ids.Count == 0) return;

        var meta = new Dictionary<string, (string Title, string? Icon)>(StringComparer.OrdinalIgnoreCase);

        try
        {
            for (var i = 0; i < ids.Count; i += 100)
            {
                var batch = ids.Skip(i).Take(100).ToList();
                var url = "https://api.modrinth.com/v2/projects?ids=" +
                          Uri.EscapeDataString("[" + string.Join(",", batch.Select(x => '"' + x + '"')) + "]");

                var text = await Http.GetStringAsync(url, ct);
                using var doc = JsonDocument.Parse(text);
                if (doc.RootElement.ValueKind != JsonValueKind.Array) continue;

                foreach (var p in doc.RootElement.EnumerateArray())
                {
                    var id = p.TryGetProperty("id", out var pid) ? pid.GetString() : null;
                    if (id is null) continue;
                    meta[id] = (
                        p.TryGetProperty("title", out var t) ? t.GetString() ?? id : id,
                        p.TryGetProperty("icon_url", out var ic) ? ic.GetString() : null);
                }
            }
        }
        catch (Exception ex)
        {
            // 补不到元数据不影响安装，标题退回 project id 即可
            Logger.Debug($"[deps] 补全标题失败：{ex.Message}");
        }

        foreach (var item in items)
        {
            if (meta.TryGetValue(item.ProjectId, out var m))
            {
                item.Title = m.Title;
                item.IconUrl = m.Icon;
            }
            else if (string.IsNullOrWhiteSpace(item.Title))
            {
                item.Title = item.ProjectId;
            }
        }
    }
}
