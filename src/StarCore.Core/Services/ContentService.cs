using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using StarCore.Core.App;
using StarCore.Core.Models;

namespace StarCore.Core.Services;

/// <summary>
/// 实例内内容的统一管理：模组 / 资源包 / 光影 / 存档 / 数据包 / 备份。
///
/// 禁用采用"给文件名加 .disabled 后缀"这一社区通用做法 ——
/// 好处是游戏完全看不到它，而文件仍在原处，用户可以直接在资源管理器里改回来。
/// </summary>
public static class ContentService
{
    private const string DisabledSuffix = ".disabled";

    /// <summary>可整体启用/禁用的种类（存档与备份没有这个概念）。</summary>
    private static readonly HashSet<string> Toggleable = new(StringComparer.OrdinalIgnoreCase)
    {
        "mod", "resourcepack", "shader", "datapack",
    };

    /// <summary>需要联网解析图标的种类。</summary>
    private static readonly HashSet<string> IconableKinds = new(StringComparer.OrdinalIgnoreCase)
    {
        "mod", "resourcepack", "shader", "datapack",
    };

    /* ==================================================================
       图标解析：用文件哈希向 Modrinth 反查

       模组本身不带图标（jar 里偶尔有 logo.png，但格式五花八门且大多没有），
       最可靠的办法是拿 jar 的 SHA1 去问 Modrinth "这个文件属于哪个项目"，
       再取该项目的图标。这比按文件名猜准确得多，也不会误配。

       结果落盘缓存：同一个 jar 只会查一次，之后完全离线可用。
       ================================================================== */

    private static readonly ConcurrentDictionary<string, string> IconCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object IconCacheGate = new();
    private static bool _iconCacheLoaded;

    private static string IconCacheFile => Path.Combine(StarCorePaths.CacheRoot, "modrinth-file-icons.json");

    private static void EnsureIconCacheLoaded()
    {
        if (_iconCacheLoaded) return;
        lock (IconCacheGate)
        {
            if (_iconCacheLoaded) return;
            _iconCacheLoaded = true;
            try
            {
                if (!File.Exists(IconCacheFile)) return;
                var map = Json.Deserialize<Dictionary<string, string>>(File.ReadAllText(IconCacheFile));
                if (map is null) return;
                foreach (var (k, v) in map) IconCache[k] = v;
                Logger.Debug($"[content] 图标缓存已载入，共 {map.Count} 条");
            }
            catch (Exception ex)
            {
                Logger.Warn($"[content] 图标缓存读取失败（忽略）：{ex.Message}");
            }
        }
    }

    private static void SaveIconCache()
    {
        try
        {
            lock (IconCacheGate)
            {
                var map = IconCache.ToDictionary(kv => kv.Key, kv => kv.Value);
                Directory.CreateDirectory(StarCorePaths.CacheRoot);
                File.WriteAllText(IconCacheFile, JsonSerializer.Serialize(map, Json.Options));
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"[content] 图标缓存写入失败（忽略）：{ex.Message}");
        }
    }

    /// <summary>流式计算 SHA1。大 jar 也不能整读进内存。</summary>
    private static string Sha1OfFile(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16);
        return Convert.ToHexString(SHA1.HashData(fs)).ToLowerInvariant();
    }

    /// <summary>
    /// 带图标的扫描。
    /// 本地扫描是同步且必须成功的；图标解析是"锦上添花"，任何失败都静默降级，
    /// 绝不能让网络问题把整个内容列表搞崩。
    /// </summary>
    public static async Task<List<ContentItem>> ScanAsync(long instanceId, string kind, CancellationToken ct = default)
    {
        var items = Scan(instanceId, kind);
        if (items.Count == 0 || !IconableKinds.Contains(kind)) return items;

        try
        {
            EnsureIconCacheLoaded();

            // 1) 先算哈希，命中的直接套用缓存
            var pending = new Dictionary<string, ContentItem>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in items)
            {
                if (string.IsNullOrWhiteSpace(item.Path) || !File.Exists(item.Path)) continue;
                if (item.IconUrl is not null) continue;

                string hash;
                try { hash = Sha1OfFile(item.Path); }
                catch { continue; }

                if (IconCache.TryGetValue(hash, out var cached))
                {
                    item.IconUrl = cached;
                }
                else
                {
                    pending[hash] = item;
                }
            }

            if (pending.Count == 0) return items;

            // 2) 批量问 Modrinth：哈希 → 项目 id（一次最多 100 个）
            var projectOfHash = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var chunk in Chunk(pending.Keys.ToList(), 100))
            {
                ct.ThrowIfCancellationRequested();
                var body = JsonSerializer.Serialize(new { hashes = chunk, algorithm = "sha1" });
                using var content = new StringContent(body, Encoding.UTF8, "application/json");
                using var res = await Http.Client.PostAsync("https://api.modrinth.com/v2/version_files", content, ct);
                if (!res.IsSuccessStatusCode) continue;

                var text = await res.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(text);
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    if (prop.Value.TryGetProperty("project_id", out var pid) && pid.ValueKind == JsonValueKind.String)
                        projectOfHash[prop.Name] = pid.GetString()!;
                }
            }

            if (projectOfHash.Count == 0) return items;

            // 3) 批量取项目图标
            var iconOfProject = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var ids = projectOfHash.Values.Distinct().ToList();
            foreach (var chunk in Chunk(ids, 100))
            {
                ct.ThrowIfCancellationRequested();
                var url = "https://api.modrinth.com/v2/projects?ids=" +
                          Uri.EscapeDataString("[" + string.Join(",", chunk.Select(i => '"' + i + '"')) + "]");
                var text = await Http.GetStringAsync(url, ct);
                using var doc = JsonDocument.Parse(text);
                if (doc.RootElement.ValueKind != JsonValueKind.Array) continue;

                foreach (var proj in doc.RootElement.EnumerateArray())
                {
                    var id = proj.TryGetProperty("project_id", out var pid) ? pid.GetString()
                           : proj.TryGetProperty("id", out var i2) ? i2.GetString() : null;
                    var icon = proj.TryGetProperty("icon_url", out var ic) ? ic.GetString() : null;
                    if (id is not null && !string.IsNullOrEmpty(icon)) iconOfProject[id] = icon!;
                }
            }

            // 4) 回填 + 落缓存
            foreach (var (hash, item) in pending)
            {
                if (projectOfHash.TryGetValue(hash, out var pid) && iconOfProject.TryGetValue(pid, out var icon))
                {
                    item.IconUrl = icon;
                    IconCache[hash] = icon;
                }
            }
            SaveIconCache();
            Logger.Debug($"[content] 已解析 {iconOfProject.Count} 个项目的图标（{items.Count} 项内容）");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // 网络不可达、Modrinth 变更接口……都不该影响用户看到自己的模组列表
            Logger.Warn($"[content] 图标解析失败（已降级为无图标）：{ex.Message}");
        }

        return items;
    }

    private static IEnumerable<List<T>> Chunk<T>(List<T> source, int size)
    {
        for (var i = 0; i < source.Count; i += size)
            yield return source.GetRange(i, Math.Min(size, source.Count - i));
    }

    public static List<ContentItem> Scan(long instanceId, string kind)
    {
        var inst = InstanceService.Get(instanceId);
        if (inst is null) return new();

        var dir = InstanceService.ContentFolder(inst.Path, kind);
        if (!Directory.Exists(dir)) return new();

        var items = new List<ContentItem>();

        try
        {
            if (kind == "world")
            {
                // 存档是目录
                foreach (var d in Directory.EnumerateDirectories(dir))
                {
                    var info = new DirectoryInfo(d);
                    items.Add(new ContentItem
                    {
                        Name = info.Name,
                        FileName = info.Name,
                        Path = d,
                        SizeBytes = DirSize(d),
                        ModifiedAt = info.LastWriteTimeUtc,
                        IconUrl = ReadWorldIcon(d),
                        Tags = ReadWorldTags(d),
                    });
                }
            }
            else
            {
                foreach (var f in Directory.EnumerateFiles(dir))
                {
                    var info = new FileInfo(f);
                    var disabled = f.EndsWith(DisabledSuffix, StringComparison.OrdinalIgnoreCase);

                    // 只收本类目关心的扩展名，避免把说明文件也算进来
                    var bare = disabled ? f[..^DisabledSuffix.Length] : f;
                    var ext = Path.GetExtension(bare).ToLowerInvariant();
                    if (ext is not (".jar" or ".zip" or ".litemod")) continue;

                    items.Add(new ContentItem
                    {
                        Name = Path.GetFileNameWithoutExtension(bare),
                        FileName = Path.GetFileName(f),
                        Path = f,
                        SizeBytes = info.Length,
                        Disabled = disabled,
                        ModifiedAt = info.LastWriteTimeUtc,
                        Tags = ReadJarTags(bare),
                    });
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, $"[content] 扫描失败 kind={kind} dir={dir}");
        }

        return items
            .OrderByDescending(i => i.ModifiedAt ?? DateTimeOffset.MinValue)
            .ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>启用 / 禁用一项内容。</summary>
    public static OpResult Toggle(long instanceId, string kind, string path, bool enabled)
    {
        if (!Toggleable.Contains(kind))
            return OpResult.Fail("该类型不支持启用/禁用。");
        if (!File.Exists(path)) return OpResult.Fail("文件不存在。");

        var isDisabled = path.EndsWith(DisabledSuffix, StringComparison.OrdinalIgnoreCase);
        if (enabled && !isDisabled) return OpResult.Success();
        if (!enabled && isDisabled) return OpResult.Success();

        var target = enabled ? path[..^DisabledSuffix.Length] : path + DisabledSuffix;
        try
        {
            if (File.Exists(target))
                return OpResult.Fail($"目标文件已存在：{Path.GetFileName(target)}");

            File.Move(path, target);
            InstanceService.InvalidateStats(instanceId);
            return OpResult.Success(enabled ? "已启用" : "已禁用", target);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "[content] 切换启用状态失败");
            return OpResult.Fail($"操作失败：{ex.Message}");
        }
    }

    public static OpResult Import(long instanceId, string kind, IEnumerable<string> paths)
    {
        var inst = InstanceService.Get(instanceId);
        if (inst is null) return OpResult.Fail("实例不存在。");

        var dir = InstanceService.ContentFolder(inst.Path, kind);
        Directory.CreateDirectory(dir);

        var n = 0;
        foreach (var src in paths)
        {
            try
            {
                if (File.Exists(src))
                {
                    File.Copy(src, Path.Combine(dir, Path.GetFileName(src)), true);
                    n++;
                }
                else if (Directory.Exists(src) && kind == "world")
                {
                    var dst = Path.Combine(dir, Path.GetFileName(src));
                    CopyDir(src, dst);
                    n++;
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex, $"[content] 导入失败 {src}");
            }
        }

        return AfterWrite(instanceId,
            n > 0 ? OpResult.Success($"已导入 {n} 项") : OpResult.Fail("没有可导入的文件。"));
    }

    /// <summary>写操作成功后要把实例体积缓存清掉，否则列表里的"占用空间"会停在旧值。</summary>
    private static OpResult AfterWrite(long instanceId, OpResult r)
    {
        if (r.Ok) InstanceService.InvalidateStats(instanceId);
        return r;
    }

    public static OpResult Remove(long instanceId, string kind, string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
            else if (Directory.Exists(path)) Directory.Delete(path, true);
            else return OpResult.Fail("目标不存在。");
            InstanceService.InvalidateStats(instanceId);
            return OpResult.Success("已删除");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "[content] 删除失败");
            return OpResult.Fail($"删除失败：{ex.Message}");
        }
    }

    /* ==================================================================
       元信息提取
       ================================================================== */

    /// <summary>
    /// 从模组/资源包 jar 里读 fabric.mod.json / quilt.mod.json / pack.mcmeta，
    /// 拿到真实名称、图标与分类标签。读不到就退化为文件名，不会失败。
    /// </summary>
    private static List<string> ReadJarTags(string jarPath)
    {
        var tags = new List<string>();
        try
        {
            using var zip = ZipFile.OpenRead(jarPath);

            var fabric = zip.GetEntry("fabric.mod.json");
            if (fabric is not null)
            {
                using var s = fabric.Open();
                using var doc = JsonDocument.Parse(s);
                if (doc.RootElement.TryGetProperty("name", out var nm) && nm.ValueKind == JsonValueKind.String)
                    tags.Add(nm.GetString()!);
                if (doc.RootElement.TryGetProperty("environment", out var env) && env.ValueKind == JsonValueKind.String)
                    tags.Add(env.GetString()!.ToLowerInvariant() switch { "client" => "客户端", "server" => "服务端", _ => "通用" });
                return tags;
            }

            var mcmeta = zip.GetEntry("pack.mcmeta");
            if (mcmeta is not null)
            {
                using var s = mcmeta.Open();
                using var doc = JsonDocument.Parse(s);
                if (doc.RootElement.TryGetProperty("pack", out var pack) &&
                    pack.TryGetProperty("description", out var desc))
                {
                    var text = desc.ValueKind == JsonValueKind.String ? desc.GetString() : desc.GetRawText();
                    if (!string.IsNullOrWhiteSpace(text)) tags.Add(Trim(text!, 40));
                }
            }
        }
        catch { /* 不是标准 jar 就留空 */ }
        return tags;
    }

    private static string? ReadWorldIcon(string worldDir)
    {
        var icon = Path.Combine(worldDir, "icon.png");
        return File.Exists(icon) ? WebAssets.Data(icon) : null;
    }

    private static List<string> ReadWorldTags(string worldDir)
    {
        // 交给 WorldService 解析 level.dat（游戏模式 / 版本 / 最后游玩）。
        // 之前只给了个文件日期 —— 第 5 批把信息补全。
        try { return WorldService.PeekTags(worldDir); }
        catch { return []; }
    }

    private static string Trim(string s, int max)
    {
        s = s.Replace('\n', ' ').Replace('\r', ' ').Trim();
        return s.Length <= max ? s : s[..max] + "…";
    }

    private static long DirSize(string dir)
    {
        long total = 0;
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                try { total += new FileInfo(f).Length; } catch { }
            }
        }
        catch { }
        return total;
    }

    private static void CopyDir(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (var f in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(src, f);
            var t = Path.Combine(dst, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(t)!);
            File.Copy(f, t, true);
        }
    }
}
