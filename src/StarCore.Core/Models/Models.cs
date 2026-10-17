namespace StarCore.Core.Models;

/* ============================================================================
   跨层数据模型。
   字段命名统一按 JSON camelCase 暴露给前端（由 App.Json.Options 处理），
   这里的属性名保持 PascalCase 以符合 C# 约定。
   ========================================================================== */

/// <summary>运行环境与版本信息。</summary>
public sealed class AppInfo
{
    public string Name { get; init; } = "StarCore";
    public string Version { get; init; } = "1.0.0";
    public string CoreVersion { get; init; } = "1.0.0";
    public string OsName { get; init; } = "";
    public string Arch { get; init; } = "";
    public string Locale { get; init; } = "zh-CN";
    public string DataDir { get; init; } = "";
    public long TotalMemoryMb { get; init; }
    public bool Portable { get; init; }
    public string DotNetVersion { get; init; } = "";
}

/// <summary>用户设置。全部字段都有默认值，缺失即用默认，不做"未设置"区分。</summary>
public sealed class Settings
{
    // 外观
    public string Theme { get; set; } = "light";           // light | dark | system
    public string Accent { get; set; } = "terracotta";     // 见前端 theme.js 的 ACCENTS
    public string Background { get; set; } = "dusk";       // 见前端 theme.js 的 BACKGROUNDS

    // —— 自定义媒体（背景图 / 背景视频 / 背景音乐）——
    // 存的是导入到数据目录后的虚拟地址，不是用户的原始路径 ——
    // 原始文件可能被移动或删除，那样背景会突然变空白。
    public string BgImageUrl { get; set; } = "";
    public string BgVideoUrl { get; set; } = "";
    public string BgMusicUrl { get; set; } = "";

    /// <summary>是否播放背景音乐。默认关闭 —— 突然出声是很冒犯的体验。</summary>
    public bool BgMusicEnabled { get; set; }

    /// <summary>背景音乐音量，0~1。</summary>
    public double BgMusicVolume { get; set; } = 0.45;

    /// <summary>动效强度：off | subtle | full。默认取中间值。</summary>
    public string UiMotion { get; set; } = "subtle";

    /// <summary>渲染开销：low | mid | high。检测到软件光栅化时前端会自动降到 low。</summary>
    public string UiPerf { get; set; } = "high";

    // 区域
    public string Language { get; set; } = "zh-CN";

    // 启动行为
    public bool KeepLauncherOpen { get; set; } = true;
    public bool CloseToTray { get; set; }
    public bool LaunchCloseLauncher { get; set; }
    /// <summary>
    /// 更新源仓库，格式 "用户名/仓库名"（GitHub）。
    /// 走 GitHub Releases 的公开接口，因此不需要任何密钥 ——
    /// 这也是选它当"更新服务器"的原因：零运维、零成本、自带版本归档。
    ///
    /// ⚠️ 这里必须和实际仓库一致。曾经写的是占位符 StarCore-Project/StarCore，
    /// 结果每次检查更新都报"所有下载源均不可达"，看起来像网络问题，
    /// 实际是仓库根本不存在。改这个值时请顺手确认
    /// https://api.github.com/repos/&lt;值&gt;/releases/latest 能返回 200。
    /// </summary>
    public string UpdateRepo { get; set; } = "Yonyuan123/StarCore";

    public bool AutoUpdate { get; set; } = true;

    // 下载
    /// <summary>允许同一实例同时开多个游戏进程（PCL/HMCL 都有）。默认关：两个进程写同一个存档会互相踩。</summary>
    public bool AllowMultiInstance { get; set; }

    // —— 游戏语言（第 5 批附加）——
    /// <summary>
    /// 安装 / 启动时自动把游戏语言调成该值（MC 语言代码，如 zh_cn）。
    /// "auto" = 跟随界面语言。玩家不用再进游戏设置里翻语言列表。
    /// </summary>
    public string GameLanguage { get; set; } = "auto";
    /// <summary>关闭后不再自动改写 options.txt 的 lang，游戏保持自己的语言。</summary>
    public bool AutoGameLanguage { get; set; } = true;

    // —— 启动前自动备份（第 5 批）——
    /// <summary>启动前备份有改动的存档（PCL 的招牌安全感）。保留最近 5 份。</summary>
    public bool AutoBackupWorlds { get; set; } = true;

    // —— 启动体验（第 6 批）——
    /// <summary>启动前的倒计时确认（秒）。0 = 不等待直接启动。</summary>
    public int LaunchCountdown { get; set; }
    /// <summary>
    /// 游戏窗口标题模板，支持 {instance} {version} {loader} 变量。留空则用游戏默认标题。
    /// 实现方式是启动后改写游戏进程主窗口的标题（MC 本身没有这个参数）。
    /// </summary>
    public string GameWindowTitle { get; set; } = "";
    /// <summary>追加给游戏进程的环境变量，一行一个 KEY=VALUE。</summary>
    public string LaunchEnvVars { get; set; } = "";

    public string DownloadSource { get; set; } = "official"; // official | mirror | custom

    /// <summary>注册的游戏目录列表（HMCL 的多目录管理）。
    /// 实例的 GameDirMode = shared 时从中选一个作为运行目录。</summary>
    public List<string> GameDirs { get; set; } = new();
    public string CustomDownloadSource { get; set; } = "";
    public int MaxThreads { get; set; } = 32;

    // —— 资源详情翻译 ——
    /// <summary>翻译后端：free（免费机翻，零配置）| llm（AI 翻译，需要 Key）。</summary>
    public string TranslateProvider { get; set; } = "free";

    /// <summary>OpenAI 兼容的接口地址。留空时按 DeepSeek 默认地址。</summary>
    public string TranslateEndpoint { get; set; } = "";

    /// <summary>翻译用的 API Key。仅本地保存，不随日志或诊断报告导出。</summary>
    public string TranslateApiKey { get; set; } = "";

    /// <summary>翻译用的模型名。</summary>
    public string TranslateModel { get; set; } = "";

    // —— 第三方皮肤库 ——
    /// <summary>
    /// 皮肤站地址（LittleSkin / Blessing Skin 等）。
    /// 留空表示只用 Mojang 官方皮肤。填上之后可以读取该站的皮肤与披风。
    /// </summary>
    public string SkinSiteUrl { get; set; } = "";
    public string Proxy { get; set; } = "";

    // 账户
    /// <summary>
    /// 微软登录用的 Azure 应用 client id。
    /// 必须由使用者自行注册：微软已下线上古时期那个公共 id，
    /// 现在任何第三方启动器都要有自己的应用身份，否则会得到 AADSTS700016。
    /// </summary>
    public string MicrosoftClientId { get; set; } = "";

    // 数据
    public string DataRoot { get; set; } = "";
    public long LastInstanceId_Deprecated { get; set; }
}

/// <summary>账户。凭据与资料同构，便于统一渲染列表。</summary>
public sealed class AccountInfo
{
    public string Uuid { get; set; } = "";
    public string UserName { get; set; } = "";

    /// <summary>Microsoft | Offline | Authlib | YggdrasilConnect</summary>
    public string ProfileType { get; set; } = "Offline";

    public string? SkinUrl { get; set; }
    /// <summary>前端头像用的 8×8 正面贴图（由皮肤贴图裁出，data URI 或本地缓存路径）。</summary>
    public string? SkinFace { get; set; }
    public string? CapeUrl { get; set; }
    /// <summary>classic | slim</summary>
    public string SkinModel { get; set; } = "classic";

    // —— 以下为凭据，仅正版账户使用。
    //    落盘时需要保存，但**绝不**出现在发给前端的 JSON 里 ——
    //    由 StarCoreHost 统一投影（ToPublic），而不是依赖序列化忽略，
    //    这样即使将来有人新增了序列化路径也不会意外泄漏。
    public string? AccessToken { get; set; }
    public string? RefreshToken { get; set; }
    public DateTimeOffset? TokenExpiresAt { get; set; }

    public DateTimeOffset AddedAt { get; set; } = DateTimeOffset.Now;
}

/// <summary>游戏实例。</summary>
public sealed class InstanceInfo
{
    public long Id { get; set; }
    public string Name { get; set; } = "新实例";

    /// <summary>基础游戏版本 id，例如 1.20.1；未安装时为空。</summary>
    public string? VersionId { get; set; }

    /// <summary>vanilla | forge | neoforge | fabric | quilt | optifine | liteloader</summary>
    public string Loader { get; set; } = "vanilla";
    public string? LoaderVersion { get; set; }

    public string Path { get; set; } = "";

    // —— 运行参数 ——
    public int MemoryMb { get; set; } = 4096;
    public string? JavaPath { get; set; }
    public bool Fullscreen { get; set; }
    public int WindowWidth { get; set; } = 854;
    public int WindowHeight { get; set; } = 480;
    public string JvmArgs { get; set; } = "";
    public string GameArgs { get; set; } = "";
    public string? PreLaunchCommand { get; set; }
    public string? PostLaunchCommand { get; set; }

    // —— 派生统计，由 InstanceService 在列出时填充 ——
    public int ModCount { get; set; }
    public int ResourcePackCount { get; set; }
    public int ShaderCount { get; set; }
    public int WorldCount { get; set; }
    public long SizeBytes { get; set; }
    public DateTimeOffset? LastPlayedAt { get; set; }
    public long TotalPlayTimeMs { get; set; }
    public bool Installed { get; set; }

    // —— 展示用图标（相对 wwwroot/assets/img/ 的路径，由 App.Icons 产出）——
    /// <summary>实例封面：装了加载器就是加载器的方块，否则是版本类型的方块。
    /// 用户设置了自定义封面（CustomCover）时优先显示自定义。</summary>
    public string? Cover { get; set; }
    /// <summary>加载器图标。前端据此在角标与列表里显示。</summary>
    public string? LoaderIcon { get; set; }

    // —— 游戏目录（PCL 的"版本隔离"开关）——
    /// <summary>isolated = 实例自己的目录（默认，mods/存档完全独立）；
    /// shared = 与其他实例共用一个目录（SharedGameDir）。</summary>
    public string GameDirMode { get; set; } = "isolated";
    /// <summary>共用模式下实际使用的工作目录。来自设置里注册的游戏目录列表。</summary>
    public string? SharedGameDir { get; set; }

    /// <summary>用户自定义封面，相对数据目录（如 covers/9.png）。优先于方块图标。</summary>
    public string? CustomCover { get; set; }

    /// <summary>隐藏的实例不出现在列表与切换菜单里（可在"显示已隐藏"中找回）。</summary>
    public bool Hidden { get; set; }

    /// <summary>
    /// 启动后直连的服务器地址（host 或 host:port）。
    /// 仅 1.20+ 支持（走 --quickPlayMultiplayer，不碰 servers.dat 的 NBT）。
    /// </summary>
    public string? QuickPlayServer { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
}

/// <summary>Mojang 版本清单里的一条。</summary>
public sealed class VersionSummary
{
    public string Id { get; set; } = "";
    public string Type { get; set; } = "release";
    public DateTimeOffset? ReleaseTime { get; set; }
    public string Url { get; set; } = "";

    /// <summary>该版本对应的方块图标（相对 wwwroot/assets/img/ 的路径）。</summary>
    public string? Icon { get; set; }

    /// <summary>是否为愚人节版本。清单里的 type 区分不出来，需要单独标记。</summary>
    public bool AprilFools { get; set; }
}

/// <summary>Java 运行时。</summary>
public sealed class JavaInfo
{
    public string Path { get; set; } = "";
    public string? Version { get; set; }
    public int Major { get; set; }
    public string Arch { get; set; } = "";
    public string Vendor { get; set; } = "";
    public bool Valid { get; set; } = true;
    public bool Managed { get; set; }
}

/// <summary>后台任务。前端状态条与下载页都消费它。</summary>
public sealed class TaskInfo
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";

    /// <summary>queued | running | done | failed | cancelled</summary>
    public string State { get; set; } = "queued";

    public double Progress { get; set; }
    public double? Speed { get; set; }
    public string? Detail { get; set; }
    public string? Error { get; set; }
    public long TotalBytes { get; set; }
    public long DoneBytes { get; set; }
    public int TotalFiles { get; set; }
    public int DoneFiles { get; set; }
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.Now;
    public bool Cancellable { get; set; } = true;
}

/// <summary>实例内的内容项（模组 / 资源包 / 光影 / 存档 / 备份）。</summary>
public sealed class ContentItem
{
    public string Name { get; set; } = "";
    public string FileName { get; set; } = "";
    public string Path { get; set; } = "";
    public long SizeBytes { get; set; }
    public bool Disabled { get; set; }
    public List<string> Tags { get; set; } = new();
    public string? IconUrl { get; set; }
    public DateTimeOffset? ModifiedAt { get; set; }
}

/// <summary>日志文件条目。</summary>
public sealed class LogFileInfo
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    /// <summary>launch | crash | launcher</summary>
    public string Kind { get; set; } = "launch";
    public long SizeBytes { get; set; }
    public DateTimeOffset? ModifiedAt { get; set; }
}

/// <summary>启动状态推送。</summary>
public sealed class LaunchState
{
    public string State { get; set; } = "idle"; // idle | preparing | running | exited | crashed
    public long? InstanceId { get; set; }
    public int? Pid { get; set; }
    public int? ExitCode { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public string? Message { get; set; }
}

/// <summary>通用的"操作结果"返回，前端可据此判断是否成功与失败原因。</summary>
public sealed class OpResult
{
    public bool Ok { get; set; }
    public string? Message { get; set; }
    public string? Path { get; set; }
    public object? Data { get; set; }

    public static OpResult Success(string? message = null, string? path = null)
        => new() { Ok = true, Message = message, Path = path };

    public static OpResult Fail(string message) => new() { Ok = false, Message = message };
}
