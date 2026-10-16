using StarCore.Core.App;
using StarCore.Core.Models;

namespace StarCore.Core.Services;

/// <summary>
/// 自定义媒体（背景图 / 背景视频 / 背景音乐）的导入与管理。
///
/// 为什么不直接引用用户选的原始路径：那个文件随时可能被移动、重命名或删除，
/// 一旦如此启动器就会变成空白背景，而用户完全不知道发生了什么。
/// 所以统一**复制进数据目录**，启动器自己管着，设置也就跟着可移植了。
///
/// 为什么不去碰用户的文件：只复制，绝不修改或删除源文件 —— 那是用户的资产。
/// </summary>
public static class MediaService
{
    /// <summary>支持的三类媒体及其扩展名。</summary>
    public static readonly string[] ImageExtensions = { ".png", ".jpg", ".jpeg", ".webp", ".bmp", ".gif" };
    public static readonly string[] MusicExtensions = { ".mp3", ".ogg", ".wav", ".m4a", ".flac", ".aac" };
    public static readonly string[] VideoExtensions = { ".mp4", ".webm", ".m4v" };

    /// <summary>
    /// 各类媒体的体积上限。
    /// 视频给得宽（背景视频动辄几百 MB），但也要有个上限 ——
    /// 否则用户误选了一个 5GB 的文件，复制过程会把磁盘写满且看起来像卡死。
    /// </summary>
    private const long MaxImageBytes = 64L * 1024 * 1024;
    private const long MaxMusicBytes = 128L * 1024 * 1024;
    private const long MaxVideoBytes = 1024L * 1024 * 1024;

    private static string MediaRoot => Path.Combine(StarCorePaths.DataRoot, "media");

    private static string[] ExtensionsFor(string kind) => kind switch
    {
        "image" => ImageExtensions,
        "music" => MusicExtensions,
        "video" => VideoExtensions,
        _ => Array.Empty<string>(),
    };

    private static long LimitFor(string kind) => kind switch
    {
        "image" => MaxImageBytes,
        "music" => MaxMusicBytes,
        _ => MaxVideoBytes,
    };

    /// <summary>把用户选的文件复制进数据目录，返回可供界面直接使用的地址。</summary>
    public static object Import(string sourcePath, string kind)
    {
        kind = (kind ?? "").Trim().ToLowerInvariant();
        var allowed = ExtensionsFor(kind);
        if (allowed.Length == 0) return OpResult.Fail($"未知的媒体类型：{kind}");

        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            return OpResult.Fail("文件不存在或已被移动。");

        var ext = Path.GetExtension(sourcePath).ToLowerInvariant();
        if (!allowed.Contains(ext))
            return OpResult.Fail($"这个格式不支持。{kind} 支持：{string.Join("、", allowed)}");

        var size = new FileInfo(sourcePath).Length;
        var limit = LimitFor(kind);
        if (size > limit)
            return OpResult.Fail($"文件太大（{size / 1024 / 1024} MB），上限 {limit / 1024 / 1024} MB。");

        try
        {
            Directory.CreateDirectory(MediaRoot);

            // 同类媒体只保留一个：换了新的就删掉旧的，避免数据目录无限膨胀
            ClearKind(kind);

            var dest = Path.Combine(MediaRoot, kind + ext);
            File.Copy(sourcePath, dest, true);

            var relative = Path.Combine("media", kind + ext).Replace('\\', '/');
            Logger.Info($"[media] 已导入{KindLabel(kind)}：{Path.GetFileName(sourcePath)}（{size / 1024 / 1024.0:F1} MB）");

            return new
            {
                ok = true,
                kind,
                relative,
                url = WebAssets.Data(relative),
                fileName = Path.GetFileName(sourcePath),
                size,
            };
        }
        catch (Exception ex)
        {
            Logger.Error(ex, $"[media] 导入{KindLabel(kind)}失败");
            return OpResult.Fail($"导入失败：{ex.Message}");
        }
    }

    /// <summary>删除某一类媒体。</summary>
    public static object Remove(string kind)
    {
        kind = (kind ?? "").Trim().ToLowerInvariant();
        if (ExtensionsFor(kind).Length == 0) return OpResult.Fail($"未知的媒体类型：{kind}");

        try
        {
            ClearKind(kind);
            Logger.Info($"[media] 已清除{KindLabel(kind)}");
            return new { ok = true };
        }
        catch (Exception ex)
        {
            return OpResult.Fail($"清除失败：{ex.Message}");
        }
    }

    /// <summary>列出现有的三类媒体，供界面在启动时还原状态。</summary>
    public static object Info()
    {
        return new
        {
            image = Describe("image"),
            video = Describe("video"),
            music = Describe("music"),
        };
    }

    private static object? Describe(string kind)
    {
        try
        {
            if (!Directory.Exists(MediaRoot)) return null;

            var exts = ExtensionsFor(kind);
            foreach (var f in Directory.EnumerateFiles(MediaRoot))
            {
                var e = Path.GetExtension(f);
                if (!exts.Contains(e, StringComparer.OrdinalIgnoreCase)) continue;

                var relative = Path.Combine("media", Path.GetFileName(f)).Replace('\\', '/');
                return new
                {
                    kind,
                    relative,
                    url = WebAssets.Data(relative),
                    fileName = Path.GetFileName(f),
                    size = new FileInfo(f).Length,
                };
            }
        }
        catch (Exception ex)
        {
            Logger.Debug($"[media] 读取{KindLabel(kind)}信息失败：{ex.Message}");
        }
        return null;
    }

    private static void ClearKind(string kind)
    {
        if (!Directory.Exists(MediaRoot)) return;

        foreach (var f in Directory.EnumerateFiles(MediaRoot).ToList())
        {
            var e = Path.GetExtension(f);
            if (ExtensionsFor(kind).Contains(e, StringComparer.OrdinalIgnoreCase))
            {
                try { File.Delete(f); } catch (Exception ex) { Logger.Warn($"[media] 删除旧文件失败：{ex.Message}"); }
            }
        }
    }

    private static string KindLabel(string kind) => kind switch
    {
        "image" => "背景图",
        "video" => "背景视频",
        "music" => "背景音乐",
        _ => "媒体",
    };
}
