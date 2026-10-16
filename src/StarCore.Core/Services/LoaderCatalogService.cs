using System.Text.Json;
using StarCore.Core.App;

namespace StarCore.Core.Services;

/// <summary>
/// 加载器版本目录。
///
/// 为什么需要它：之前的安装流程只会去问"最新版是哪个"，用户没有办法指定
/// 「1.20.1 + Forge 47.2.0」这种精确组合，而模组生态里版本配套是硬约束 ——
/// 装错一个小版本，模组就直接崩。PCL / HMCL 都提供完整的下拉列表，这里补齐。
///
/// 设计原则：
///   · 每个加载器的上游结构都不一样，逐个适配但统一出口格式；
///   · 网络失败一律返回空列表 + error 说明，绝不抛异常 ——
///     加载器列表拿不到不该让整个启动器不可用；
///   · 统一走 <see cref="DownloadSource"/>，天然获得镜像加速与官方回落。
/// </summary>
public static class LoaderCatalogService
{
    /// <summary>单个加载器最多返回多少条版本，避免把下拉列表撑爆。</summary>
    private const int MaxVersions = 60;

    public static async Task<object> ListVersionsAsync(string loader, string gameVersion, CancellationToken ct = default)
    {
        loader = (loader ?? "fabric").Trim().ToLowerInvariant();
        gameVersion = (gameVersion ?? "").Trim();

        if (gameVersion.Length == 0)
            return Wrap(loader, gameVersion, Array.Empty<object>(), "请先选择游戏版本。");

        // 这几个加载器不依赖具体游戏版本，可以脱离 gameVersion 给出列表
        if (loader is "liteloader")
            return LiteLoader(gameVersion);

        try
        {
            return loader switch
            {
                "fabric"       => await FabricLike("fabric", gameVersion, ct),
                "legacyfabric" => await FabricLike("legacyfabric", gameVersion, ct),
                "quilt"        => await Quilt(gameVersion, ct),
                "forge"        => await Forge(gameVersion, ct),
                "neoforge"     => await NeoForge(gameVersion, ct),
                "optifine"     => Wrap(loader, gameVersion, Array.Empty<object>(),
                    "OptiFine 没有公开的下载接口，请从 optifine.net 手动下载安装器。若只是想要光影，建议改用 Iris + Sodium 组合。"),
                _ => Wrap(loader, gameVersion, Array.Empty<object>(), $"未知的加载器：{loader}"),
            };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Logger.Warn($"[loader] 获取 {loader} 版本列表失败：{ex.Message}");
            return Wrap(loader, gameVersion, Array.Empty<object>(),
                $"无法获取 {loader} 的版本列表，可让启动器自动选择。");
        }
    }

    private static object Wrap(string loader, string gameVersion, object[] versions, string? error = null)
        => new { loader, gameVersion, versions, error, source = DownloadSource.CurrentName };

    private static object Item(string id, string label, bool recommended = false, string? note = null)
        => new { id, label, recommended, note };

    /* ==================================================================
       Fabric / Legacy Fabric / Quilt —— 结构同源，只换域名
       ================================================================== */

    private static async Task<object> FabricLike(string loader, string gameVersion, CancellationToken ct)
    {
        // meta.fabricmc.net 的路径结构在 BMCL 镜像上完全一致（fabric-meta 前缀），
        // legacy fabric 同理。所以这里只换域名，路径不变。
        var host = loader == "legacyfabric" ? "https://meta.legacyfabric.net" : "https://meta.fabricmc.net";
        var url = $"{host}/v2/versions/loader/{Uri.EscapeDataString(gameVersion)}";

        var json = await DownloadSource.GetStringAsync(url, ct);
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            return Wrap(loader, gameVersion, Array.Empty<object>(), "上游返回了非预期的结构。");

        // 先收齐（含 stable 标记），再按版本号排序，最后截断 —— 顺序不能反。
        var entries = new List<(string Id, bool Stable)>();
        foreach (var entry in doc.RootElement.EnumerateArray())
        {
            if (!entry.TryGetProperty("loader", out var l) ||
                !l.TryGetProperty("version", out var ver) || ver.ValueKind != JsonValueKind.String)
                continue;

            var stable = !l.TryGetProperty("stable", out var st) || st.ValueKind != JsonValueKind.False;
            entries.Add((ver.GetString()!, stable));
        }

        var recommendedTaken = false;
        var list = VersionComparer.SortDescending(entries, e => e.Id)
            .Take(MaxVersions)
            .Select(e =>
            {
                // "推荐"标给**第一个稳定版**，而不是列表第一条 ——
                // 上游第一条经常是 beta，无脑推荐等于把用户往测试版上引。
                var recommend = e.Stable && !recommendedTaken;
                if (recommend) recommendedTaken = true;
                return Item(e.Id, e.Id, recommended: recommend, note: e.Stable ? null : "测试版");
            })
            .ToArray();

        // 极端情况：整个列表都是测试版，那还是把最新的标成推荐
        if (!recommendedTaken && list.Length > 0)
        {
            var fid = list[0].GetType().GetProperty("id")!.GetValue(list[0])!.ToString()!;
            list[0] = Item(fid, fid, recommended: true, note: "测试版");
        }

        return Wrap(loader, gameVersion, list);
    }

    private static async Task<object> Quilt(string gameVersion, CancellationToken ct)
    {
        // 实测 BMCL 并未镜像 Quilt 的 meta（返回 COMMON_NO_SUCH_OBJECT），
        // 因此这里主源就是官方；DownloadSource 的回落机制在这里不产生额外请求。
        var url = $"https://meta.quiltmc.org/v3/versions/loader/{Uri.EscapeDataString(gameVersion)}";
        var json = await DownloadSource.GetStringAsync(url, ct);
        using var doc = JsonDocument.Parse(json);

        var list = new List<object>();

        if (doc.RootElement.ValueKind == JsonValueKind.Array)
        {
            // 先全部收齐、再排序、最后才截断。
            // 之前的顺序是「边遍历边 Add、遇到 60 条就 break」——那是**上游顺序**的前 60 条，
            // 排序根本没机会生效（实测下拉框前三项就是 0.20.0-beta.9 / -beta.7 / -beta.8，
            // 而真正该推荐的 0.24.0 被埋在后面）。
            var ids = new List<string>();
            foreach (var entry in doc.RootElement.EnumerateArray())
            {
                var id = entry.TryGetProperty("loader", out var l) && l.TryGetProperty("version", out var v)
                    ? v.GetString()
                    : entry.TryGetProperty("version", out var v2) ? v2.GetString() : null;
                if (!string.IsNullOrEmpty(id)) ids.Add(id);
            }

            var taken = false;
            var sorted = VersionComparer.SortDescending(ids).Take(MaxVersions).Select(id =>
            {
                // 含 -beta / -pre / -rc 的都算预发布，不打推荐标
                var stable = !System.Text.RegularExpressions.Regex.IsMatch(
                    id, @"-(beta|pre|rc|alpha|snapshot)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                var recommend = stable && !taken;
                if (recommend) taken = true;
                return Item(id, id, recommended: recommend, note: stable ? null : "测试版");
            }).ToArray();

            // 整个版本线都只有预发布时，至少把最新的标成推荐，总得给个默认值
            if (!taken && sorted.Length > 0)
            {
                var first = sorted[0];
                var fid = first.GetType().GetProperty("id")!.GetValue(first)!.ToString()!;
                sorted[0] = Item(fid, fid, recommended: true, note: "测试版");
            }

            return Wrap("quilt", gameVersion, sorted);
        }

        // 该游戏版本在 Quilt 上不存在时，上游会返回 404，
        // 此时明确告诉用户"这个版本 Quilt 不支持"，比给一个空下拉有用。
        return Wrap("quilt", gameVersion, Array.Empty<object>(),
            $"Quilt 似乎不支持 Minecraft {gameVersion}，请换一个游戏版本。");
    }

    /* ==================================================================
       Forge
       ================================================================== */

    private static async Task<object> Forge(string gameVersion, CancellationToken ct)
    {
        // 首选 BMCL：它有按游戏版本索引好的列表，一次请求就够，不必解析 HTML 或 XML。
        try
        {
            var url = $"https://bmclapi2.bangbang93.com/forge/minecraft/{Uri.EscapeDataString(gameVersion)}";
            var json = await DownloadSource.GetStringAsync(url, ct);
            using var doc = JsonDocument.Parse(json);

            if (doc.RootElement.ValueKind == JsonValueKind.Array && doc.RootElement.GetArrayLength() > 0)
            {
                var all = new List<string>();
                foreach (var e in doc.RootElement.EnumerateArray())
                {
                    if (e.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String)
                        all.Add(v.GetString()!);
                }

                // **不要直接 reverse 上游数组**：实测 BMCL 的顺序并不严格递增
                // （出现过 47.4.27 / 47.4.28 / 47.4.24 交错），reverse 之后最新版会夹在中间。
                // 必须按版本号自己排一遍。
                var taken = false;
                var list = VersionComparer.SortDescending(all).Take(MaxVersions).Select(id =>
                {
                    // Forge 的 <build> 版本号偶有 -beta 后缀，同样不该被推荐
                    var stable = !id.Contains("-beta", StringComparison.OrdinalIgnoreCase);
                    var recommend = stable && !taken;
                    if (recommend) taken = true;
                    return Item(id, id, recommended: recommend, note: stable ? null : "测试版");
                }).ToArray();
                return Wrap("forge", gameVersion, list);
            }
        }
        catch (Exception ex)
        {
            Logger.Debug($"[loader] Forge 的 BMCL 列表不可用，回落官方 maven：{ex.Message}");
        }

        // 回落官方 maven-metadata.xml
        var meta = await DownloadSource.GetStringAsync(
            "https://maven.minecraftforge.net/net/minecraftforge/forge/maven-metadata.xml", ct);
        var ids = VersionComparer.SortDescending(
            ParseMavenVersions(meta)
                .Where(v => v.StartsWith(gameVersion + "-", StringComparison.Ordinal))
                .Select(v => v[(gameVersion.Length + 1)..])
                .Distinct());

        if (ids.Count == 0)
        {
            // 再退一步：只给官方推荐的几条，至少有得装
            return Wrap("forge", gameVersion, Array.Empty<object>(),
                $"未能列出 Forge {gameVersion} 的版本，可让启动器自动选择推荐版本。");
        }

        return Wrap("forge", gameVersion,
            ids.Take(MaxVersions).Select((id, i) => Item(id, id, recommended: i == 0)).ToArray());
    }

    /* ==================================================================
       NeoForge
       ================================================================== */

    private static async Task<object> NeoForge(string gameVersion, CancellationToken ct)
    {
        // 关键：maven.neoforged.net 在部分网络下完全不可达，所以 BMCL 是主源而非备选。
        var url = $"https://bmclapi2.bangbang93.com/neoforge/list/{Uri.EscapeDataString(gameVersion)}";
        var json = await DownloadSource.GetStringAsync(url, ct);
        using var doc = JsonDocument.Parse(json);

        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            return Wrap("neoforge", gameVersion, Array.Empty<object>(), "上游返回了非预期的结构。");

        var labels = new List<string>();
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            var raw = e.TryGetProperty("rawVersion", out var r) ? r.GetString()
                    : e.TryGetProperty("version", out var v) ? v.GetString() : null;
            if (string.IsNullOrEmpty(raw)) continue;

            // rawVersion 形如 "neoforge-21.1.1"，去掉前缀给用户看更干净
            labels.Add(raw.StartsWith("neoforge-", StringComparison.OrdinalIgnoreCase)
                ? raw["neoforge-".Length..]
                : raw);
        }

        // 同样按版本号排序而不是信上游顺序；并补上推荐标记
        // （之前这里忘了标 recommended，界面上下拉框里一个"推荐"都没有）
        var taken = false;
        var list = VersionComparer.SortDescending(labels).Take(MaxVersions).Select(label =>
        {
            // NeoForge 用 <mc>.<patch> 编号，-beta 后缀的是预发布
            var stable = !label.Contains("-beta", StringComparison.OrdinalIgnoreCase);
            var recommend = stable && !taken;
            if (recommend) taken = true;
            return Item(label, label, recommended: recommend, note: stable ? null : "测试版");
        }).ToArray();

        return Wrap("neoforge", gameVersion, list);
    }

    /* ==================================================================
       LiteLoader —— 早已停止维护，只给一份静态清单
       ================================================================== */

    private static object LiteLoader(string gameVersion)
    {
        string[] supported = { "1.12.2", "1.12.1", "1.12", "1.11.2", "1.10.2", "1.9.4", "1.8.9", "1.7.10" };

        if (!supported.Contains(gameVersion, StringComparer.OrdinalIgnoreCase))
        {
            return Wrap("liteloader", gameVersion, Array.Empty<object>(),
                "LiteLoader 已多年未更新，只支持 1.12.2 及更早版本。新版本请改用 Fabric 或 NeoForge。");
        }

        return Wrap("liteloader", gameVersion,
            new object[] { Item(gameVersion, gameVersion, recommended: true, note: "LiteLoader 最终版本") });
    }

    /* ==================================================================
       工具
       ================================================================== */

    /// <summary>从 maven-metadata.xml 里抽出所有 &lt;version&gt; 文本。</summary>
    private static List<string> ParseMavenVersions(string xml)
    {
        var result = new List<string>();
        var idx = 0;
        while (true)
        {
            var open = xml.IndexOf("<version>", idx, StringComparison.OrdinalIgnoreCase);
            if (open < 0) break;
            var close = xml.IndexOf("</version>", open, StringComparison.OrdinalIgnoreCase);
            if (close < 0) break;

            var value = xml[(open + "<version>".Length)..close].Trim();
            if (value.Length > 0) result.Add(value);
            idx = close + 1;
        }
        return result;
    }
}
