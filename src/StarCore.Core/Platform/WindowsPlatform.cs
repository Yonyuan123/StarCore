using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace StarCore.Core.Platform;

/// <summary>
/// Windows 平台实现。
///
/// 这里集中了内核里所有 Windows 专有的东西 —— 之前它们散落在 JavaService 里，
/// 导致内核事实上只能在 Windows 上跑。收进来的好处是：
/// 将来做安卓版时，只要不注册这个类，内核就能在 Android 上编译并运行
/// （只是会走 <see cref="NeutralPlatform"/> 的通用探测）。
/// </summary>
internal sealed class WindowsPlatform : IRuntimePlatform
{
    public string Name => "windows";
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern bool SetWindowText(IntPtr hwnd, string text);

    /// <summary>
    /// 改写游戏进程主窗口标题。
    /// Java 进程的主窗口不是立即出现的（LWJGL 要等 JVM 起来才建窗口），
    /// 所以调用方需要在后台重试若干次，见 LaunchService.ApplyWindowTitleAsync。
    /// </summary>
    public bool TrySetWindowTitle(int pid, string title)
    {
        if (string.IsNullOrEmpty(title)) return false;
        try
        {
            using var proc = System.Diagnostics.Process.GetProcessById(pid);
            proc.Refresh();
            var hwnd = proc.MainWindowHandle;
            if (hwnd == IntPtr.Zero) return false;
            return SetWindowText(hwnd, title);
        }
        catch
        {
            return false;
        }
    }

    public bool IsDesktop => true;

    // 用 javaw 而不是 java：前者不附带控制台窗口。
    // 启动 Minecraft 时如果用了 java.exe，游戏窗口旁边会多挂一个黑框。
    public string JavaExecutable => "javaw.exe";
    public string JavaExecutableFallback => "java.exe";

    // 版本 JSON 只有 natives-windows 与 natives-windows-arm64 两种，没有 x86 专用名。
    public string NativePlatformSuffix =>
        RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "windows-arm64" : "windows";

    public IEnumerable<(string Root, int Depth)> JavaProbeRoots()
    {
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

        // 深度 3 是实测出来的：本机有个 Program Files\Honor\HNPCAIService\jdk，
        // 宿主目录名完全看不出和 Java 有关系，只能靠遍历碰到。
        // 成本实测可接受 —— 这三个根目录扫到第 3 层共 832 个目录、33ms。
        yield return (pf, 3);
        yield return (pf86, 3);
        yield return (Path.Combine(local, "Programs"), 3);
        yield return (Path.Combine(user, ".jdks"), 1);                     // IntelliJ
        yield return (Path.Combine(roaming, ".minecraft", "runtime"), 1);  // 官方启动器
        yield return (Path.Combine(roaming, ".minecraft", "jre"), 1);      // 老版官方启动器
        yield return (Path.Combine(user, ".hmcl", "java"), 1);             // HMCL
        yield return (Path.Combine(user, "scoop", "apps"), 2);             // scoop
        yield return (Path.Combine(programData, "chocolatey", "lib"), 2);  // chocolatey
        yield return (Path.Combine(user, "Documents"), 0);
    }

    /// <summary>各发行版写进注册表的键与取值名。不同厂商的命名不统一，所以做成表。</summary>
    private static readonly (string Key, string ValueName)[] RegistrySources =
    {
        (@"SOFTWARE\JavaSoft\JDK", "JavaHome"),
        (@"SOFTWARE\JavaSoft\JRE", "JavaHome"),
        (@"SOFTWARE\JavaSoft\Java Development Kit", "JavaHome"),
        (@"SOFTWARE\JavaSoft\Java Runtime Environment", "JavaHome"),
        (@"SOFTWARE\Eclipse Adoptium\JDK", "Path"),
        (@"SOFTWARE\Eclipse Foundation\JDK", "Path"),
        (@"SOFTWARE\Microsoft\JDK", "Path"),
        (@"SOFTWARE\Azul Systems\Zulu", "InstallationPath"),
        (@"SOFTWARE\BellSoft\LibericaJDK", "JavaHome"),
        (@"SOFTWARE\Amazon Corretto", "InstallationPath"),
        (@"SOFTWARE\Semeru\JDK", "Path"),
        (@"SOFTWARE\SapMachine\JDK", "Path"),
    };

    public IEnumerable<string> JavaFromSystemDatabase()
    {
        var found = new List<string>();

        foreach (var (root, valueName) in RegistrySources)
        {
            foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
            {
                foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
                {
                    try
                    {
                        using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                        using var key = baseKey.OpenSubKey(root);
                        if (key is null) continue;

                        // 两种布局都要处理：
                        //   JavaSoft 风格 —— <root>\<版本> 的 JavaHome
                        //   MSI 风格    —— <root>\<版本>\hotspot\MSI 的 Path
                        foreach (var sub in key.GetSubKeyNames())
                        {
                            using var vk = key.OpenSubKey(sub);
                            if (vk is null) continue;

                            if (vk.GetValue(valueName) is string direct && direct.Length > 0)
                                found.Add(direct);

                            foreach (var deeper in new[] { @"hotspot\MSI", @"hotspot", "MSI" })
                            {
                                using var dk = vk.OpenSubKey(deeper);
                                if (dk?.GetValue(valueName) is string nested && nested.Length > 0)
                                    found.Add(nested);
                            }
                        }
                    }
                    catch { /* 注册表不可读时继续 */ }
                }
            }
        }

        return found;
    }

    public bool RevealInFileManager(string path)
    {
        try
        {
            var isFile = File.Exists(path);
            var args = isFile ? $"/select,\"{path}\"" : $"\"{path}\"";
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", args)
            {
                UseShellExecute = true,
            });
            return true;
        }
        catch { return false; }
    }
}
