using System.Runtime.InteropServices;

namespace StarCore.Core.Platform;

/// <summary>
/// 运行平台抽象。
///
/// 为什么要有这一层：StarCore 的内核（版本安装、加载器、账户、Modrinth、下载）
/// 与平台无关，只有少数几处是 Windows 专有的 —— Java 探测的注册表、
/// 可执行文件名、以及"在文件管理器里显示文件"这种桌面能力。
/// 把它们收进接口，将来做安卓版时只要实现一个 <see cref="IRuntimePlatform"/>，
/// 内核可以一行不改地复用。
///
/// **刻意保持最小**：接口越大，跨平台时要实现的空壳就越多。
/// 现在只有 6 个成员，每一个都是当前代码里真实用到、且确实有平台差异的。
/// </summary>
public interface IRuntimePlatform
{
    /// <summary>平台标识，小写。windows / linux / macos / android。</summary>
    string Name { get; }

    /// <summary>是否为桌面平台。影响"能否在文件管理器里打开""能否执行外部进程"这类能力。</summary>
    bool IsDesktop { get; }

    /// <summary>
    /// 用于启动游戏的 Java 可执行文件名。
    /// Windows 上是 javaw.exe（不弹控制台窗口），其它平台就是 java。
    /// </summary>
    string JavaExecutable { get; }

    /// <summary>
    /// 改写某个进程主窗口的标题。返回是否改写成功。
    /// 平台不支持时返回 false（安卓没有窗口标题这回事）。
    /// </summary>
    bool TrySetWindowTitle(int pid, string title);

    /// <summary>备选的 Java 可执行文件名（找不到主名时用它）。</summary>
    string JavaExecutableFallback { get; }

    /// <summary>
    /// 版本 JSON 里"原生库分类符"的平台后缀，例如 <c>windows</c> / <c>windows-arm64</c>。
    ///
    /// 为什么平台抽象必须提供它：新版的 Minecraft 版本 JSON 把原生库写成
    /// <c>org.lwjgl:lwjgl:3.4.3:natives-windows</c> 这样的名字，**平台信息在库名里**，
    /// 而且 x64 与 arm64 两条目的 <c>rules</c> 完全一样（都是 allow windows）——
    /// 靠 rules 根本区分不出来。只能由启动器按自己的架构去比对分类符，
    /// 否则会把 arm64 的 dll 也当成可用的下下来。
    /// </summary>
    string NativePlatformSuffix { get; }

    /// <summary>
    /// Java 探测的「根目录 + 深度」列表。
    /// 各平台的 JDK 安装位置差异很大，交给平台自己决定。
    /// </summary>
    IEnumerable<(string Root, int Depth)> JavaProbeRoots();

    /// <summary>
    /// 从系统注册表/配置数据库里找出 JDK 安装位置。
    /// Android 与 Linux 上没有这个概念，返回空集合即可。
    /// </summary>
    IEnumerable<string> JavaFromSystemDatabase();

    /// <summary>在系统文件管理器里定位一个文件。桌面平台才有意义。</summary>
    bool RevealInFileManager(string path);
}

/// <summary>
/// 平台实现注册点。
///
/// 内核不直接 new WindowsPlatform —— 那样内核就依赖了具体实现，
/// 也就无法在非 Windows 上编译。宿主（Desktop / Android）在启动时注册自己那份实现。
/// 未注册时退回一个"仅含通用行为"的空实现，保证内核的单元测试能跑。
/// </summary>
public static class PlatformServices
{
    private static readonly object Gate = new();
    private static IRuntimePlatform? _current;

    /// <summary>
    /// 当前平台实现。**首次访问时自动探测**。
    ///
    /// 刻意不设"中性实现"作默认值：那种默认值不会崩，只会静默少做事，
    /// 于是任何一个忘记调 <see cref="AutoDetect"/> 的新入口
    /// （无界面工具、将来的安卓宿主、单元测试）都会表现为
    /// "Java 一个都探测不到"—— 一个极难归因的症状。
    /// 探测本身只是读一个 OS 常量，没有理由推迟到"有人记得调"。
    /// </summary>
    public static IRuntimePlatform Current
    {
        get
        {
            var c = _current;
            if (c is not null) return c;
            lock (Gate) return _current ??= Detect();
        }
        set
        {
            lock (Gate) _current = value ?? Detect();
        }
    }

    /// <summary>按当前运行环境重新探测一次。只有需要强制切换时才用。</summary>
    public static void AutoDetect()
    {
        lock (Gate) _current = Detect();
    }

    private static IRuntimePlatform Detect() =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? new WindowsPlatform()
            : new NeutralPlatform();
}

/// <summary>
/// 不含任何平台特性的实现。
/// 用于单元测试与非 Windows 构建 —— 它不会崩，只是探测能力弱一些。
/// </summary>
internal sealed class NeutralPlatform : IRuntimePlatform
{
    public string Name => RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "macos"
                        : RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? "linux"
                        : "unknown";

    public bool IsDesktop => true;
    public string JavaExecutable => "java";
    public string JavaExecutableFallback => "java";

    public string NativePlatformSuffix => Name switch
    {
        "macos" => RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "macos-arm64" : "macos",
        "linux" => "linux",
        _ => "",
    };

    public IEnumerable<(string Root, int Depth)> JavaProbeRoots()
    {
        // 通用约定：几个常见的位置。Unix 上 JDK 通常装在 /usr/lib/jvm、/opt 下
        yield return ("/usr/lib/jvm", 2);
        yield return ("/opt", 2);
        yield return (Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".jdks"), 1);
        yield return (Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Java", "JavaVirtualMachines"), 2);
    }

    public IEnumerable<string> JavaFromSystemDatabase() => Array.Empty<string>();

    public bool RevealInFileManager(string path)
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                System.Diagnostics.Process.Start("open", new[] { "-R", path });
                return true;
            }

            var dir = File.Exists(path) ? Path.GetDirectoryName(path) : path;
            if (string.IsNullOrEmpty(dir)) return false;
            System.Diagnostics.Process.Start("xdg-open", new[] { dir! });
            return true;
        }
        catch { return false; }
    }
    public bool TrySetWindowTitle(int pid, string title) => false;
}
