using System.Diagnostics;
using System.Reflection;

namespace StarCore.Core.App;

/// <summary>
/// 全部文件系统位置的唯一来源。
/// 任何地方需要路径都必须从这里取，禁止在别处拼接 —— 这样"便携模式"、
/// "自定义数据目录"这类需求只需改这一处。
/// </summary>
public static class StarCorePaths
{
    /// <summary>便携模式：可执行文件同级存在 StarCore.portable 文件时，数据写入程序目录。</summary>
    public static bool IsPortable { get; private set; }

    public static string AppRoot { get; private set; } = AppContext.BaseDirectory;

    /// <summary>用户数据根目录（设置、账户、日志、实例元数据）。</summary>
    public static string DataRoot { get; private set; } = "";

    /// <summary>游戏实例的实际存放位置（每个实例一个子目录）。</summary>
    public static string InstancesRoot => Path.Combine(DataRoot, "instances");

    public static string SettingsFile => Path.Combine(DataRoot, "settings.json");
    public static string AccountsFile => Path.Combine(DataRoot, "accounts.json");
    public static string VersionsFile => Path.Combine(DataRoot, "versions.json");
    public static string JavasFile => Path.Combine(DataRoot, "java.json");
    public static string LogsRoot => Path.Combine(DataRoot, "logs");
    public static string CacheRoot => Path.Combine(DataRoot, "cache");
    public static string AssetsCacheRoot => Path.Combine(CacheRoot, "assets");

    /// <summary>版本安装到实例后，各实例自己的版本目录。</summary>
    public static string VersionDir(string instancePath) => Path.Combine(instancePath, "versions");
    public static string LibraryDir(string instancePath) => Path.Combine(instancePath, "libraries");
    public static string AssetDir(string instancePath) => Path.Combine(instancePath, "assets");

    public static void Initialize()
    {
        AppRoot = AppContext.BaseDirectory;

        IsPortable = File.Exists(Path.Combine(AppRoot, "StarCore.portable"));

        DataRoot = IsPortable
            ? Path.Combine(AppRoot, "StarCoreData")
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "StarCore");

        foreach (var dir in new[] { DataRoot, InstancesRoot, LogsRoot, CacheRoot, AssetsCacheRoot })
        {
            try { Directory.CreateDirectory(dir); }
            catch (Exception ex) { Debug.WriteLine($"[paths] 创建目录失败 {dir}: {ex.Message}"); }
        }
    }

    /// <summary>
    /// 用设置里的自定义数据目录覆盖默认位置。
    /// 只接受绝对路径；目录不存在时会尝试创建，创建失败则保持原位置并留日志。
    /// </summary>
    public static bool OverrideDataRoot(string dir)
    {
        if (string.IsNullOrWhiteSpace(dir) || !Path.IsPathRooted(dir)) return false;
        try
        {
            Directory.CreateDirectory(dir);
            foreach (var sub in new[] { "instances", "logs", "cache" })
                Directory.CreateDirectory(Path.Combine(dir, sub));

            DataRoot = dir;
            Logger.Info($"[paths] 数据目录已切换为：{dir}");
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, $"[paths] 切换数据目录失败：{dir}");
            return false;
        }
    }

    /// <summary>把相对路径解析为绝对路径（相对 DataRoot）。</summary>
    public static string Resolve(string path)
        => string.IsNullOrWhiteSpace(path) ? DataRoot
           : Path.IsPathRooted(path) ? path
           : Path.GetFullPath(Path.Combine(DataRoot, path));

    public static string ProductVersion
    {
        get
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version;
            return v is null ? "1.0.0" : $"{v.Major}.{v.Minor}.{v.Build}";
        }
    }
}
