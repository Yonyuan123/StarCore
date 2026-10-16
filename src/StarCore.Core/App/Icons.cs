namespace StarCore.Core.App;

/// <summary>
/// 图标语义映射。
///
/// 沿用 PCL 的设计：**用「方块」隐喻版本与加载器的类型**。
/// 这不是随手贴图，每个方块都在表意：
///   草方块（正式版）   —— 原汁原味、标准
///   命令方块（快照）   —— 实验性、开发中
///   圆石（旧版）       —— 最古老、粗糙
///   金块（愚人节）     —— 华而不实
///   红石块（异常）     —— 通电警告，出问题了
///   铁砧（Forge）      —— 打铁锻造
///   草径（OptiFine）   —— 被"优化"过的地面
///   蛋（LiteLoader）   —— 轻量、待孵化
///
/// 图标本体在 wwwroot/assets/img/blocks/ 下（提取自 PCL-CE，Apache-2.0，见该目录 SOURCES.md）。
/// 这里只负责"什么情况用哪一个"，不关心文件在哪 —— 前端把返回值拼成资源路径。
/// </summary>
public static class Icons
{
    private const string FallbackVersion = "blocks/version-unknown.png";
    private const string FallbackLoader = "blocks/version-release.png";

    /// <summary>
    /// 按版本类型与版本号挑方块图标。
    /// 优先看 type（这是 Mojang 清单里的权威字段），
    /// 只有在 type 不足以区分时（比如都是 release）才去看版本号。
    /// </summary>
    public static string ForVersion(string? versionId, string? type)
    {
        // 愚人节版本在清单里同样是 release/snapshot，必须先判掉
        if (IsAprilFools(versionId)) return "blocks/version-fool.png";

        switch ((type ?? "").ToLowerInvariant())
        {
            case "release":
                return "blocks/version-release.png";
            case "snapshot":
            case "pending":
                return "blocks/version-snapshot.png";
            case "old_beta":
            case "old_alpha":
                return "blocks/version-old.png";
        }

        // 清单里没有 type（例如加载器合成的版本 JSON），退化为按版本号形态判断
        var id = versionId ?? "";
        if (id.Length == 0) return FallbackVersion;
        if (id.StartsWith("a", StringComparison.OrdinalIgnoreCase) ||
            id.StartsWith("b", StringComparison.OrdinalIgnoreCase) ||
            id.StartsWith("rd-", StringComparison.OrdinalIgnoreCase) ||
            id.StartsWith("c", StringComparison.OrdinalIgnoreCase))
            return "blocks/version-old.png";   // alpha / beta / classic
        if (id.Contains('w') && id.Any(char.IsDigit)) return "blocks/version-snapshot.png"; // 形如 25w14a

        return "blocks/version-release.png";
    }

    /// <summary>按加载器挑图标。未知加载器回落到原版草方块，而不是问号图。</summary>
    public static string ForLoader(string? loader) => (loader ?? "").ToLowerInvariant() switch
    {
        "forge" => "blocks/loader-forge.png",
        "neoforge" => "blocks/loader-neoforge.png",
        "fabric" => "blocks/loader-fabric.png",
        // PCL 里 LegacyFabric 也复用 Fabric 的图标：它本质就是同一套工具链
        "legacyfabric" => "blocks/loader-fabric.png",
        "quilt" => "blocks/loader-quilt.png",
        "optifine" => "blocks/loader-optifine.png",
        "optifabric" => "blocks/loader-optifabric.png",
        "liteloader" => "blocks/loader-liteloader.png",
        "litemod" => "blocks/loader-litemod.png",
        "cleanroom" => "blocks/loader-cleanroom.png",
        _ => FallbackLoader,
    };

    /// <summary>
    /// 实例列表里显示的封面。
    /// 与 PCL 一致：**装了加载器就显示加载器的方块**，因为那才是这个实例真正的"身份"；
    /// 基础版本号会以文字形式显示在旁边，不会丢信息。
    /// </summary>
    public static string ForInstance(string? versionId, string? type, string? loader)
    {
        var l = (loader ?? "").ToLowerInvariant();
        if (l.Length > 0 && l != "vanilla") return ForLoader(l);
        return ForVersion(versionId, type);
    }

    /// <summary>
    /// 判断是否为愚人节版本。
    /// 用硬编码名单而不是"看看是不是 4 月 1 日发布" —— 后者会误伤当天正常发布的版本，
    /// 而愚人节版本的数量是有限的、可枚举的。
    /// </summary>
    private static readonly HashSet<string> AprilFools = new(StringComparer.OrdinalIgnoreCase)
    {
        "15w14a", "1.rv-pre1", "3d shareware v1.34", "20w14infinite",
        "22w13oneblockatatime", "23w13a_or_b", "24w14potato", "25w14craftmine", "26w14a",
        "2.0", "2point0_red", "2point0_blue", "2point0_purple",
    };

    public static bool IsAprilFools(string? versionId)
        => !string.IsNullOrWhiteSpace(versionId) && AprilFools.Contains(versionId.Trim());
}
