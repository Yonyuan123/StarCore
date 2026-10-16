using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using StarCore.Core.App;
using StarCore.Core.Models;

namespace StarCore.Core.Services;

/// <summary>
/// 资源详情翻译。
///
/// 两条后端，按质量与可用性分层：
///
///   1. <b>LLM 翻译</b>（可选，需要 API Key）
///      走 OpenAI 兼容的 chat/completions 接口（DeepSeek / 硅基流动 / 百炼 都行），
///      配合 Minecraft 术语表提示词。这是唯一能真正把 Modrinth 上那种
///      "markdown + 口语化 + 大量专有名词" 的介绍翻成通顺中文的方式 ——
///      机翻会把 Mod / Shader / Datapack 全译成"模丁/着色器/数据包"，读起来很别扭。
///
///   2. <b>免费机翻</b>（默认，无需配置）
///      MyMemory 的公开接口。质量一般，但胜在零配置可用，并且我们对它做两层补偿：
///      先按术语表把专有名词替换成占位符，翻完再还原 —— 这样至少专业词汇是对的。
///
/// 两者都落盘缓存：同一段文本只翻一次，离线也能看。
/// </summary>
public static class TranslateService
{
    /// <summary>MyMemory 单次查询长度上限（实测约 500 字符，留一点余量）。</summary>
    private const int ChunkSize = 450;

    /// <summary>
    /// 术语表版本号。
    ///
    /// 把它混进缓存键里，是为了让"改了术语表之后旧译文自动失效"。
    /// 否则会出现一种很难查的现象：明明把 glossary 修好了，界面上看到的却还是
    /// 上次那份带着"渲染发动机"的旧译文 —— 因为它命中了缓存。
    /// **每次改动 <see cref="Glossary"/> 都要把这个数字 +1。**
    /// </summary>
    private const int GlossaryVersion = 2;

    /* ==================================================================
       术语表
       ------------------------------------------------------------------
       这是翻译质量的胜负手。机翻最常见的失败就是把这些词按字面意思乱译，
       而中文 MC 社区对它们有固定叫法。
       ================================================================== */

    private static readonly (string En, string Zh)[] Glossary =
    {
        // ⚠️ 顺序即优先级：**长的复合词必须排在短的单词前面**。
        //    否则 "rendering engine" 会被 "rendering" 先命中，剩下的 "engine"
        //    单独交给机翻，就成了"渲染发动机"这种读不懂的译法。

        // —— 复合词（必须先于其组成部分）——
        ("rendering engine", "渲染引擎"),
        ("graphics engine", "图形引擎"),
        ("light engine", "光照引擎"),
        ("game engine", "游戏引擎"),
        ("resource pack", "资源包"),
        ("resource packs", "资源包"),
        ("shader pack", "光影包"),
        ("shader packs", "光影包"),
        ("data pack", "数据包"),
        ("data packs", "数据包"),
        ("mod loader", "模组加载器"),
        ("mod loaders", "模组加载器"),
        ("mod pack", "整合包"),
        ("mod packs", "整合包"),
        ("frame rate", "帧率"),
        ("frame rates", "帧率"),
        ("bug fix", "问题修复"),
        ("bug fixes", "问题修复"),
        ("client side", "客户端"),
        ("server side", "服务端"),
        ("client-side", "客户端"),
        ("server-side", "服务端"),
        ("optifine", "OptiFine"),
        ("fabric api", "Fabric API"),

        // —— 单个术语 ——
        ("resourcepacks", "资源包"),
        ("shaderpacks", "光影包"),
        ("modpack", "整合包"),
        ("modpacks", "整合包"),
        ("datapacks", "数据包"),
        ("datapack", "数据包"),
        ("modloader", "模组加载器"),
        ("shaders", "光影"),
        ("shader", "光影"),
        ("mods", "模组"),
        ("mod", "模组"),
        ("vanilla", "原版"),
        ("snapshots", "快照"),
        ("snapshot", "快照"),
        ("chunks", "区块"),
        ("chunk", "区块"),
        ("biomes", "生物群系"),
        ("biome", "生物群系"),
        ("dimensions", "维度"),
        ("dimension", "维度"),
        ("mobs", "生物"),
        ("mob", "生物"),
        ("engines", "引擎"),
        ("engine", "引擎"),
        ("rendering", "渲染"),
        ("renderer", "渲染器"),
        ("textures", "贴图"),
        ("texture", "贴图"),
        ("lighting", "光照"),
        ("tick rate", "游戏刻"),
        ("gameplay", "玩法"),
        ("multiplayer", "多人游戏"),
        ("singleplayer", "单人游戏"),
        ("culling", "剔除"),
        ("entities", "实体"),
        ("entity", "实体"),
        ("seed", "种子"),
        ("worlds", "世界"),
        ("world", "世界"),
        ("seeds", "种子"),
        ("crashes", "崩溃"),
        ("crash", "崩溃"),
        ("dependencies", "前置依赖"),
        ("dependency", "前置依赖"),
        ("compatibility", "兼容性"),
        ("optimisation", "优化"),
        ("optimization", "优化"),
        ("performance", "性能"),
        ("loaders", "加载器"),
        ("loader", "加载器"),
        ("configs", "配置"),
        ("config", "配置"),
        ("configuration", "配置"),
        ("clients", "客户端"),
        ("client", "客户端"),
        ("servers", "服务端"),
        ("server", "服务端"),
        ("updates", "更新"),
        ("update", "更新"),
        ("versions", "版本"),
        ("version", "版本"),
        ("releases", "正式版"),
        ("release", "正式版"),
        ("flickering", "闪烁"),
        ("flicker", "闪烁"),
    };

    /* ==================================================================
       缓存
       ================================================================== */

    private static readonly Dictionary<string, string> Cache = new(StringComparer.Ordinal);
    private static readonly object Gate = new();
    private static bool _loaded;

    private static string CacheFile => Path.Combine(StarCorePaths.CacheRoot, "translations.json");

    private static void EnsureLoaded()
    {
        if (_loaded) return;
        lock (Gate)
        {
            if (_loaded) return;
            _loaded = true;
            try
            {
                if (!File.Exists(CacheFile)) return;
                var map = Json.Deserialize<Dictionary<string, string>>(File.ReadAllText(CacheFile));
                if (map is null) return;
                foreach (var (k, v) in map) Cache[k] = v;
                Logger.Debug($"[translate] 翻译缓存已载入，共 {map.Count} 条");
            }
            catch (Exception ex)
            {
                Logger.Warn($"[translate] 翻译缓存读取失败（忽略）：{ex.Message}");
            }
        }
    }

    private static void SaveCache()
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(StarCorePaths.CacheRoot);
                File.WriteAllText(CacheFile, JsonSerializer.Serialize(Cache, Json.Options));
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"[translate] 翻译缓存写入失败（忽略）：{ex.Message}");
        }
    }

    private static string KeyOf(string text, string provider) =>
        Convert.ToHexString(SHA1.HashData(
            Encoding.UTF8.GetBytes($"v{GlossaryVersion}\u0000{provider}\u0000{text}"))).ToLowerInvariant();

    /* ==================================================================
       对外入口
       ================================================================== */

    public static async Task<object> TranslateAsync(string text, string? target = null, CancellationToken ct = default)
    {
        target ??= "zh-CN";
        text = (text ?? "").Trim();

        if (text.Length == 0)
            return OpResult.Fail("没有可翻译的内容。");

        // 正文常带 markdown，先规整一下：否则机翻会把每个 # 和 * 都当正文，
        // 翻完结构就乱了。
        var prepared = NormalizeMarkdown(text);

        var settings = SettingsService.Current;
        var useLlm = string.Equals(settings.TranslateProvider, "llm", StringComparison.OrdinalIgnoreCase)
                     && !string.IsNullOrWhiteSpace(settings.TranslateApiKey);

        var providerTag = useLlm ? $"llm:{settings.TranslateModel}" : "mymemory";
        var key = KeyOf(prepared, providerTag);

        EnsureLoaded();
        if (Cache.TryGetValue(key, out var hit))
            return new { text = hit, provider = providerTag, cached = true };

        string result;
        try
        {
            result = useLlm
                ? await TranslateByLlmAsync(prepared, target, settings, ct)
                : await TranslateByMyMemoryAsync(prepared, target, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Logger.Warn($"[translate] 翻译失败：{ex.Message}");
            return OpResult.Fail(useLlm
                ? $"翻译失败：{ex.Message}。可在「设置 → 翻译」里检查接口地址与密钥。"
                : $"翻译失败：{ex.Message}。可以到「设置 → 翻译」配置一个 AI 接口，翻译质量会好很多。");
        }

        if (!string.IsNullOrWhiteSpace(result))
        {
            lock (Gate) Cache[key] = result;
            SaveCache();
        }

        return new { text = result, provider = providerTag, cached = false };
    }

    /* ==================================================================
       LLM 路径
       ================================================================== */

    private static async Task<string> TranslateByLlmAsync(
        string text, string target, Models.Settings s, CancellationToken ct)
    {
        var endpoint = string.IsNullOrWhiteSpace(s.TranslateEndpoint)
            ? "https://api.deepseek.com/v1/chat/completions"
            : s.TranslateEndpoint.Trim();
        var model = string.IsNullOrWhiteSpace(s.TranslateModel) ? "deepseek-chat" : s.TranslateModel.Trim();

        var system = BuildSystemPrompt(target);

        var payload = new
        {
            model,
            temperature = 0.2,   // 翻译要的是确定性，不是创造力
            stream = false,
            messages = new object[]
            {
                new { role = "system", content = system },
                new { role = "user", content = text },
            },
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, endpoint);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", s.TranslateApiKey.Trim());
        req.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        using var res = await Http.Client.SendAsync(req, ct);
        var body = await res.Content.ReadAsStringAsync(ct);

        if (!res.IsSuccessStatusCode)
            throw new InvalidOperationException($"接口返回 HTTP {(int)res.StatusCode}：{Shorten(body)}");

        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
            throw new InvalidOperationException($"接口返回里没有 choices：{Shorten(body)}");

        var content = choices[0].TryGetProperty("message", out var msg)
                      && msg.TryGetProperty("content", out var c)
            ? c.GetString()
            : null;

        if (string.IsNullOrWhiteSpace(content))
            throw new InvalidOperationException("接口返回了空内容。");

        return RestoreGlossary(content.Trim());
    }

    private static string BuildSystemPrompt(string target)
    {
        var lang = target.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? "简体中文" : target;

        return $"""
            你是 Minecraft 社区的专业本地化译者。把用户给你的内容翻译成{lang}。

            要求：
            1. 只输出译文本身，不要任何解释、前言、代码块包裹或"以下是翻译"之类的话。
            2. 保留原文的 Markdown 结构（标题 #、列表 -、粗体 **、链接 []()、行内代码 `）与换行位置。
            3. 保留原文里的英文专有名词、模组名、命令、版本号、URL 不动。
            4. 术语必须用 Minecraft 中文社区的标准叫法，例如：
               mod → 模组；modpack → 整合包；resource pack → 资源包；shader → 光影；
               datapack → 数据包；vanilla → 原版；snapshot → 快照；chunk → 区块；
               biome → 生物群系；mob → 生物；optifine → OptiFine；serverside → 服务端。
            5. 符合中文表达习惯，不要逐词硬译；长句可以拆，但不要丢信息。
            6. 如果原文本来就是中文，原样返回。
            """;
    }

    /* ==================================================================
       免费机翻路径
       ================================================================== */

    private static async Task<string> TranslateByMyMemoryAsync(string text, string target, CancellationToken ct)
    {
        var langpair = target.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? "en|zh-CN" : $"auto|{target}";
        var chunks = SplitForApi(text, ChunkSize);
        var sb = new StringBuilder();

        foreach (var chunk in chunks)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(chunk)) { sb.Append(chunk); continue; }

            // 只把术语保护起来，其余交给机翻。保护后再翻译，专业词至少不会错。
            var tokens = new Dictionary<string, string>(StringComparer.Ordinal);
            var guarded = ApplyGlossary(chunk, tokens);

            var url = "https://api.mymemory.translated.net/get?q="
                      + Uri.EscapeDataString(guarded)
                      + "&langpair=" + Uri.EscapeDataString(langpair);

            var body = await DownloadSource.GetStringAsync(url, 2 * 1024 * 1024, ct);
            using var doc = JsonDocument.Parse(body);

            var translated = doc.RootElement.TryGetProperty("responseData", out var rd)
                             && rd.TryGetProperty("translatedText", out var tt)
                ? tt.GetString()
                : null;

            // MyMemory 的响应里翻译结果有时会带 HTML 实体，需要还原
            if (string.IsNullOrWhiteSpace(translated))
                throw new InvalidOperationException("翻译服务没有返回内容（可能超出当日免费额度）。");

            sb.Append(RestoreGlossary(WebUtility.HtmlDecode(translated), tokens));
        }

        return sb.ToString().Trim();
    }

    /* ==================================================================
       辅助
       ================================================================== */

    /// <summary>
    /// 把术语替换成不参与翻译的占位符，翻完再还原。
    /// 占位符选择 ⟦T0⟧ 这种形态，是因为它在机翻里几乎不会被改写或拆词。
    /// </summary>
    private static string ApplyGlossary(string text, Dictionary<string, string> tokens)
    {
        var result = text;
        var n = 0;

        foreach (var (en, zh) in Glossary)
        {
            // 大小写不敏感地整词匹配，避免 "mod" 命中 "model" 里的前缀
            var pattern = $@"\b{System.Text.RegularExpressions.Regex.Escape(en)}\b";
            result = System.Text.RegularExpressions.Regex.Replace(
                result, pattern,
                match =>
                {
                    var token = $"⟦T{n++}⟧";
                    tokens[token] = zh;
                    return token;
                },
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }

        return result;
    }

    private static string RestoreGlossary(string text, Dictionary<string, string>? tokens = null)
    {
        if (tokens is { Count: > 0 })
        {
            // 机翻可能改动占位符里的括号，所以还原时用宽松匹配
            foreach (var (token, zh) in tokens)
            {
                var inner = token.Trim('⟦', '⟧');
                text = System.Text.RegularExpressions.Regex.Replace(
                    text, $@"[\[⟦（(]?\s*{System.Text.RegularExpressions.Regex.Escape(inner)}\s*[\]⟧）)]?",
                    zh, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            }
        }

        // LLM 与机翻都可能漏下几个占位符，兜底清掉，别让 ⟦T3⟧ 出现在正文里
        return System.Text.RegularExpressions.Regex.Replace(text, @"[\[⟦（(]?\s*T\d+\s*[\]⟧）)]?", " ");
    }

    /// <summary>
    /// 按段落边界切分长文本。
    /// 直接按字符数硬切会把单词和 markdown 结构切断，译文会出现莫名其妙的断句，
    /// 所以优先在换行处切，其次句号，最后才退化成硬切。
    /// </summary>
    private static List<string> SplitForApi(string text, int limit)
    {
        var chunks = new List<string>();
        if (text.Length <= limit) { chunks.Add(text); return chunks; }

        var start = 0;
        while (start < text.Length)
        {
            var remaining = text.Length - start;
            if (remaining <= limit) { chunks.Add(text[start..]); break; }

            var window = text.Substring(start, limit);
            var cut = window.LastIndexOf('\n');
            if (cut < limit / 3) cut = window.LastIndexOf(". ", StringComparison.Ordinal);
            if (cut < limit / 3) cut = window.LastIndexOf(' ');
            if (cut <= 0) cut = limit - 1;

            chunks.Add(text.Substring(start, cut + 1));
            start += cut + 1;
        }

        return chunks;
    }

    /// <summary>
    /// 规整 markdown：把连续空行压成一个、去掉行尾空格。
    /// 翻完再按行重组，结构不会被机翻搞乱。
    /// </summary>
    private static string NormalizeMarkdown(string text)
    {
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var sb = new StringBuilder();
        var blank = 0;
        foreach (var line in lines)
        {
            var t = line.TrimEnd();
            if (t.Length == 0)
            {
                blank++;
                if (blank > 1) continue;
            }
            else blank = 0;
            sb.Append(t).Append('\n');
        }
        return sb.ToString().Trim();
    }

    private static string Shorten(string s, int max = 180)
        => s.Length <= max ? s : s[..max] + "…";
}
