using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using StarCore.Core.App;

namespace StarCore.Core.Services;

/// <summary>
/// 微软账户登录（OAuth 2.0 设备码流程 → Xbox Live → XSTS → Minecraft 服务）。
///
/// 选择设备码而不是内嵌浏览器，原因有三：
///   1. 不需要在启动器里放 WebView 登录页，也就不会碰到密码输入的安全顾虑；
///   2. 支持两步验证与"密码less"账户，用户在自己熟悉的浏览器里完成；
///   3. 实现简单、可在任何环境（含无 GUI 的控制台）下工作。
/// </summary>
public static class MicrosoftAuth
{
    /// <summary>
    /// 内置的应用身份（混淆存放）。
    ///
    /// 为什么不写成明文 GUID：
    ///   · 它不是密钥，泄露不会造成安全损失，但明文放在二进制里会被随手 grep 出来，
    ///     然后被人拿去做别的启动器，或者被批量滥用触发风控 —— PCL / HMCL 也都是
    ///     这样把它藏起来的；
    ///   · 这串东西**绝不能**直接从源码里删掉换新值，否则所有人的登录都会断。
    ///     换应用时请用 `_tools/diag` 里的说明重新生成（XOR 密钥就是 "StarCore"）。
    ///
    /// 说明：这里不内置任何"从别处借来的"公共 id —— 微软已经回收了历史上被第三方
    /// 启动器广泛借用的那个，拿它请求只会得到 AADSTS700016（应用不存在）。
    /// </summary>
    private const string ObfuscatedClientId = "MRIFRSFYEVJ+TARDcUJGBzVHTEslXxFINkBUR3FbQ10yFVJK";

    /// <summary>内置的应用身份。用户想换成自己注册的应用时，走设置里的覆盖值。</summary>
    public static string DefaultClientId => Reveal(ObfuscatedClientId);

    private static string Reveal(string encoded)
    {
        try
        {
            byte[] key = System.Text.Encoding.UTF8.GetBytes("StarCore");
            var data = Convert.FromBase64String(encoded);
            for (var i = 0; i < data.Length; i++) data[i] ^= key[i % key.Length];
            return System.Text.Encoding.UTF8.GetString(data);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "[msa] 内置应用身份解码失败");
            return "";
        }
    }

    /// <summary>当前生效的 client id。设置里为空时回落到内置身份。</summary>
    public static string ClientId { get; set; } = "";

    /// <summary>当前生效的 client id（都没有时抛出具可操作性的错误）。</summary>
    /// <summary>
    /// 环境变量覆盖（排查用）：STARCORE_MSA_CLIENT_ID。
    /// 场景：申请表里登记的 ID 和内置的不一致时，用户可以先设置它验证，
    /// 不必等启动器更新。优先级：环境变量 > 设置里的覆盖值 > 内置值。
    /// </summary>
    private const string ClientIdEnvVar = "STARCORE_MSA_CLIENT_ID";

    private static string RequireClientId()
    {
        var fromEnv = Environment.GetEnvironmentVariable(ClientIdEnvVar);
        var id = !string.IsNullOrWhiteSpace(fromEnv)
            ? fromEnv
            : string.IsNullOrWhiteSpace(ClientId) ? DefaultClientId : ClientId;
        if (string.IsNullOrWhiteSpace(id))
            throw new InvalidOperationException(
                "启动器内置的微软应用身份不可用，且设置里也没有覆盖值。"
                + "请更新到最新版本，或在「设置 → 账户」里填入自己注册的 Azure 应用 ID。");
        return id.Trim();
    }

    private const string DeviceCodeUrl = "https://login.microsoftonline.com/consumers/oauth2/v2.0/devicecode";
    private const string TokenUrl = "https://login.microsoftonline.com/consumers/oauth2/v2.0/token";
    private const string XblUrl = "https://user.auth.xboxlive.com/user/authenticate";
    private const string XstsUrl = "https://xsts.auth.xboxlive.com/xsts/authorize";
    private const string McLoginUrl = "https://api.minecraftservices.com/authentication/login_with_xbox";
    private const string McProfileUrl = "https://api.minecraftservices.com/minecraft/profile";
    private const string McSkinUrl = "https://api.minecraftservices.com/minecraft/profile/skins";

    /// <summary>uuid → XUID。启动参数里需要它，登录后缓存起来避免重复换票。</summary>
    public static readonly ConcurrentDictionary<string, string> LastXuid = new();

    public sealed class DeviceCodeInfo
    {
        public string DeviceCode { get; set; } = "";
        public string UserCode { get; set; } = "";
        public string VerificationUri { get; set; } = "";
        public string Message { get; set; } = "";
        public int ExpiresIn { get; set; } = 900;
        public int Interval { get; set; } = 5;
    }

    public sealed class PollResult
    {
        /// <summary>pending | ok | expired | error</summary>
        public string Status { get; set; } = "pending";
        public string? Message { get; set; }
        public Session? Session { get; set; }
    }

    public sealed class Session
    {
        public string Uuid { get; set; } = "";
        public string Name { get; set; } = "";
        public string AccessToken { get; set; } = "";
        public string? RefreshToken { get; set; }
        public DateTimeOffset ExpiresAt { get; set; }
        public string? SkinUrl { get; set; }
        public string? CapeUrl { get; set; }
        public string SkinModel { get; set; } = "classic";
    }

    public sealed class MinecraftProfile
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string? SkinUrl { get; set; }
        public string? ActiveCapeUrl { get; set; }
        public string SkinModel { get; set; } = "classic";
        public List<MinecraftCape> Capes { get; set; } = new();
    }

    public sealed class MinecraftCape
    {
        public string Id { get; set; } = "";
        public string Alias { get; set; } = "";
        public string Url { get; set; } = "";
    }

    // ==================================================================
    //   自检
    //
    //   存在的理由很具体：应用注册配错时微软的报错（如 AADSTS70002
    //   "must be marked as 'mobile'"）完全不指向要改的那一项，
    //   用户只能反复试。这里直接把两个端点都打一遍，用**哪个通了**反推病因 ——
    //   这比读错误码可靠得多。
    // ==================================================================

    public sealed class ProbeResult
    {
        public bool Ok { get; set; }
        public string? Message { get; set; }
        public string? UserCode { get; set; }
        public bool ConsumerOk { get; set; }
        public bool OrganizationOk { get; set; }
        public string? DeviceCode { get; set; }
        public string? ClientIdMasked { get; set; }

        /// <summary>
        /// Mojang 是否已放行这个应用（白名单）。
        /// 判定方式是拿一个假 XSTS 令牌打 login_with_xbox：
        ///   403 Invalid app registration → 应用没被放行
        ///   401 UNAUTHORIZED              → 应用层已通过，只是令牌无效（正是我们要的）
        /// 这比只看设备码自检有用得多 —— 设备码成功不代表能换到游戏令牌。
        /// </summary>
        public bool GameApiOk { get; set; }
    }

    /// <summary>
    /// 只申请设备码、不轮询（不消耗任何用户操作），用来判断登录链路是否可用。
    /// 这是幂等的只读探测：拿到的设备码直接丢弃，不写任何状态。
    /// </summary>
    public static async Task<ProbeResult> ProbeAsync(CancellationToken ct = default)
    {
        var fromEnv = Environment.GetEnvironmentVariable(ClientIdEnvVar);
        var id = !string.IsNullOrWhiteSpace(fromEnv)
            ? fromEnv
            : string.IsNullOrWhiteSpace(ClientId) ? DefaultClientId : ClientId;

        var r = new ProbeResult
        {
            ClientIdMasked = (id.Length > 8 ? $"{id[..8]}…{id[^4..]}" : "(空)")
                             + (string.IsNullOrWhiteSpace(fromEnv) ? "" : "（来自环境变量）"),
            ConsumerOk = false,
            OrganizationOk = false,
        };

        // 个人账户端点 —— Minecraft 必须走这条（正版账户都是个人 MS Account）
        var consumer = await TryDeviceCodeAsync(DeviceCodeUrl, id, ct);
        r.ConsumerOk = consumer.Ok;

        // 组织账户端点 —— 只用来**对照**：它通而上面不通，就说明应用不支持个人账户
        var org = await TryDeviceCodeAsync(
            "https://login.microsoftonline.com/organizations/oauth2/v2.0/devicecode", id, ct);
        r.OrganizationOk = org.Ok;

        // Mojang 白名单探测（假令牌，不消耗用户授权）
        try
        {
            var body = new StringContent(
                "{\"identityToken\":\"XBL3.0 x=starcore-probe;probe\"}",
                System.Text.Encoding.UTF8, "application/json");
            using var res = await Http.Client.PostAsync(McLoginUrl, body, ct);
            var text = await res.Content.ReadAsStringAsync(ct);
            // 401 = 已经认我们这个应用了（只是令牌假的）；403 = 还没进白名单
            r.GameApiOk = (int)res.StatusCode is 401 or 400;
            if ((int)res.StatusCode == 403 && text.Contains("Invalid app registration"))
                r.GameApiOk = false;
        }
        catch { r.GameApiOk = false; }

        if (consumer.Ok)
        {
            r.Ok = true;
            r.UserCode = consumer.UserCode;
            r.DeviceCode = consumer.DeviceCode;

            // 再打一次换票端点做收尾确认：拿到 device_code 只证明"申请"这一步通，
            // 换票端点才是真正会出问题的地方（权限没授予、公共客户端流没开，
            // 都在这一步才暴露）。还没授权时它正确的返回是 authorization_pending ——
            // 那就说明链路完整，用户去浏览器确认一下即可。
            var poll = await TryPollOnceAsync(consumer.DeviceCode ?? "", id, ct);
            r.Message = poll switch
            {
                null => "设备码申请成功；换票端点未返回可判读的结果（可能被限流），一般不影响登录。",
                "authorization_pending" or "slow_down" =>
                    "设备码申请与换票端点均正常，登录链路完整可用。",
                _ => $"设备码申请成功，但换票端点返回了 {poll} —— 通常意味着 Xbox Live 权限还没授予。",
            };
            r.Ok = poll is null or "authorization_pending" or "slow_down";
            return r;
        }

        r.Ok = false;
        if (string.IsNullOrEmpty(id))
        {
            r.Message = "启动器没有可用的应用身份（内置值解码失败，且设置里没有覆盖值）。";
        }
        else if (consumer.Error?.Contains("AADSTS70002") == true && org.Ok)
        {
            r.Message =
                "应用身份存在，但**不被个人微软账户接受**（组织账户端点倒是通的）。\n\n" +
                "最常见的原因：Azure 应用注册的「身份验证」页里缺一项 ——\n" +
                "  · 没有「添加平台 → 移动和桌面应用程序」（只加过 Web 不算）；\n" +
                "  · 或「允许公共客户端流」还是「否」。\n" +
                "这两项任缺其一，微软都会回 AADSTS70002。改完立即生效，无需重启启动器。";
        }
        else if (consumer.Error?.Contains("AADSTS700016") == true)
        {
            r.Message = $"应用身份已失效或被删除（{r.ClientIdMasked}）。这是应用注册方需要处理的问题。";
        }
        else
        {
            r.Message = $"设备码申请失败：{Shorten(consumer.Error ?? "(无返回)")}";
        }
        return r;
    }

    private static async Task<(bool Ok, string? UserCode, string? DeviceCode, string? Error)> TryDeviceCodeAsync(
        string url, string clientId, CancellationToken ct)
    {
        try
        {
            var body = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = clientId,
                ["scope"] = "XboxLive.signin offline_access",
            });

            using var res = await Http.Client.PostAsync(url, body, ct);
            var text = await res.Content.ReadAsStringAsync(ct);
            if (!res.IsSuccessStatusCode) return (false, null, null, text);

            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            return (true,
                    root.TryGetProperty("user_code", out var u) ? u.GetString() : null,
                    root.TryGetProperty("device_code", out var d) ? d.GetString() : null,
                    null);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return (false, null, null, ex.Message);
        }
    }

    /// <summary>
    /// 只换一次票。返回 null 表示"没拿到可判读的结论"（网络/限流），
    /// 否则返回 OAuth 的 error 字段 —— 用户还没去授权时应当是 authorization_pending。
    /// </summary>
    private static async Task<string?> TryPollOnceAsync(string deviceCode, string clientId, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(deviceCode)) return null;
        try
        {
            var body = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = clientId,
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
                ["device_code"] = deviceCode,
            });

            using var res = await Http.Client.PostAsync(TokenUrl, body, ct);
            var text = await res.Content.ReadAsStringAsync(ct);
            if (res.IsSuccessStatusCode) return "already_authorized";

            try
            {
                using var doc = JsonDocument.Parse(text);
                return doc.RootElement.TryGetProperty("error", out var e) ? e.GetString() : null;
            }
            catch { return null; }
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    /* ==================================================================
       1. 设备码
       ================================================================== */

    public static async Task<DeviceCodeInfo> StartDeviceCodeAsync(CancellationToken ct = default)
    {
        var clientId = RequireClientId();
        var body = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["scope"] = "XboxLive.signin offline_access",
        });

        using var res = await Http.Client.PostAsync(DeviceCodeUrl, body, ct);
        var text = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
            throw new InvalidOperationException(TranslateAuthError(text, (int)res.StatusCode));

        using var doc = JsonDocument.Parse(text);
        var r = doc.RootElement;

        var dc = new DeviceCodeInfo
        {
            DeviceCode = Str(r, "device_code"),
            UserCode = Str(r, "user_code"),
            VerificationUri = Str(r, "verification_uri"),
            Message = Str(r, "message"),
        };
        if (r.TryGetProperty("expires_in", out var ei) && ei.TryGetInt32(out var e)) dc.ExpiresIn = e;
        if (r.TryGetProperty("interval", out var iv) && iv.TryGetInt32(out var i)) dc.Interval = Math.Max(2, i);

        if (string.IsNullOrEmpty(dc.VerificationUri))
            dc.VerificationUri = "https://microsoft.com/link";

        Logger.Info($"[msa] 已申请设备码，用户码 {dc.UserCode}，有效期 {dc.ExpiresIn}s");
        return dc;
    }

    /* ==================================================================
       2. 轮询
       ================================================================== */

    public static async Task<PollResult> PollAsync(string deviceCode, CancellationToken ct = default)
    {
        /* ✦ 设备码只能用一次。这条记录是为了兜住一次真实踩坑：

           换票环节（Xbox → XSTS → Minecraft）里任何一个 HTTP 步骤失败都会抛异常
           （见 PostJsonAsync）。异常一旦穿透出去，前端会把它当成"网络抖动、
           下次轮询再试"，于是拿**已经被消费掉**的 device_code 再问一次微软，
           拿回 AADSTS70000「device_code has already been used」——
           这条报错会盖掉真正的失败原因，用户看到的就是一句看不懂的英文 JSON。

           所以：一旦拿设备码换过票，无论成败都把结论记下来；
           后续任何一次重复轮询直接回放同一结论，不再打扰微软。
           （用 ConcurrentDictionary 是因为轮询可能和"取消"并发，且设备码本身是随机的。） */
        if (SpentCodes.TryGetValue(deviceCode, out var replay))
        {
            Logger.Debug("[msa] 该设备码已结算过，直接回放上次结论");
            return replay;
        }

        var body = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = RequireClientId(),
            ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
            ["device_code"] = deviceCode,
        });

        using var res = await Http.Client.PostAsync(TokenUrl, body, ct);
        var text = await res.Content.ReadAsStringAsync(ct);

        if (!res.IsSuccessStatusCode)
        {
            var err = TryErrorCode(text);
            var failure = err switch
            {
                "authorization_pending" => new PollResult { Status = "pending" },
                "slow_down" => new PollResult { Status = "pending" },
                "expired_token" => new PollResult { Status = "expired", Message = "登录代码已失效，请重新开始。" },
                "authorization_declined" => new PollResult { Status = "error", Message = "你取消了授权。" },
                "bad_verification_code" => new PollResult { Status = "error", Message = "登录代码无效，请重新开始。" },

                // 走到这里说明这个设备码已经被换过票了。正常情况下前端不该再问，
                // 但万一问到了，就说清"流程其实已经结束了"，而不是甩原始 JSON。
                "invalid_grant" when text.Contains("AADSTS70000") =>
                    new PollResult { Status = "error", Message = "这次登录已经结算过了（设备码一次性）。请点「微软账户登录」重新开始。" },

                _ => new PollResult { Status = "error", Message = $"授权失败：{Shorten(text)}" },
            };

            // pending 不是终局，不记账；其余都是终局，记账以免被重复问
            if (failure.Status != "pending") Remember(deviceCode, failure);
            return failure;
        }

        using var doc = JsonDocument.Parse(text);
        var access = Str(doc.RootElement, "access_token");
        var refresh = Str(doc.RootElement, "refresh_token");
        var expiresIn = 3600;
        if (doc.RootElement.TryGetProperty("expires_in", out var ei) && ei.TryGetInt32(out var e)) expiresIn = e;

        if (string.IsNullOrEmpty(access))
        {
            var bad = new PollResult { Status = "error", Message = "微软未返回访问令牌。" };
            Remember(deviceCode, bad);
            return bad;
        }

        /* 命门在这里：此刻设备码**已经消费掉了**，后续不论发生什么都必须
           变成一个 PollResult 返回，绝不能让异常穿透出去 ——
           一穿透，前端就会带着同一个设备码重试，用户看到的就是 AADSTS70000。 */
        PollResult result;
        try
        {
            result = await CompleteSignInAsync(access, refresh, expiresIn, ct);
        }
        catch (OperationCanceledException)
        {
            throw;   // 用户主动取消，按取消处理
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "[msa] 微软授权已通过，但换取游戏令牌失败");
            result = new PollResult
            {
                Status = "error",
                Message = "微软授权已通过，但换取游戏登录令牌时失败了。\n\n"
                          + ex.Message
                          + "\n\n"
                          + HintForExchangeFailure(ex.Message),
            };
        }

        Remember(deviceCode, result);
        return result;
    }

    /// <summary>
    /// 给"换票阶段失败"补一句人话。
    ///
    /// 这一段的价值在于：用户已经完成了浏览器授权，心理上认为"登录成功了"，
    /// 此时如果只丢一个 HTTP 403，他完全不知道该去查什么。
    /// 实测撞到的那个 403 尤其误导 —— 报错文案是 "Invalid app registration"，
    /// 看起来像"应用没注册好"，实际原因是 Mojang 的**人工允许列表**。
    /// </summary>
    private static string HintForExchangeFailure(string message)
    {
        // 最容易被误判成"配置写错了"的一条，放最前
        if (message.Contains("Invalid app registration", StringComparison.OrdinalIgnoreCase))
            return "这不是配置问题，也没有修法能绕过：\n"
                   + "Mojang 对 Java 版游戏服务 API 实行**人工允许列表**制度 —— "
                   + "新注册的 Azure 应用在通过审核之前，login_with_xbox 一律返回 403。\n\n"
                   + "已有的启动器（PCL / HMCL 等）是因为早已在名单里，所以能直接用。\n"
                   + "我们的应用需要先申请：https://aka.ms/mce-reviewappid\n"
                   + "审核通过后**无需改任何代码**，这里会立刻可用。";

        if (message.Contains("401") || message.Contains("403"))
            return "常见原因（按可能性排序）：\n"
                   + "  · 这个微软账户没有 Minecraft: Java 版的所有权（只有基岩版或只有 Game Pass 的云端权益不算）；\n"
                   + "  · Xbox Live 档案尚未创建 —— 先登录一次 xbox.com 把档案建出来；\n"
                   + "  · 账户是未成年人账户，需要家庭组里的成人先授权。";

        if (message.Contains("429"))
            return "被微软限流了（429）。等一两分钟再试即可。";

        return "如果反复失败，请检查网络与系统时间是否准确 —— 微软的令牌校验对时间偏差很敏感。";
    }

    /// <summary>已结算的设备码 → 结论。只保留最近若干条，避免长时间运行后无限增长。</summary>
    private static readonly ConcurrentDictionary<string, PollResult> SpentCodes = new();

    private static void Remember(string deviceCode, PollResult result)
    {
        if (SpentCodes.Count > 16) SpentCodes.Clear();
        SpentCodes[deviceCode] = result;
    }

    /// <summary>用刷新令牌换取新的会话（静默续期）。</summary>
    public static async Task<Session?> RefreshAsync(string refreshToken, CancellationToken ct = default)
    {
        var body = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = RequireClientId(),
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["scope"] = "XboxLive.signin offline_access",
        });

        using var res = await Http.Client.PostAsync(TokenUrl, body, ct);
        var text = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
        {
            Logger.Warn($"[msa] 刷新令牌失败：{Shorten(text)}");
            return null;
        }

        using var doc = JsonDocument.Parse(text);
        var access = Str(doc.RootElement, "access_token");
        var refresh = Str(doc.RootElement, "refresh_token");
        var expiresIn = 3600;
        if (doc.RootElement.TryGetProperty("expires_in", out var ei) && ei.TryGetInt32(out var e)) expiresIn = e;

        var poll = await CompleteSignInAsync(access, refresh, expiresIn, ct);
        return poll.Session;
    }

    /* ==================================================================
       3. 换票：Xbox Live → XSTS → Minecraft
       ================================================================== */

    private static async Task<PollResult> CompleteSignInAsync(string msaAccessToken, string? msaRefreshToken, int expiresIn, CancellationToken ct)
    {
        // --- Xbox Live ---
        var xblJson = await PostJsonAsync(XblUrl, new
        {
            Properties = new
            {
                AuthMethod = "RPS",
                SiteName = "user.auth.xboxlive.com",
                RpsTicket = $"d={msaAccessToken}",
            },
            RelyingParty = "http://auth.xboxlive.com",
            TokenType = "JWT",
        }, ct);

        var xblToken = Str(xblJson, "Token");
        var uhs = "";
        if (xblJson.TryGetProperty("DisplayClaims", out var dc) &&
            dc.TryGetProperty("xui", out var xui) && xui.GetArrayLength() > 0)
        {
            uhs = Str(xui[0], "uhs");
        }
        if (string.IsNullOrEmpty(xblToken) || string.IsNullOrEmpty(uhs))
            return new PollResult { Status = "error", Message = "Xbox Live 认证失败。" };

        // --- XSTS ---
        HttpResponseMessage xstsRes;
        string xstsText;
        try
        {
            var content = new StringContent(JsonSerializer.Serialize(new
            {
                Properties = new { SandboxId = "RETAIL", UserTokens = new[] { xblToken } },
                RelyingParty = "rp://api.minecraftservices.com/",
                TokenType = "JWT",
            }), Encoding.UTF8, "application/json");

            xstsRes = await Http.Client.PostAsync(XstsUrl, content, ct);
            xstsText = await xstsRes.Content.ReadAsStringAsync(ct);
        }
        catch (Exception ex)
        {
            return new PollResult { Status = "error", Message = $"XSTS 请求失败：{ex.Message}" };
        }

        if (!xstsRes.IsSuccessStatusCode)
        {
            // Xbox 服务的错误码对用户很有解释价值，直接翻译过来
            var code = 0L;
            try
            {
                using var d = JsonDocument.Parse(xstsText);
                if (d.RootElement.TryGetProperty("XErr", out var xe) && xe.TryGetInt64(out var v)) code = v;
            }
            catch { /* 保底走通用分支 */ }

            return code switch
            {
                2148916233 => new PollResult { Status = "error", Message = "该微软账户还没有 Xbox 档案。请先登录一次 Xbox 官网创建档案。" },
                2148916235 => new PollResult { Status = "error", Message = "所在地区不支持 Xbox Live。" },
                2148916236 or 2148916237 => new PollResult { Status = "error", Message = "该账户需要完成年龄验证。" },
                2148916238 => new PollResult { Status = "error", Message = "该账户是未成年人账户，需要由家庭组中的成人先完成授权。" },
                _ => new PollResult { Status = "error", Message = $"XSTS 授权失败（XErr {code}）。" },
            };
        }

        using var xstsDoc = JsonDocument.Parse(xstsText);
        var xstsToken = Str(xstsDoc.RootElement, "Token");
        if (string.IsNullOrEmpty(xstsToken))
            return new PollResult { Status = "error", Message = "XSTS 未返回令牌。" };

        // --- Minecraft 服务 ---
        var mcJson = await PostJsonAsync(McLoginUrl, new { identityToken = $"XBL3.0 x={uhs};{xstsToken}" }, ct);
        var mcAccess = Str(mcJson, "access_token");
        var mcExpires = 86400;
        if (mcJson.TryGetProperty("expires_in", out var mex) && mex.TryGetInt32(out var me)) mcExpires = me;

        if (string.IsNullOrEmpty(mcAccess))
            return new PollResult { Status = "error", Message = "Minecraft 登录失败，请稍后重试。" };

        // --- 档案 ---
        var profile = await GetProfileAsync(mcAccess, ct);
        if (profile is null)
            return new PollResult { Status = "error", Message = "该微软账户没有 Minecraft: Java 版的所有权。" };

        LastXuid[profile.Id] = uhs;

        return new PollResult
        {
            Status = "ok",
            Session = new Session
            {
                Uuid = profile.Id,
                Name = profile.Name,
                AccessToken = mcAccess,
                RefreshToken = msaRefreshToken,
                ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(mcExpires - 60),
                SkinUrl = profile.SkinUrl,
                CapeUrl = profile.ActiveCapeUrl,
                SkinModel = profile.SkinModel,
            },
        };
    }

    /* ==================================================================
       4. 档案与皮肤
       ================================================================== */

    public static async Task<MinecraftProfile?> GetProfileAsync(string mcAccessToken, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, McProfileUrl);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", mcAccessToken);

        using var res = await Http.Client.SendAsync(req, ct);
        if (!res.IsSuccessStatusCode) return null;

        var text = await res.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(text);
        var r = doc.RootElement;

        var p = new MinecraftProfile
        {
            Id = Str(r, "id"),
            Name = Str(r, "name"),
        };

        if (r.TryGetProperty("skins", out var skins) && skins.GetArrayLength() > 0)
        {
            // 取 state == ACTIVE 的那一张，没有就取第一张
            JsonElement chosen = skins[0];
            foreach (var s in skins.EnumerateArray())
            {
                if (string.Equals(Str(s, "state"), "ACTIVE", StringComparison.OrdinalIgnoreCase)) { chosen = s; break; }
            }
            p.SkinUrl = Str(chosen, "url");
            p.SkinModel = string.Equals(Str(chosen, "variant"), "SLIM", StringComparison.OrdinalIgnoreCase) ? "slim" : "classic";
        }

        if (r.TryGetProperty("capes", out var capes))
        {
            foreach (var c in capes.EnumerateArray())
            {
                var entry = new MinecraftCape
                {
                    Id = Str(c, "id"),
                    Alias = Str(c, "alias"),
                    Url = Str(c, "url"),
                };
                if (entry.Alias == "") entry.Alias = "披风";
                p.Capes.Add(entry);
                if (string.Equals(Str(c, "state"), "ACTIVE", StringComparison.OrdinalIgnoreCase))
                    p.ActiveCapeUrl = entry.Url;
            }
        }

        return p;
    }

    /// <summary>上传皮肤到官方。variant 取 classic / slim。</summary>
    public static async Task UploadSkinAsync(string mcAccessToken, string pngPath, string variant, CancellationToken ct = default)
    {
        var bytes = await File.ReadAllBytesAsync(pngPath, ct);

        using var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        content.Add(file, "file", Path.GetFileName(pngPath));
        content.Add(new StringContent(variant == "slim" ? "slim" : "classic"), "variant");

        using var req = new HttpRequestMessage(HttpMethod.Post, McSkinUrl) { Content = content };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", mcAccessToken);

        using var res = await Http.Client.SendAsync(req, ct);
        var text = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
            throw new InvalidOperationException($"HTTP {(int)res.StatusCode}：{Shorten(text)}");
    }

    /* ==================================================================
       内部工具
       ================================================================== */

    private static async Task<JsonElement> PostJsonAsync(string url, object body, CancellationToken ct)
    {
        using var content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var res = await Http.Client.PostAsync(url, content, ct);
        var text = await res.Content.ReadAsStringAsync(ct);

        if (!res.IsSuccessStatusCode && res.StatusCode != HttpStatusCode.BadRequest)
            throw new InvalidOperationException($"HTTP {(int)res.StatusCode}：{Shorten(text)}");

        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.Clone();
    }

    private static string Str(JsonElement el, string name)
        => el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? "" : "";

    /// <summary>
    /// 把 Entra ID 的原始错误翻译成用户能照着做的说明。
    /// 直接把 AADSTS 码丢给用户等于没给信息。
    /// </summary>
    /// <summary>
    /// 把微软返回的错误翻成"用户下一步该点哪里"。
    ///
    /// 这些分支不是拍脑袋写的，是拿真实应用 ID 打过端点之后按返回顺序补齐的 ——
    /// 例如 AADSTS70002（客户端未被标记为 mobile）就是实测撞到的：
    /// 应用注册好了、ID 完全有效，但只加过"Web"平台、没加"移动和桌面应用程序"，
    /// 于是设备码流程直接被拒。旧代码把它归到"ID 无效"，用户会一直去核对那串 GUID，
    /// 方向完全错了。
    /// </summary>
    private static string TranslateAuthError(string json, int status)
    {
        var code = "";
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("error", out var e)) code = e.GetString() ?? "";
        }
        catch { /* 非 JSON 就按原文处理 */ }

        // AADSTS 细分码优先于 OAuth 的粗粒度 error 字段：
        // 同一个 invalid_client 底下可能藏着完全不同的病因。
        if (json.Contains("AADSTS70002"))
            return "这个 Azure 应用还没有登记成「移动和桌面应用程序」，设备码登录会被微软直接拒绝。\n\n" +
                   "修法：Azure 门户 → 你的应用 → 左侧「身份验证」→「添加平台」→ 选「移动和桌面应用程序」→ 勾选 " +
                   "https://login.microsoftonline.com/common/oauth2/nativeclient → 保存。\n" +
                   "（只加过「Web」平台是不够的。这个 ID 本身没问题，不用重新注册应用。）";

        if (json.Contains("AADSTS50194") || json.Contains("AADSTS500011"))
            return "该 Azure 应用只允许单一组织登录，而 Minecraft 需要个人微软账户。\n\n" +
                   "修法：Azure 门户 → 你的应用 →「身份验证」→ 把「支持的账户类型」改成" +
                   "「任何组织目录中的账户和个人 Microsoft 账户」，保存后重试。";

        if (json.Contains("AADSTS65004") || json.Contains("AADSTS65005"))
            return "尚未同意所需权限。请在 Azure 门户 →「API 权限」里添加 Xbox Live 的 XboxLive.signin，" +
                   "并点「代表 XX 授予管理员同意」。";

        return code switch
        {
            // 白名单类错误：说的是"应用没被 Mojang 放行"，不是网络问题。
            // 用户最容易误判成"还没通过审核"，所以要给出可核对的字段。
            _ when json.Contains("Invalid app registration") =>
                "Mojang 未放行这个 Azure 应用（403 Invalid app registration）。\n\n"
                + "这条错误只认「Application (Client) ID」，请按顺序核对：\n"
                + "  1. 申请表（aka.ms/mce-reviewappid）里填的应用 ID，是否与启动器内置的 ID 完全一致；\n"
                + "  2. 审批通过后通常需要 24～48 小时才会在 API 侧生效，期间仍会返回这条错误；\n"
                + "  3. 如果 ID 对不上，可以设置环境变量 STARCORE_MSA_CLIENT_ID=<正确的ID> 后重启启动器，\n"
                + "     无需等新版本即可验证（设置 → 账户 → 自检登录链路 会显示当前使用的 ID 前缀）。",
            "unauthorized_client" when json.Contains("AADSTS700016") =>
                "该 Azure 应用 ID 不存在。请在「设置 → 账户」重新填写自己注册应用的客户端 ID。",
            "invalid_client" =>
                $"这个 Azure 应用 ID 被微软拒绝了（{AadStsCode(json) ?? "invalid_client"}）。\n" +
                "多数情况是应用注册里的「身份验证」页还缺一项配置。可在「设置 → 账户」按说明逐条核对。",
            "unauthorized_client" =>
                "该 Azure 应用未启用「允许公共客户端流」。请在 Azure 门户的应用身份验证页勾选它。",
            "invalid_grant" =>
                "授权已失效，请重新登录。",
            _ => $"登录失败（HTTP {status}）：{Shorten(json)}",
        };
    }

    /// <summary>从错误正文里抠出 AADSTS 细分码，便于用户搜索。</summary>
    private static string? AadStsCode(string json)
    {
        var i = json.IndexOf("AADSTS", StringComparison.Ordinal);
        if (i < 0) return null;
        var end = i;
        while (end < json.Length && char.IsLetterOrDigit(json[end])) end++;
        return json[i..end];
    }

    private static string TryErrorCode(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("error", out var e) ? e.GetString() ?? "" : "";
        }
        catch { return ""; }
    }

    private static string Shorten(string s) => s.Length <= 220 ? s : s[..220] + "…";
}
