using System.Text;
using StarCore.Core.App;

namespace StarCore.Core.Services;

/// <summary>下载源种类。</summary>
public enum SourceKind
{
    /// <summary>Mojang / 各项目官方源。</summary>
    Official,
    /// <summary>BMCLAPI 镜像。</summary>
    Bmclapi,
    /// <summary>用户自定义前缀。</summary>
    Custom,
}

/// <summary>
/// 统一下载源解析。
///
/// 为什么必须有这一层，而不是在各处写死 URL：
///   1. Mojang 与各加载器的源在国内直连经常超时，BMCLAPI 是社区维护的完整镜像；
///   2. 镜像并非"所有文件都有"—— Quilt 的 meta、部分 Forge 的构件在 BMCL 上是 404。
///      所以每次请求都要能拿到「主源 + 备用源」两个候选，失败自动回落官方。
///      只做前缀替换而不做回落，一定会出现"某些文件永远下不下来"的玄学问题。
///   3. 用户可能自建镜像（企业内网、校园网），所以还要支持自定义前缀。
///
/// 全部映射集中在 <see cref="Rules"/> 一张表里，改动只需要改一处。
/// </summary>
public static class DownloadSource
{
    private const string BmclRoot = "https://bmclapi2.bangbang93.com";

    /// <summary>
    /// 前缀映射表。
    /// **顺序有意义**：先长后短，否则 `https://maven.minecraftforge.net` 会被
    /// 更短的规则先命中而拼出错误的地址。
    /// </summary>
    private static readonly (string From, string To)[] Rules =
    {
        // —— Minecraft 本体 ——
        ("https://piston-meta.mojang.com",       BmclRoot),
        ("https://launchermeta.mojang.com",      BmclRoot),
        ("https://launcher.mojang.com",          BmclRoot),
        ("https://piston-data.mojang.com",       BmclRoot),
        ("https://resources.download.minecraft.net", $"{BmclRoot}/assets"),

        // —— 库文件：BMCL 统一挂在 /maven 下 ——
        ("https://libraries.minecraft.net",      $"{BmclRoot}/maven"),
        ("https://maven.fabricmc.net",           $"{BmclRoot}/maven"),
        ("https://maven.minecraftforge.net",     $"{BmclRoot}/maven"),
        ("https://maven.neoforged.net/releases", $"{BmclRoot}/maven"),

        // —— 加载器元数据 ——
        ("https://meta.fabricmc.net",            $"{BmclRoot}/fabric-meta"),
        ("https://meta.legacyfabric.net",        $"{BmclRoot}/legacy-fabric-meta"),
        // 注意：BMCL 并未镜像 Quilt 的 meta（实测返回 COMMON_NO_SUCH_OBJECT），
        // 这里依然给出映射，靠 Candidates() 的官方回落兜底。
        ("https://meta.quiltmc.org",             $"{BmclRoot}/quilt-meta"),

        // —— 其它常用源 ——
        ("https://files.minecraftforge.net",     $"{BmclRoot}/maven"),
        ("https://api.modrinth.com",             "https://mod.mcimirror.top/modrinth"),
    };

    public static SourceKind Kind
    {
        get
        {
            var raw = (SettingsService.Current.DownloadSource ?? "official").Trim().ToLowerInvariant();
            return raw switch
            {
                "mirror" or "bmclapi" => SourceKind.Bmclapi,
                "custom"              => SourceKind.Custom,
                _                     => SourceKind.Official,
            };
        }
    }

    /// <summary>给界面显示用的当前源名称。</summary>
    public static string CurrentName => Kind switch
    {
        SourceKind.Bmclapi => "BMCLAPI 镜像",
        SourceKind.Custom   => $"自定义（{SettingsService.Current.CustomDownloadSource}）",
        _                   => "官方源",
    };

    /// <summary>把官方 URL 映射成当前源下的 URL。已经是镜像地址时原样返回。</summary>
    public static string Map(string officialUrl)
    {
        if (string.IsNullOrWhiteSpace(officialUrl)) return officialUrl;

        return Kind switch
        {
            SourceKind.Official => officialUrl,
            SourceKind.Bmclapi   => ReplaceByRules(officialUrl, BmclRoot),
            SourceKind.Custom    => MapCustom(officialUrl),
            _                    => officialUrl,
        };
    }

    private static string ReplaceByRules(string url, string _)
    {
        foreach (var (from, to) in Rules)
        {
            if (url.StartsWith(from, StringComparison.OrdinalIgnoreCase))
                return to + url[from.Length..];
        }
        return url;
    }

    /// <summary>
    /// 自定义源：只替换 Mojang 的几个根域名。
    /// 不自作聪明去猜用户镜像的目录结构 —— 猜错会得到 404，比不做更糟。
    /// 界面上已明确写清「仅替换 Mojang 根域名」。
    /// </summary>
    private static string MapCustom(string url)
    {
        var root = (SettingsService.Current.CustomDownloadSource ?? "").Trim().TrimEnd('/');
        if (root.Length == 0) return url;

        foreach (var host in new[]
                 {
                     "https://piston-meta.mojang.com",
                     "https://launchermeta.mojang.com",
                     "https://launcher.mojang.com",
                     "https://piston-data.mojang.com",
                     "https://resources.download.minecraft.net",
                     "https://libraries.minecraft.net",
                 })
        {
            if (url.StartsWith(host, StringComparison.OrdinalIgnoreCase))
                return root + url[host.Length..];
        }
        return url;
    }

    /// <summary>
    /// 返回该 URL 的候选列表：主源优先，官方源兜底。
    /// 官方源模式下只有一个元素，调用方按顺序试即可，不必分支判断。
    /// </summary>
    public static string[] Candidates(string officialUrl)
    {
        if (string.IsNullOrWhiteSpace(officialUrl)) return Array.Empty<string>();

        var primary = Map(officialUrl);
        if (string.Equals(primary, officialUrl, StringComparison.OrdinalIgnoreCase))
            return new[] { primary };

        return new[] { primary, officialUrl };
    }

    /* ==================================================================
       带回落的请求
       ------------------------------------------------------------------
       镜像不是所有文件都有。这里把「逐个候选尝试 + 官方兜底」封装好，
       调用方只管要数据，不用关心当前是什么源、要不要重试。
       ================================================================== */

    /// <summary>取文本（JSON / XML）。默认限制 8MB，防止被人塞一个超大页面把内存吃光。</summary>
    public static Task<string> GetStringAsync(string officialUrl, CancellationToken ct = default)
        => GetStringAsync(officialUrl, 8 * 1024 * 1024, ct);

    public static async Task<string> GetStringAsync(string officialUrl, int maxBytes, CancellationToken ct = default)
    {
        var bytes = await GetBytesAsync(officialUrl, maxBytes, ct);
        return Encoding.UTF8.GetString(bytes);
    }

    /// <summary>取字节。逐个候选尝试，全部失败才抛。</summary>
    public static async Task<byte[]> GetBytesAsync(string officialUrl, CancellationToken ct = default)
        => await GetBytesAsync(officialUrl, 0, ct);

    public static async Task<byte[]> GetBytesAsync(string officialUrl, int maxBytes, CancellationToken ct = default)
    {
        var candidates = Candidates(officialUrl);
        Exception? last = null;

        for (var i = 0; i < candidates.Length; i++)
        {
            var url = candidates[i];
            try
            {
                using var res = await Http.Client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
                res.EnsureSuccessStatusCode();

                // 长度预检：metadata 接口偶尔会返回一个 HTML 错误页，
                // 直接当 JSON 解析会得到一堆看不懂的异常。这里提前拦掉。
                if (maxBytes > 0 && res.Content.Headers.ContentLength is { } len && len > maxBytes)
                    throw new InvalidOperationException($"响应过大（{len} 字节），可能是错误页面：{url}");

                var data = await res.Content.ReadAsByteArrayAsync(ct);

                // 镜像返回内容但其实是错误页（部分镜像会用 200 返回 HTML 404 页）
                if (maxBytes > 0 && data.Length > 0 && LooksLikeHtml(data))
                    throw new InvalidOperationException($"响应看起来是 HTML 而非数据：{url}");

                if (i > 0) Logger.Warn($"[source] 主源失败，已回落到备用源：{url}");
                return data;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                last = ex;
                if (i == 0 && candidates.Length > 1)
                    Logger.Debug($"[source] 主源不可用（{url}）：{ex.Message}，尝试官方源");
            }
        }

        throw new InvalidOperationException($"所有下载源均不可达：{officialUrl}", last);
    }

    /// <summary>下载到文件，同样带源回落。已存在且大小一致时直接跳过。</summary>
    /// <param name="maxParallel">单文件内部分块并发数；1 表示单流。</param>
    /// <param name="onDelta">实时字节增量回调，用于显示速度。</param>
    public static async Task<long> DownloadAsync(
        string officialUrl, string dest, CancellationToken ct = default, long? expectSize = null,
        int maxParallel = 1, Action<long>? onDelta = null)
    {
        // 本地已完整就不必联网 —— 这是让"重装/修复"变得廉价的关键。
        if (File.Exists(dest))
        {
            var len = new FileInfo(dest).Length;
            if (expectSize is > 0 && len == expectSize) return 0;
            if (expectSize is null && len > 0) return 0;
        }

        var candidates = Candidates(officialUrl);
        Exception? last = null;

        for (var i = 0; i < candidates.Length; i++)
        {
            try
            {
                var written = await Http.DownloadAsync(candidates[i], dest, ct, expectSize, maxParallel, onDelta);
                if (i > 0) Logger.Warn($"[source] 主源失败，已回落到备用源下载：{officialUrl}");
                return written;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                last = ex;
                if (i == 0 && candidates.Length > 1)
                    Logger.Debug($"[source] 主源下载失败，尝试官方源：{officialUrl}");
            }
        }

        throw new InvalidOperationException($"所有下载源均无法下载：{officialUrl}", last);
    }

    private static bool LooksLikeHtml(byte[] data)
    {
        // 只看开头一小段，避免对大文件做无谓的解码
        var n = Math.Min(data.Length, 64);
        for (var i = 0; i < n; i++)
        {
            var b = data[i];
            if (b is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n' or 0xEF or 0xBB or 0xBF) continue;
            return b == (byte)'<';
        }
        return false;
    }
}
