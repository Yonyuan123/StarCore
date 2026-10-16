using StarCore.Core.App;
using StarCore.Core.Models;

namespace StarCore.Core.Services;

/// <summary>
/// 设置持久化。
/// 采用"读-改-写"整对象落盘：设置体量极小，整写比增量写更不容易出状态不一致。
/// </summary>
public static class SettingsService
{
    private static readonly object Gate = new();
    private static Settings _current = new();

    public static Settings Current => _current;

    public static void Load()
    {
        lock (Gate)
        {
            try
            {
                if (File.Exists(StarCorePaths.SettingsFile))
                {
                    var json = File.ReadAllText(StarCorePaths.SettingsFile);
                    _current = Json.Deserialize<Settings>(json) ?? new Settings();
                }
                else
                {
                    _current = new Settings();
                    Logger.Info("[settings] 未发现配置文件，使用默认设置");
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "[settings] 读取设置失败，已回退默认值");
                _current = new Settings();
            }

            // 数据目录可以被设置覆盖（高级用户把数据放到其它盘）
            if (!string.IsNullOrWhiteSpace(_current.DataRoot) && Directory.Exists(_current.DataRoot))
            {
                StarCorePaths.OverrideDataRoot(_current.DataRoot);
            }
        }

        if (_current.MaxThreads is < 1 or > 256) _current.MaxThreads = 32;
        _ = Http.SetProxyAsync(_current.Proxy);
    }

    /// <summary>合并一批字段并落盘。只覆盖传入的字段，未传的保持原值。</summary>
    public static Settings Patch(IDictionary<string, object?> patch)
    {
        lock (Gate)
        {
            foreach (var (key, value) in patch)
            {
                Apply(_current, key, value);
            }
            Save();
        }
        return _current;
    }

    public static void Save()
    {
        lock (Gate)
        {
            try
            {
                File.WriteAllText(StarCorePaths.SettingsFile, System.Text.Json.JsonSerializer.Serialize(_current, Json.Pretty));
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "[settings] 保存设置失败");
            }
        }
    }

    public static void Reset()
    {
        lock (Gate)
        {
            _current = new Settings();
            Save();
        }
        _ = Http.SetProxyAsync("");
    }

    private static string Normalize(string value, string fallback, params string[] allowed)
        => allowed.Contains(value, StringComparer.OrdinalIgnoreCase) ? value : fallback;

    /// <summary>
    /// 把前端传来的松散键值映射到强类型字段。
    /// 用显式 switch 而不是反射，好处是拼错字段名会在编译期被忽略而不是静默生效。
    /// </summary>
    private static void Apply(Settings s, string key, object? v)
    {
        bool AsBool(bool fallback) => v switch
        {
            bool b => b,
            string str => str.Equals("true", StringComparison.OrdinalIgnoreCase),
            _ => fallback,
        };
        int AsInt(int fallback) => v switch
        {
            int i => i,
            long l => (int)l,
            double d => (int)d,
            string str when int.TryParse(str, out var r) => r,
            _ => fallback,
        };
        double AsDouble(double fallback) => v switch
        {
            double d => d,
            float f => f,
            int i => i,
            long l => l,
            string str when double.TryParse(str, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var r) => r,
            _ => fallback,
        };
        string AsStr(string fallback) => v?.ToString() ?? fallback;

        switch (key)
        {
            case "theme": s.Theme = AsStr(s.Theme); break;
            case "accent": s.Accent = AsStr(s.Accent); break;
            case "background": s.Background = AsStr(s.Background); break;
            case "bgImageUrl": s.BgImageUrl = AsStr(s.BgImageUrl).Trim(); break;
            case "bgVideoUrl": s.BgVideoUrl = AsStr(s.BgVideoUrl).Trim(); break;
            case "bgMusicUrl": s.BgMusicUrl = AsStr(s.BgMusicUrl).Trim(); break;
            case "bgMusicEnabled": s.BgMusicEnabled = AsBool(s.BgMusicEnabled); break;
            case "bgMusicVolume": s.BgMusicVolume = Math.Clamp(AsDouble(s.BgMusicVolume), 0, 1); break;
            case "uiMotion": s.UiMotion = Normalize(AsStr(s.UiMotion), "subtle", "off", "subtle", "full"); break;
            case "uiPerf": s.UiPerf = Normalize(AsStr(s.UiPerf), "high", "low", "mid", "high"); break;
            // 兼容旧配置里的 reduceMotion 字段
            case "reduceMotion": if (AsBool(false)) s.UiMotion = "off"; break;
            case "language": s.Language = AsStr(s.Language); break;
            case "keepLauncherOpen": s.KeepLauncherOpen = AsBool(s.KeepLauncherOpen); break;
            case "closeToTray": s.CloseToTray = AsBool(s.CloseToTray); break;
            case "launchCloseLauncher": s.LaunchCloseLauncher = AsBool(s.LaunchCloseLauncher); break;
            case "updateRepo": s.UpdateRepo = AsStr(s.UpdateRepo).Trim().Trim('/'); break;
            case "autoUpdate": s.AutoUpdate = AsBool(s.AutoUpdate); break;
            case "downloadSource": s.DownloadSource = AsStr(s.DownloadSource); break;
            case "customDownloadSource": s.CustomDownloadSource = AsStr(s.CustomDownloadSource); break;
            case "maxThreads": s.MaxThreads = Math.Clamp(AsInt(s.MaxThreads), 1, 256); break;
            case "proxy":
                s.Proxy = AsStr(s.Proxy).Trim();
                _ = Http.SetProxyAsync(s.Proxy);
                break;
            case "translateProvider": s.TranslateProvider = Normalize(AsStr(s.TranslateProvider), "free", "free", "llm"); break;
            case "translateEndpoint": s.TranslateEndpoint = AsStr(s.TranslateEndpoint).Trim(); break;
            case "translateApiKey": s.TranslateApiKey = AsStr(s.TranslateApiKey).Trim(); break;
            case "translateModel": s.TranslateModel = AsStr(s.TranslateModel).Trim(); break;
            case "skinSiteUrl": s.SkinSiteUrl = AsStr(s.SkinSiteUrl).Trim().TrimEnd('/'); break;
            case "microsoftClientId": s.MicrosoftClientId = AsStr(s.MicrosoftClientId).Trim(); break;
            case "dataRoot":
                s.DataRoot = AsStr(s.DataRoot).Trim();
                if (!string.IsNullOrWhiteSpace(s.DataRoot)) StarCorePaths.OverrideDataRoot(s.DataRoot);
                break;
            default:
                Logger.Debug($"[settings] 忽略未知字段：{key}");
                break;
        }
    }
}
