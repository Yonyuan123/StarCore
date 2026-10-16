using System.Drawing;
using System.Drawing.Imaging;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using StarCore.Core.App;
using StarCore.Core.Models;

namespace StarCore.Core.Services;

/// <summary>
/// 账户中心：离线账户、微软正版账户、皮肤与披风。
///
/// 设计前提：**启动器不内置任何账户**。所有账户都由用户显式创建或登录，
/// 未登录时界面进入空状态引导 —— 这是本模块最重要的一条约束。
/// </summary>
public static class AccountService
{
    private sealed class Store
    {
        public string? ActiveUuid { get; set; }
        public List<AccountInfo> Profiles { get; set; } = new();
    }

    private static readonly object Gate = new();
    private static Store _store = new();

    /// <summary>离线账户名规则：与正版一致的 3–16 位字母数字下划线。</summary>
    private static readonly Regex OfflineNamePattern = new(@"^[A-Za-z0-9_]{3,16}$", RegexOptions.Compiled);

    public static IReadOnlyList<AccountInfo> Profiles
    {
        get { lock (Gate) return _store.Profiles.ToList(); }
    }

    public static string? ActiveUuid
    {
        get { lock (Gate) return _store.ActiveUuid; }
    }

    public static AccountInfo? Active
    {
        get
        {
            lock (Gate)
                return _store.Profiles.FirstOrDefault(p => p.Uuid == _store.ActiveUuid)
                       ?? _store.Profiles.FirstOrDefault();
        }
    }

    public static void Initialize()
    {
        lock (Gate)
        {
            try
            {
                if (File.Exists(StarCorePaths.AccountsFile))
                {
                    _store = Json.Deserialize<Store>(File.ReadAllText(StarCorePaths.AccountsFile)) ?? new Store();
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "[account] 读取账户文件失败");
                _store = new Store();
            }

            // 活跃账户失效时自动退到第一个可用账户
            if (_store.ActiveUuid is not null && _store.Profiles.All(p => p.Uuid != _store.ActiveUuid))
                _store.ActiveUuid = _store.Profiles.FirstOrDefault()?.Uuid;

            Logger.Info($"[account] 已载入 {_store.Profiles.Count} 个账户");
        }
    }

    public static void Persist()
    {
        lock (Gate)
        {
            try { File.WriteAllText(StarCorePaths.AccountsFile, JsonSerializer.Serialize(_store, Json.Pretty)); }
            catch (Exception ex) { Logger.Error(ex, "[account] 保存账户文件失败"); }
        }
    }

    /* ==================================================================
       离线账户
       ================================================================== */

    /// <summary>
    /// 离线 UUID 采用和官方一致的 v3 方案：MD5("OfflinePlayer:" + name)，
    /// 再按 UUID v3 规范写入版本位与变体位。
    /// 这样同一名字在任何启动器里得到的 UUID 都相同，存档与服务器不会错乱。
    /// </summary>
    public static string OfflineUuid(string name)
    {
        var hash = MD5.HashData(Encoding.UTF8.GetBytes("OfflinePlayer:" + name));
        hash[6] = (byte)((hash[6] & 0x0F) | 0x30); // version 3
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80); // variant RFC 4122
        return FormatUuid(hash);
    }

    public static string FormatUuid(byte[] b)
        => new string(Convert.ToHexString(b).ToLowerInvariant().ToCharArray().Select((c, i) =>
            (i == 8 || i == 12 || i == 16 || i == 20) ? '-' : c).SelectMany(c => c.ToString()).ToArray());

    public static (bool ok, string? error, AccountInfo? profile) AddOffline(string name)
    {
        name = (name ?? "").Trim();
        if (!OfflineNamePattern.IsMatch(name))
            return (false, "名称需为 3–16 位英文字母、数字或下划线。", null);

        var uuid = OfflineUuid(name);
        lock (Gate)
        {
            if (_store.Profiles.Any(p => p.Uuid == uuid))
                return (false, "已存在同名账户。", null);

            var profile = new AccountInfo
            {
                Uuid = uuid,
                UserName = name,
                ProfileType = "Offline",
                SkinModel = "classic",
            };
            _store.Profiles.Add(profile);
            _store.ActiveUuid ??= uuid;
        }
        Persist();
        Logger.Info($"[account] 已创建离线账户 {name} ({uuid})");
        return (true, null, Active);
    }

    public static bool Remove(string uuid)
    {
        lock (Gate)
        {
            var n = _store.Profiles.RemoveAll(p => p.Uuid == uuid);
            if (n == 0) return false;
            if (_store.ActiveUuid == uuid) _store.ActiveUuid = _store.Profiles.FirstOrDefault()?.Uuid;
        }
        Persist();
        return true;
    }

    public static bool SetActive(string uuid)
    {
        lock (Gate)
        {
            if (_store.Profiles.All(p => p.Uuid != uuid)) return false;
            _store.ActiveUuid = uuid;
        }
        Persist();
        return true;
    }

    /* ==================================================================
       微软账户
       ================================================================== */

    /// <summary>设备码流程第 1 步：申请设备码与用户码。</summary>
    public static Task<MicrosoftAuth.DeviceCodeInfo> StartDeviceCodeAsync(CancellationToken ct = default)
    {
        // client id 由设置驱动，每次登录前同步一次，改完设置无需重启
        MicrosoftAuth.ClientId = SettingsService.Current.MicrosoftClientId;
        return MicrosoftAuth.StartDeviceCodeAsync(ct);
    }

    /// <summary>设备码流程第 2 步：轮询一次授权结果。</summary>
    public static async Task<MicrosoftAuth.PollResult> PollDeviceCodeAsync(string deviceCode, CancellationToken ct = default)
    {
        var r = await MicrosoftAuth.PollAsync(deviceCode, ct);
        if (r.Status != "ok" || r.Session is null) return r;

        var session = r.Session;
        var profile = new AccountInfo
        {
            Uuid = session.Uuid,
            UserName = session.Name,
            ProfileType = "Microsoft",
            AccessToken = session.AccessToken,
            RefreshToken = session.RefreshToken,
            TokenExpiresAt = session.ExpiresAt,
            SkinUrl = session.SkinUrl,
            CapeUrl = session.CapeUrl,
            SkinModel = session.SkinModel,
        };

        lock (Gate)
        {
            var existing = _store.Profiles.FirstOrDefault(p => p.Uuid == profile.Uuid);
            if (existing is not null)
            {
                // 同一账号再次登录：更新凭据而不是新增一条
                existing.UserName = profile.UserName;
                existing.AccessToken = profile.AccessToken;
                existing.RefreshToken = profile.RefreshToken ?? existing.RefreshToken;
                existing.TokenExpiresAt = profile.TokenExpiresAt;
                existing.SkinUrl = profile.SkinUrl ?? existing.SkinUrl;
                existing.CapeUrl = profile.CapeUrl;
                existing.SkinModel = profile.SkinModel;
                existing.ProfileType = "Microsoft";
            }
            else
            {
                _store.Profiles.Add(profile);
            }
            _store.ActiveUuid = profile.Uuid;
        }
        Persist();

        await PrepareFaceAsync(profile.Uuid);
        Logger.Info($"[account] 微软账户登录成功：{profile.UserName}");
        return r;
    }

    /// <summary>刷新正版账户资料（续期令牌并更新皮肤）。</summary>
    public static async Task<bool> RefreshAsync(string uuid, CancellationToken ct = default)
    {
        AccountInfo? target;
        lock (Gate) target = _store.Profiles.FirstOrDefault(p => p.Uuid == uuid);

        if (target is null) return false;
        if (target.ProfileType != "Microsoft" || string.IsNullOrWhiteSpace(target.RefreshToken))
            return true; // 离线账户无需刷新

        // 令牌仍然有效就不打扰服务端
        if (target.TokenExpiresAt is not null && target.TokenExpiresAt > DateTimeOffset.Now.AddMinutes(5))
            return true;

        try
        {
            var session = await MicrosoftAuth.RefreshAsync(target.RefreshToken!, ct);
            if (session is null) return false;

            lock (Gate)
            {
                target.AccessToken = session.AccessToken;
                target.RefreshToken = session.RefreshToken ?? target.RefreshToken;
                target.TokenExpiresAt = session.ExpiresAt;
                target.UserName = session.Name;
                target.SkinUrl = session.SkinUrl ?? target.SkinUrl;
                target.CapeUrl = session.CapeUrl;
                target.SkinModel = session.SkinModel;
            }
            Persist();
            await PrepareFaceAsync(uuid);
            return true;
        }
        catch (Exception ex)
        {
            Logger.Warn($"[account] 刷新账户失败：{ex.Message}");
            return false;
        }
    }

    /* ==================================================================
       皮肤与披风
       ================================================================== */

    public sealed class SkinPayload
    {
        public string? SkinUrl { get; set; }
        public string? SkinFace { get; set; }
        public string? CapeUrl { get; set; }
        public string SkinModel { get; set; } = "classic";
        public bool CanUpload { get; set; }
        public List<CapeEntry> Capes { get; set; } = new();
    }

    public sealed class CapeEntry
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public string? Url { get; set; }
        public bool Active { get; set; }
    }

    private static string SkinsCacheDir
    {
        get
        {
            var dir = Path.Combine(StarCorePaths.CacheRoot, "skins");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static SkinPayload GetSkin(string uuid)
    {
        AccountInfo? p;
        lock (Gate) p = _store.Profiles.FirstOrDefault(x => x.Uuid == uuid) ?? Active;
        if (p is null) return new SkinPayload();

        var payload = new SkinPayload
        {
            SkinModel = p.SkinModel,
            CanUpload = p.ProfileType == "Microsoft",
        };

        // 本地缓存优先：离线账户与已下载过的正版皮肤都走这里，
        // 避免每次打开账户页都去请求一次 CDN。
        var local = FindLocalSkin(p.Uuid);
        payload.SkinUrl = local is not null ? WebAssets.Data(local) : WebAssets.Normalize(p.SkinUrl);
        payload.CapeUrl = WebAssets.Normalize(p.CapeUrl);

        var face = Path.Combine(SkinsCacheDir, $"{p.Uuid}_face.png");
        if (File.Exists(face)) payload.SkinFace = WebAssets.Data(face);

        return payload;
    }

    private static string? FindLocalSkin(string uuid)
    {
        foreach (var ext in new[] { "png" })
        {
            var f = Path.Combine(SkinsCacheDir, $"{uuid}.{ext}");
            if (File.Exists(f)) return f;
        }
        return null;
    }

    /// <summary>上传皮肤。正版账户走 Mojang API；离线账户只在本机替换显示贴图。</summary>
    public static async Task<OpResult> UploadSkinAsync(string uuid, string? filePath, string variant = "classic", CancellationToken ct = default)
    {
        AccountInfo? p;
        lock (Gate) p = _store.Profiles.FirstOrDefault(x => x.Uuid == uuid);
        if (p is null) return OpResult.Fail("账户不存在。");

        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            return OpResult.Fail("皮肤文件不存在。");

        // 先把文件落到本地缓存，保证界面上立刻能看到效果（即便服务端上传失败）
        var local = Path.Combine(SkinsCacheDir, $"{uuid}.png");
        try { File.Copy(filePath, local, true); }
        catch (Exception ex) { return OpResult.Fail($"无法读取皮肤文件：{ex.Message}"); }

        p.SkinModel = variant == "slim" ? "slim" : "classic";

        if (p.ProfileType == "Microsoft")
        {
            try
            {
                await MicrosoftAuth.UploadSkinAsync(p.AccessToken!, local, p.SkinModel, ct);
            }
            catch (Exception ex)
            {
                Logger.Warn($"[account] 服务端皮肤上传失败（本地已生效）：{ex.Message}");
                Persist();
                await PrepareFaceAsync(uuid);
                return OpResult.Fail($"已应用到本地，但上传到官方失败：{ex.Message}");
            }
        }

        Persist();
        await PrepareFaceAsync(uuid);
        return OpResult.Success("皮肤已更新。");
    }

    public static OpResult SetSkinModel(string uuid, string model)
    {
        lock (Gate)
        {
            var p = _store.Profiles.FirstOrDefault(x => x.Uuid == uuid);
            if (p is null) return OpResult.Fail("账户不存在。");
            p.SkinModel = model == "slim" ? "slim" : "classic";
        }
        Persist();
        return OpResult.Success("模型已切换。");
    }

    /// <summary>
    /// 从皮肤贴图裁出 8×8 头部，作为界面头像。
    /// 皮肤为标准 64×64 布局，头部位于 (8,8)；1.8 之前的 64×32 老皮肤同位置。
    /// </summary>
    public static async Task PrepareFaceAsync(string uuid)
    {
        try
        {
            AccountInfo? p;
            lock (Gate) p = _store.Profiles.FirstOrDefault(x => x.Uuid == uuid);
            if (p is null) return;

            var local = FindLocalSkin(uuid);
            if (local is null && !string.IsNullOrWhiteSpace(p.SkinUrl) &&
                p.SkinUrl!.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                local = Path.Combine(SkinsCacheDir, $"{uuid}.png");
                await Http.DownloadAsync(p.SkinUrl!, local);
            }
            if (local is null || !File.Exists(local)) return;

            var facePath = Path.Combine(SkinsCacheDir, $"{uuid}_face.png");
            using var full = Image.FromFile(local);
            using var face = new Bitmap(8, 8, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(face))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
                g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
                g.DrawImage(full, new Rectangle(0, 0, 8, 8), new Rectangle(8, 8, 8, 8), GraphicsUnit.Pixel);
            }
            face.Save(facePath, ImageFormat.Png);
            Logger.Debug($"[account] 已生成头像：{facePath}");
        }
        catch (Exception ex)
        {
            // 头像只是锦上添花，失败不影响账户本身的可用性
            Logger.Debug($"[account] 生成头像失败（忽略）：{ex.Message}");
        }
    }

    public static async Task<List<CapeEntry>> ListCapesAsync(string uuid, CancellationToken ct = default)
    {
        AccountInfo? p;
        lock (Gate) p = _store.Profiles.FirstOrDefault(x => x.Uuid == uuid);
        if (p is null || p.ProfileType != "Microsoft" || string.IsNullOrWhiteSpace(p.AccessToken))
            return new List<CapeEntry>();

        try
        {
            var caps = await MicrosoftAuth.GetProfileAsync(p.AccessToken!, ct);
            if (caps is null) return new List<CapeEntry>();

            lock (Gate)
            {
                p.CapeUrl = caps.ActiveCapeUrl;
                p.SkinUrl = caps.SkinUrl ?? p.SkinUrl;
            }
            Persist();

            return caps.Capes.Select(c => new CapeEntry { Id = c.Id, Name = c.Alias, Url = c.Url, Active = c.Url == caps.ActiveCapeUrl }).ToList();
        }
        catch (Exception ex)
        {
            Logger.Warn($"[account] 获取披风列表失败：{ex.Message}");
            return new List<CapeEntry>();
        }
    }

    /// <summary>构造启动游戏所需的会话信息。</summary>
    public sealed class LaunchSession
    {
        public string Uuid { get; init; } = "";
        public string Name { get; init; } = "";
        public string AccessToken { get; init; } = "";
        public string UserType { get; init; } = "legacy";
        public string Xuid { get; init; } = "";
        public string ClientId { get; init; } = "";
    }

    public static LaunchSession? BuildSession(string uuid)
    {
        AccountInfo? p;
        lock (Gate) p = _store.Profiles.FirstOrDefault(x => x.Uuid == uuid) ?? Active;
        if (p is null) return null;

        return new LaunchSession
        {
            Uuid = p.Uuid,
            Name = p.UserName,
            // 离线账户的 accessToken 按惯例用 uuid 本身（服务端只做非空校验）
            AccessToken = p.ProfileType == "Microsoft" ? (p.AccessToken ?? "") : p.Uuid,
            UserType = p.ProfileType == "Microsoft" ? "msa" : "legacy",
            Xuid = p.ProfileType == "Microsoft" ? MicrosoftAuth.LastXuid.GetValueOrDefault(p.Uuid, "") : "",
            ClientId = p.ProfileType == "Microsoft" ? SettingsService.Current.MicrosoftClientId : "starcore",
        };
    }
}
