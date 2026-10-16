namespace StarCore.Core.App;

/// <summary>
/// 本地文件到前端可用 URL 的映射约定。
///
/// 前端跑在 WebView2 里，受 CSP 限制不能直接读 file://；同时我们又必须能显示
/// 用户本地的皮肤、背景图、导出图标等。解决办法是在宿主里把两个目录挂到同一个
/// 虚拟主机名下（见 Desktop 的 MainWindow），内核只负责生成 URL。
/// </summary>
public static class WebAssets
{
    /// <summary>
    /// 两个虚拟主机名。
    /// WebView2 的 SetVirtualHostNameToFolderMapping 只能把一个主机名映射到一个目录，
    /// 而我们需要同时暴露"界面资源"和"用户数据"两个目录，因此拆成两个主机。
    /// </summary>
    public const string AppHost = "app.starcore.local";
    public const string DataHost = "data.starcore.local";

    /// <summary>界面静态资源（wwwroot 下）。</summary>
    public static string App(string relative) => $"https://{AppHost}/{relative.TrimStart('/')}";

    /// <summary>数据目录下的文件（皮肤缓存、导出图标…）。</summary>
    public static string Data(string absoluteOrRelative)
    {
        var full = StarCorePaths.Resolve(absoluteOrRelative);
        var rel = System.IO.Path.GetRelativePath(StarCorePaths.DataRoot, full).Replace('\\', '/');
        // 出现 .. 说明文件在数据目录之外，不能也不应该暴露出去
        if (rel.StartsWith("..", StringComparison.Ordinal)) return "";
        return $"https://{DataHost}/{Uri.EscapeDataString(rel).Replace("%2F", "/")}";
    }

    /// <summary>若已是 http(s) 地址则原样返回，否则尝试映射为本地资源 URL。</summary>
    public static string? Normalize(string? urlOrPath)
    {
        if (string.IsNullOrWhiteSpace(urlOrPath)) return null;
        if (urlOrPath.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            urlOrPath.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return urlOrPath;
        var u = Data(urlOrPath);
        return string.IsNullOrEmpty(u) ? null : u;
    }
}
