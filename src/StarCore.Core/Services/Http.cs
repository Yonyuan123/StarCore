using System.Net;
using System.Net.Http.Headers;
using StarCore.Core.App;

namespace StarCore.Core.Services;

/// <summary>
/// 全应用共用的 HTTP 出口。
/// 复用单个 HttpClient（避免 socket 耗尽），并集中处理代理、重试与下载进度。
/// </summary>
public static class Http
{
    private static readonly SemaphoreSlim Swap = new(1, 1);
    private static string _proxy = "";

    /// <summary>
    /// 注意：这里必须是计算属性而不是自动初始化的静态字段。
    /// 静态字段按声明顺序初始化，若 <c>_client</c> 声明在前，Build() 里读到的
    /// UserAgent 还是 null，请求头解析会抛异常（整个类初始化失败）。
    /// </summary>
    public static string UserAgent =>
        $"StarCore/{StarCorePaths.ProductVersion} (Minecraft Launcher; +https://github.com/starcore)";

    private static HttpClient _client = Build(null);

    public static HttpClient Client => _client;

    private static HttpClient Build(string? proxy)
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = true,
            MaxConnectionsPerServer = 64,
        };

        if (!string.IsNullOrWhiteSpace(proxy))
        {
            try { handler.Proxy = new WebProxy(proxy) { BypassProxyOnLocal = true }; handler.UseProxy = true; }
            catch (Exception ex) { Logger.Warn($"[http] 代理地址无效，已忽略：{proxy} ({ex.Message})"); }
        }

        var c = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(10) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        c.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));
        return c;
    }

    /// <summary>设置代理。地址变化时重建客户端。</summary>
    public static async Task SetProxyAsync(string proxy)
    {
        if (string.Equals(proxy ?? "", _proxy, StringComparison.OrdinalIgnoreCase)) return;
        await Swap.WaitAsync();
        try
        {
            var old = _client;
            _proxy = proxy ?? "";
            _client = Build(_proxy);
            old.Dispose();
            Logger.Info($"[http] 代理已更新：{(string.IsNullOrEmpty(_proxy) ? "直连" : _proxy)}");
        }
        finally { Swap.Release(); }
    }

    /// <summary>带重试的 GET 文本。下载源抖动时不必让整个流程失败。</summary>
    public static async Task<string> GetStringAsync(string url, CancellationToken ct = default, int retries = 3)
    {
        Exception? last = null;
        for (var attempt = 1; attempt <= retries; attempt++)
        {
            try
            {
                using var res = await _client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
                res.EnsureSuccessStatusCode();
                return await res.Content.ReadAsStringAsync(ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                last = ex;
                Logger.Warn($"[http] GET 失败（第 {attempt}/{retries} 次）：{url} — {ex.Message}");
                if (attempt < retries) await Task.Delay(TimeSpan.FromMilliseconds(300 * attempt), ct);
            }
        }
        throw new InvalidOperationException($"请求失败：{url}", last);
    }

    public static async Task<byte[]> GetBytesAsync(string url, CancellationToken ct = default, int retries = 3)
    {
        Exception? last = null;
        for (var attempt = 1; attempt <= retries; attempt++)
        {
            try
            {
                using var res = await _client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
                res.EnsureSuccessStatusCode();
                return await res.Content.ReadAsByteArrayAsync(ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                last = ex;
                if (attempt < retries) await Task.Delay(TimeSpan.FromMilliseconds(300 * attempt), ct);
            }
        }
        throw new InvalidOperationException($"请求失败：{url}", last);
    }

    /* ==================================================================
       下载

       两级并行：
         · 文件级 —— 一批小文件用 Parallel.ForEachAsync 同时拉（调用方控制并发度）；
         · 文件内 —— 单个大文件用 HTTP Range 切成多段同时拉，最后合并。

       为什么需要第二级：Minecraft 的 client.jar 有二十多 MB，资源包和整合包更大。
       在 32 个并发工作线程下，如果一个任务只负责这一个文件，那 31 个线程都在空转，
       而它自己还是单流跑满一条连接 —— 带宽根本吃不满。
       ================================================================== */

    /// <summary>小于这个大小就不值得分块了，请求开销比收益还大。</summary>
    private const long ChunkedThreshold = 2 * 1024 * 1024;

    /// <summary>单块最小大小。块太小会让请求开销占比过高。</summary>
    private const long MinChunkSize = 256 * 1024;

    /// <summary>单个文件最多切多少块。再多也不会更快，反而容易被 CDN 限流。</summary>
    private const int MaxChunks = 16;

    /// <summary>
    /// 一段时间内一个字节都没收到，就认定这条连接僵住了。
    ///
    /// 为什么必须有：镜像站（BMCLAPI 这类）经常在发完 Range 数据后**不断开连接**，
    /// 或者干脆半死不活地吊着。上一次没有这个开关时，25MB 的 client.jar 下载
    /// 会卡到 HttpClient 的 10 分钟总超时才失败 —— 用户看到的就是"进度条不动了"。
    /// 实测现场：8 个分片里 5 个已经收满字节数却全都停在"等 EOF"上，整个安装僵死。
    /// </summary>
    private const int StallTimeoutMs = 20_000;

    /// <summary>
    /// 下载到文件。已存在且大小一致的直接跳过 —— 这是让"断点重下"变得廉价的关键：
    /// 安装中断后再点一次，只会补缺失的文件。
    /// </summary>
    /// <param name="maxParallel">单个文件内部的分块并发数。1 表示单流。</param>
    /// <param name="onDelta">每写到一段数据就回调一次"这次写了多少字节"，用于实时显示速度。</param>
    public static async Task<long> DownloadAsync(
        string url, string dest, CancellationToken ct = default,
        long? expectSize = null, int maxParallel = 1, Action<long>? onDelta = null)
    {
        if (File.Exists(dest))
        {
            var len = new FileInfo(dest).Length;
            if (expectSize is > 0 && len == expectSize) return 0;
            if (expectSize is null && len > 0) return 0;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);

        // 先探测：这个资源支不支持 Range、总长度是多少。
        // 用 bytes=0-1 的最小请求探测，成本可以忽略。
        if (maxParallel >= 2)
        {
            var probe = await ProbeRangeAsync(url, ct);
            if (probe is { SupportsRanges: true, Length: > 0 } info
                && info.Length >= ChunkedThreshold
                && (expectSize is null or <= 0 || expectSize == info.Length))
            {
                try
                {
                    return await DownloadChunkedAsync(url, dest, info.Length, maxParallel, ct, onDelta);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    // 分块失败就退回单流：宁可慢一点，也不能让用户装不上。
                    // 分片的清理由 DownloadChunkedAsync 自己的 finally 负责，
                    // 这里不要再扫 .part* —— 那会把并发的另一次下载的活分片删掉。
                    Logger.Warn($"[http] 分块下载失败，回退单流：{ex.Message}");
                }
            }
        }

        return await DownloadSingleAsync(url, dest, ct, expectSize, onDelta);
    }

    private static async Task<(bool SupportsRanges, long Length)?> ProbeRangeAsync(string url, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 1);

            using var res = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if (res.StatusCode != System.Net.HttpStatusCode.PartialContent) return (false, 0);

            // Content-Range: bytes 0-1/12345678 —— 总长度在斜杠后面
            var total = res.Content.Headers.ContentRange?.Length
                        ?? res.Content.Headers.ContentLength
                        ?? 0;
            return (true, total);
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            return null;   // 探测失败不当成错误，交给单流路径
        }
    }

    private static async Task<long> DownloadChunkedAsync(
        string url, string dest, long length, int maxParallel, CancellationToken ct, Action<long>? onDelta)
    {
        var chunks = Math.Clamp((int)(length / MinChunkSize), 2, Math.Min(maxParallel, MaxChunks));
        var per = length / chunks;
        var parts = new string[chunks];

        /* ✦ 分片名必须带上"本次尝试"的标识。
           踩过的坑：分片名原本固定是 <dest>.part0 … <dest>.partN，而清理逻辑是
           按 `<dest>.part*` 通配删除。于是当同一个文件被并发下载两次
           （用户连点两次安装、或两个任务命中同一个库），先完成的那次会在
           finally 里把另一次**正在写**的分片删掉，另一次合并时报
           「Could not find file '…\icu4j-73.2.jar.part0'」，
           回退单流、拖慢整批，运气差时直接缺文件。
           加上唯一前缀后，每次尝试只碰自己的分片，天然互不干扰。 */
        var token = Guid.NewGuid().ToString("N")[..8];
        var prefix = $"{dest}.part{token}-";

        Logger.Debug($"[http] 分块下载 {Path.GetFileName(dest)}：{length / 1024 / 1024.0:F1}MB / {chunks} 块");

        try
        {
            await Parallel.ForEachAsync(
                Enumerable.Range(0, chunks),
                new ParallelOptions { MaxDegreeOfParallelism = chunks, CancellationToken = ct },
                async (i, tk) =>
                {
                    var start = (long)i * per;
                    // 最后一块吃掉除不尽的部分，保证覆盖到文件末尾
                    var end = i == chunks - 1 ? length - 1 : start + per - 1;

                    // Range 请求的字节数是**确定**的，就地把它算出来。
                    // 绝不能靠"读到 EOF 才停"：镜像站发完数据后不关连接是常态，
                    // 那样这个分片会永远卡在 ReadAsync 上（见 StallTimeoutMs 的说明）。
                    var want = end - start + 1;

                    var partPath = $"{prefix}{i}";
                    parts[i] = partPath;

                    using var req = new HttpRequestMessage(HttpMethod.Get, url);
                    req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(start, end);

                    using var res = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, tk);
                    if (res.StatusCode != System.Net.HttpStatusCode.PartialContent)
                        throw new InvalidOperationException($"服务器未按 Range 返回（HTTP {(int)res.StatusCode}）");

                    // 每收到一段数据就把停滞计时器往后推；真僵住了就由它来中止，
                    // 从而落到 DownloadAsync 的单流回退路径，而不是干等总超时。
                    using var stall = CancellationTokenSource.CreateLinkedTokenSource(tk);
                    stall.CancelAfter(StallTimeoutMs);

                    await using var src = await res.Content.ReadAsStreamAsync(tk);
                    await using var fs = new FileStream(partPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);

                    var buf = new byte[81920];
                    long got = 0;

                    try
                    {
                        while (got < want)
                        {
                            // 只读"还差多少"，绝不越过本块的范围
                            var room = (int)Math.Min(buf.Length, want - got);
                            var read = await src.ReadAsync(buf.AsMemory(0, room), stall.Token);
                            if (read <= 0) break;

                            await fs.WriteAsync(buf.AsMemory(0, read), stall.Token);
                            got += read;
                            onDelta?.Invoke(read);
                            stall.CancelAfter(StallTimeoutMs);
                        }
                    }
                    catch (OperationCanceledException) when (!tk.IsCancellationRequested)
                    {
                        // 停滞超时（不是用户取消）。抛非取消异常，让上层走单流回退。
                        throw new TimeoutException($"分片 {i} 停滞超过 {StallTimeoutMs / 1000} 秒（已收 {got}/{want} 字节）");
                    }

                    if (got != want)
                        throw new IOException($"分片 {i} 数据不完整：{got}/{want} 字节");
                });

            // 合并：顺序写入最终文件。块是按序存放的，合并不会成为瓶颈。
            // 先写临时文件再原子改名，避免合并到一半被打断留下半截的 jar
            // （jar 被当成"已存在且大小一致"而跳过，是最难查的一类问题）。
            var merged = prefix + "merged";
            await using (var outFs = new FileStream(merged, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
            {
                foreach (var part in parts)
                {
                    if (!File.Exists(part))
                    {
                        // 分片不该丢。丢了就把现场打出来，别只留一句 "Could not find file" ——
                        // 那样根本分不清是"没写出来"还是"被别人删了"。
                        var dir = Path.GetDirectoryName(prefix)!;
                        var siblings = Directory.Exists(dir)
                            ? string.Join(", ", Directory.EnumerateFiles(dir, Path.GetFileName(dest) + ".part*").Select(Path.GetFileName))
                            : "(目录不存在)";
                        Logger.Error($"[http] 分片缺失 {Path.GetFileName(part)}；同名前缀的分片：{siblings}");
                        throw new FileNotFoundException($"下载分片丢失：{Path.GetFileName(part)}", part);
                    }

                    await using var inFs = new FileStream(part, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
                    await inFs.CopyToAsync(outFs, ct);
                }
            }
            File.Move(merged, dest, true);

            return length;
        }
        finally
        {
            CleanupParts(prefix);
        }
    }

    private static async Task<long> DownloadSingleAsync(
        string url, string dest, CancellationToken ct, long? expectSize, Action<long>? onDelta)
    {
        using var res = await _client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        res.EnsureSuccessStatusCode();

        // 临时名同样带唯一标识：同一个文件被并发下载时不会互相写坏
        var tmp = $"{dest}.part{Guid.NewGuid():N}.tmp";
        long written = 0;

        // 已知期望大小时，读满就收工 —— 理由和分块那边一样：
        // 服务端发完数据不断连接是常态，靠 EOF 判断结束会白等十分钟。
        var want = expectSize is > 0 ? expectSize.Value : (long?)null;

        using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
        stall.CancelAfter(StallTimeoutMs);

        try
        {
            await using (var src = await res.Content.ReadAsStreamAsync(ct))
            await using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
            {
                var buf = new byte[81920];
                while (want is null || written < want)
                {
                    var room = want is null ? buf.Length : (int)Math.Min(buf.Length, want.Value - written);
                    int read;
                    try
                    {
                        read = await src.ReadAsync(buf.AsMemory(0, room), stall.Token);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        throw new TimeoutException($"下载停滞超过 {StallTimeoutMs / 1000} 秒（已收 {written} 字节）");
                    }

                    if (read <= 0) break;
                    await fs.WriteAsync(buf.AsMemory(0, read), ct);
                    written += read;
                    onDelta?.Invoke(read);
                    stall.CancelAfter(StallTimeoutMs);
                }
            }

            if (want is not null && written != want)
                throw new IOException($"下载不完整：{written}/{want} 字节");

            File.Move(tmp, dest, true);
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
        }

        return written;
    }

    /// <summary>
    /// 清掉"本次分块下载"留下的所有分片（含合并用的中间文件）。
    /// <paramref name="prefix"/> 是带本次尝试唯一标识的完整前缀，
    /// 因此绝不会碰到并发进行的另一次下载。
    /// </summary>
    private static void CleanupParts(string prefix)
    {
        try
        {
            var dir = Path.GetDirectoryName(prefix);
            if (string.IsNullOrEmpty(dir)) return;
            var pattern = Path.GetFileName(prefix) + "*";
            foreach (var f in Directory.EnumerateFiles(dir, pattern))
            {
                try { File.Delete(f); } catch { }
            }
        }
        catch { }
    }

    /// <summary>
    /// 回收孤儿分片：进程上次没跑完留在磁盘上的 <c>*.part*</c>。
    ///
    /// 分片名带唯一标识之后就不会互相覆盖了，代价是"上一次崩掉留下的分片"
    /// 再也没人去清。所以在启动时按"超过 1 小时未改动"扫一遍删掉 ——
    /// 正在进行的下载不可能超过 1 小时没写过字节。
    /// </summary>
    public static void SweepOrphanParts(string root)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return;

        var cutoff = DateTime.UtcNow.AddHours(-1);
        var removed = 0;

        try
        {
            foreach (var f in Directory.EnumerateFiles(root, "*.part*", SearchOption.AllDirectories))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(f) < cutoff) { File.Delete(f); removed++; }
                }
                catch { /* 被占用就留着，下次再说 */ }
            }
        }
        catch (Exception ex)
        {
            Logger.Debug($"[http] 清理孤儿分片失败：{ex.Message}");
        }

        if (removed > 0) Logger.Info($"[http] 已清理 {removed} 个残留的下载分片");
    }

    public static string Combine(string baseUrl, string relative)
        => baseUrl.TrimEnd('/') + "/" + relative.TrimStart('/');
}
