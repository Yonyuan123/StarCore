using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using Microsoft.Win32;
using StarCore.Core;
using StarCore.Core.App;
using StarCore.Core.Models;
using StarCore.Core.Services;

namespace StarCore.Desktop.Bridge;

/// <summary>
/// 需要操作系统能力的方法：文件对话框、资源管理器、默认浏览器、快捷方式。
/// 这些都依赖 WPF / Shell，放在宿主而不是内核里，内核才能保持可测试。
/// </summary>
internal static class SystemActions
{
    public static object? Invoke(string method, JsonElement p)
    {
        return method switch
        {
            "system.pickFile" => PickFile(p),
            "system.pickFiles" => PickFiles(p),
            "system.pickFolder" => PickFolder(p),
            "system.openFolder" => OpenFolder(Str(p, "path")),
            "system.openPath" => OpenFolder(Str(p, "path")),
            "system.revealPath" => Reveal(Str(p, "path")),
            "system.openUrl" => OpenUrl(Str(p, "url")),
            "system.openInstanceFolder" => OpenInstance(Long(p, "id")),
            "system.openContentFolder" => OpenContent(Long(p, "instanceId"), Str(p, "kind")),
            "system.createShortcut" => CreateShortcut(Long(p, "id")),
            "system.memoryInfo" => MemoryInfo(),
            _ => throw new InvalidOperationException($"未知的系统方法：{method}"),
        };
    }

    /* ==================================================================
       文件对话框
       ================================================================== */

    private static object PickFile(JsonElement p)
    {
        var dlg = new OpenFileDialog
        {
            Title = Str(p, "title") ?? "选择文件",
            Filter = Str(p, "filter") ?? "所有文件 (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false,
        };
        var ok = dlg.ShowDialog() == true;
        return new { path = ok ? dlg.FileName : null, cancelled = !ok };
    }

    private static object PickFiles(JsonElement p)
    {
        var dlg = new OpenFileDialog
        {
            Title = Str(p, "title") ?? "选择文件",
            Filter = Str(p, "filter") ?? "所有文件 (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = true,
        };
        var ok = dlg.ShowDialog() == true;
        return new { paths = ok ? dlg.FileNames : Array.Empty<string>(), cancelled = !ok };
    }

    private static object PickFolder(JsonElement p)
    {
        var dlg = new OpenFolderDialog
        {
            Title = Str(p, "title") ?? "选择文件夹",
            Multiselect = false,
        };
        var ok = dlg.ShowDialog() == true;
        return new { path = ok ? dlg.FolderName : null, cancelled = !ok };
    }

    /* ==================================================================
       Shell
       ================================================================== */

    private static object OpenFolder(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) path = StarCorePaths.DataRoot;
        if (!Directory.Exists(path) && !File.Exists(path))
            return OpResult.Fail("目录不存在。");

        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
            return OpResult.Success();
        }
        catch (Exception ex)
        {
            return OpResult.Fail($"打开失败：{ex.Message}");
        }
    }

    /// <summary>在资源管理器里选中某个文件。</summary>
    private static object Reveal(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return OpResult.Fail("文件不存在。");
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            return OpResult.Success();
        }
        catch (Exception ex)
        {
            return OpResult.Fail($"打开失败：{ex.Message}");
        }
    }

    private static object OpenUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return OpResult.Fail("链接为空。");
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return OpResult.Fail("只允许打开 http/https 链接。");

        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            return OpResult.Success();
        }
        catch (Exception ex)
        {
            return OpResult.Fail($"打开链接失败：{ex.Message}");
        }
    }

    private static object OpenInstance(long? id)
    {
        var inst = id is null ? null : InstanceService.Get(id.Value);
        if (inst is null) return OpResult.Fail("实例不存在。");
        return OpenFolder(inst.Path);
    }

    private static object OpenContent(long? instanceId, string? kind)
    {
        var inst = instanceId is null ? null : InstanceService.Get(instanceId.Value);
        if (inst is null) return OpResult.Fail("实例不存在。");
        var dir = InstanceService.ContentFolder(inst.Path, kind ?? "mod");
        Directory.CreateDirectory(dir);
        return OpenFolder(dir);
    }

    /// <summary>在桌面创建指向该实例的启动快捷方式。</summary>
    private static object CreateShortcut(long? id)
    {
        var inst = id is null ? null : InstanceService.Get(id.Value);
        if (inst is null) return OpResult.Fail("实例不存在。");

        try
        {
            var exe = Environment.ProcessPath ?? Path.Combine(StarCorePaths.AppRoot, "StarCore.exe");
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            var link = Path.Combine(desktop, $"{Sanitize(inst.Name)}.lnk");

            // 用 WSH 创建 .lnk：这是 Windows 上唯一不需要额外依赖的做法
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null) return OpResult.Fail("系统不支持创建快捷方式。");

            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic sc = shell.CreateShortcut(link);
            sc.TargetPath = exe;
            sc.Arguments = $"--launch {inst.Id}";
            sc.WorkingDirectory = StarCorePaths.AppRoot;
            sc.Description = $"启动 {inst.Name}";
            sc.Save();

            Logger.Info($"[system] 已创建快捷方式：{link}");
            return OpResult.Success($"已在桌面创建「{inst.Name}」的快捷方式", link);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "[system] 创建快捷方式失败");
            return OpResult.Fail($"创建快捷方式失败：{ex.Message}");
        }
    }

    private static string Sanitize(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return string.IsNullOrWhiteSpace(name) ? "StarCore" : name;
    }

    private static object MemoryInfo()
    {
        long total = 0, available = 0;
        try
        {
            var info = GC.GetGCMemoryInfo();
            total = (long)(info.TotalAvailableMemoryBytes / 1024 / 1024);
            available = total;
        }
        catch { }

        return new
        {
            totalMb = total,
            availableMb = available,
            gcAvailableMb = (long)(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1024 / 1024),
            processorCount = Environment.ProcessorCount,
        };
    }

    private static string? Str(JsonElement p, string name)
        => p.ValueKind == JsonValueKind.Object && p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    private static long? Long(JsonElement p, string name)
    {
        if (p.ValueKind != JsonValueKind.Object || !p.TryGetProperty(name, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var i)) return i;
        if (v.ValueKind == JsonValueKind.String && long.TryParse(v.GetString(), out var s)) return s;
        return null;
    }
}
