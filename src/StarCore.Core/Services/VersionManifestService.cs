using System.Text.Json;
using StarCore.Core.App;
using StarCore.Core.Models;

namespace StarCore.Core.Services;

/// <summary>
/// Mojang 官方版本清单。
/// 清单本身很小（约 500KB），但拉取频率高，所以落盘缓存并附时间戳；
/// 前端据此显示"多久之前更新的"，用户也能手动强制刷新。
/// </summary>
public static class VersionManifestService
{
    private const string ManifestUrl = "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json";

    /// <summary>超过这个时长即视为陈旧，界面会提示刷新。</summary>
    public static readonly TimeSpan FreshFor = TimeSpan.FromHours(6);

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static List<VersionSummary> _versions = new();
    private static DateTimeOffset? _cachedAt;

    public static IReadOnlyList<VersionSummary> Versions => _versions;
    public static DateTimeOffset? CachedAt => _cachedAt;
    public static bool Stale => _cachedAt is null || DateTimeOffset.Now - _cachedAt.Value > FreshFor;

    /// <summary>读取磁盘缓存（启动时调用，不发起网络请求）。</summary>
    public static void LoadCache()
    {
        try
        {
            if (!File.Exists(StarCorePaths.VersionsFile)) return;
            using var doc = JsonDocument.Parse(File.ReadAllText(StarCorePaths.VersionsFile));
            var root = doc.RootElement;

            if (root.TryGetProperty("fetchedAt", out var at) && at.TryGetDateTimeOffset(out var dt)) _cachedAt = dt;
            if (root.TryGetProperty("versions", out var vs))
            {
                _versions = JsonSerializer.Deserialize<List<VersionSummary>>(vs.GetRawText(), Json.Options) ?? new();
            }
            // 旧版本写下的缓存里没有 Icon / AprilFools 字段，
            // 载入后补一遍，避免用户升级后看到的是一排空白图标。
            foreach (var v in _versions)
            {
                v.AprilFools = Icons.IsAprilFools(v.Id);
                v.Icon ??= Icons.ForVersion(v.Id, v.Type);
            }

            Logger.Info($"[version] 已载入本地清单缓存，共 {_versions.Count} 个版本");
        }
        catch (Exception ex)
        {
            Logger.Warn($"[version] 读取版本清单缓存失败：{ex.Message}");
            _versions = new();
        }
    }

    /// <summary>从官方源刷新清单并落盘。</summary>
    public static async Task<List<VersionSummary>> RefreshAsync(CancellationToken ct = default)
    {
        await Gate.WaitAsync(ct);
        try
        {
            Logger.Info("[version] 正在拉取官方版本清单…");
            var json = await DownloadSource.GetStringAsync(ManifestUrl, ct);

            using var doc = JsonDocument.Parse(json);
            var latest = doc.RootElement.TryGetProperty("latest", out var l) ? l : default;

            var list = new List<VersionSummary>();
            if (doc.RootElement.TryGetProperty("versions", out var arr))
            {
                foreach (var v in arr.EnumerateArray())
                {
                    var summary = new VersionSummary
                    {
                        Id = v.GetProperty("id").GetString() ?? "",
                        Type = v.TryGetProperty("type", out var t) ? t.GetString() ?? "release" : "release",
                        Url = v.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "",
                    };
                    if (v.TryGetProperty("releaseTime", out var rt) && rt.TryGetDateTimeOffset(out var dto))
                        summary.ReleaseTime = dto;

                    // 图标与愚人节标记在解析阶段一次算好并落盘缓存，
                    // 避免每次打开下载页为 900 多个版本重算一遍。
                    summary.AprilFools = Icons.IsAprilFools(summary.Id);
                    summary.Icon = Icons.ForVersion(summary.Id, summary.Type);

                    // 用 latest 段把最新正式版/快照标出来，界面上会加一个高亮标记
                    if (latest.ValueKind == JsonValueKind.Object)
                    {
                        if (summary.Id == latest.GetProperty("release").GetString()) summary.Type = "release";
                    }
                    list.Add(summary);
                }
            }

            _versions = list;
            _cachedAt = DateTimeOffset.Now;
            Persist();
            Logger.Info($"[version] 清单已更新，共 {list.Count} 个版本");
            return list;
        }
        finally { Gate.Release(); }
    }

    public static VersionSummary? Find(string id)
        => _versions.FirstOrDefault(v => string.Equals(v.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>拉取某个版本的详细 JSON（含 libraries / assets / mainClass）。</summary>
    public static async Task<JsonElement?> FetchDetailAsync(string id, CancellationToken ct = default)
    {
        var summary = Find(id) ?? throw new InvalidOperationException($"版本清单中不存在 {id}");
        if (string.IsNullOrEmpty(summary.Url)) throw new InvalidOperationException($"版本 {id} 缺少清单地址");

        var json = await DownloadSource.GetStringAsync(summary.Url, ct);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private static void Persist()
    {
        try
        {
            var payload = new { fetchedAt = _cachedAt, versions = _versions };
            File.WriteAllText(StarCorePaths.VersionsFile, JsonSerializer.Serialize(payload, Json.Pretty));
        }
        catch (Exception ex)
        {
            Logger.Warn($"[version] 写入清单缓存失败：{ex.Message}");
        }
    }
}
