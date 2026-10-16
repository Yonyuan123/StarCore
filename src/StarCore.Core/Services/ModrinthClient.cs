using System.IO.Compression;
using System.Text.Json;
using StarCore.Core.App;
using StarCore.Core.Models;

namespace StarCore.Core.Services;

/// <summary>
/// Modrinth 开放 API 客户端。
/// Modrinth 的接口不需要 API Key，且文档开放，作为资源来源比 CurseForge 更适合内置。
/// </summary>
public static class ModrinthClient
{
    private const string Base = "https://api.modrinth.com/v2";

    /// <summary>我们的 kind 与 Modrinth project_type 的映射。</summary>
    private static readonly Dictionary<string, string> TypeMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["mod"] = "mod",
        ["resourcepack"] = "resourcepack",
        ["shader"] = "shader",
        ["datapack"] = "datapack",
        ["modpack"] = "modpack",
        ["plugin"] = "plugin",
        // Modrinth 没有"世界"类目，用整合包兜底（整合包通常内含地图与配置），并在响应里给出说明
        ["world"] = "modpack",
    };

    private static readonly Dictionary<string, string> SortMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["relevance"] = "relevance",
        ["downloads"] = "downloads",
        ["follows"] = "follows",
        ["newest"] = "newest",
        ["updated"] = "updated",
    };

    public sealed class SearchResult
    {
        public List<Hit> Hits { get; set; } = new();
        public int Total { get; set; }
        public int Offset { get; set; }
        public int Limit { get; set; }
        public string? Note { get; set; }
    }

    public sealed class Hit
    {
        public string ProjectId { get; set; } = "";
        public string Slug { get; set; } = "";
        public string Title { get; set; } = "";
        public string Description { get; set; } = "";
        public string Author { get; set; } = "";
        public string? IconUrl { get; set; }
        public long Downloads { get; set; }
        public long Follows { get; set; }
        public DateTimeOffset? DateModified { get; set; }
        public List<string> Categories { get; set; } = new();
        public List<string> Versions { get; set; } = new();
        public string ProjectType { get; set; } = "";
    }

    /* ==================================================================
       搜索
       ================================================================== */

    public static async Task<SearchResult> SearchAsync(
        string kind, string? query, string? gameVersion, string? loader,
        string? sort, int offset, int limit, CancellationToken ct = default)
    {
        var type = TypeMap.TryGetValue(kind ?? "mod", out var t) ? t : "mod";
        var facets = new List<string> { $"\"project_type:{type}\"" };
        if (!string.IsNullOrWhiteSpace(gameVersion) && gameVersion != "all")
            facets.Add($"\"versions:{gameVersion}\"");
        if (!string.IsNullOrWhiteSpace(loader) && loader != "all")
            facets.Add($"\"categories:{loader}\"");

        var url = $"{Base}/search?query={Uri.EscapeDataString(query ?? "")}" +
                  $"&facets={Uri.EscapeDataString("[" + string.Join(",", facets.Select(f => $"[{f}]")) + "]")}" +
                  $"&index={(SortMap.TryGetValue(sort ?? "relevance", out var s) ? s : "relevance")}" +
                  $"&offset={Math.Max(0, offset)}&limit={Math.Clamp(limit, 1, 100)}";

        var json = await Http.GetStringAsync(url, ct);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var result = new SearchResult
        {
            Total = root.TryGetProperty("total_hits", out var th) ? th.GetInt32() : 0,
            Offset = offset,
            Limit = limit,
        };

        if (root.TryGetProperty("hits", out var hits) && hits.ValueKind == JsonValueKind.Array)
        {
            foreach (var h in hits.EnumerateArray())
            {
                result.Hits.Add(new Hit
                {
                    ProjectId = Str(h, "project_id"),
                    Slug = Str(h, "slug"),
                    Title = Str(h, "title"),
                    Description = Str(h, "description"),
                    Author = Str(h, "author"),
                    IconUrl = Str(h, "icon_url"),
                    Downloads = Long(h, "downloads"),
                    Follows = Long(h, "follows"),
                    DateModified = Date(h, "date_modified"),
                    Categories = StrList(h, "categories"),
                    Versions = StrList(h, "versions"),
                    ProjectType = Str(h, "project_type"),
                });
            }
        }

        if (kind == "world")
            result.Note = "Modrinth 没有独立的「世界」类目，这里展示的是整合包（通常内含地图与配置）。";

        return result;
    }

    /* ==================================================================
       详情
   ================================================================== */

    public static async Task<JsonElement?> ProjectAsync(string id, CancellationToken ct = default)
    {
        try
        {
            var json = await Http.GetStringAsync($"{Base}/project/{Uri.EscapeDataString(id)}", ct);
            return Json.DeserializeElement(json);
        }
        catch (Exception ex)
        {
            Logger.Warn($"[modrinth] 获取项目详情失败 {id}：{ex.Message}");
            return null;
        }
    }

    public static async Task<List<VersionInfo>> VersionsAsync(
        string projectId, string? gameVersion, string? loader, CancellationToken ct = default)
    {
        var url = $"{Base}/project/{Uri.EscapeDataString(projectId)}/version";
        var filters = new List<string>();
        if (!string.IsNullOrWhiteSpace(gameVersion) && gameVersion != "all")
            filters.Add($"game_versions=[\"{gameVersion}\"]");
        if (!string.IsNullOrWhiteSpace(loader) && loader != "all")
            filters.Add($"loaders=[\"{loader}\"]");
        if (filters.Count > 0) url += "?" + string.Join("&", filters);

        var json = await Http.GetStringAsync(url, ct);
        using var doc = JsonDocument.Parse(json);

        var list = new List<VersionInfo>();
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return list;

        foreach (var v in doc.RootElement.EnumerateArray())
        {
            var info = new VersionInfo
            {
                Id = Str(v, "id"),
                Name = Str(v, "name"),
                VersionNumber = Str(v, "version_number"),
                VersionType = Str(v, "version_type"),
                DatePublished = Date(v, "date_published"),
                Downloads = Long(v, "downloads"),
                GameVersions = StrList(v, "game_versions"),
                Loaders = StrList(v, "loaders"),
            };

            if (v.TryGetProperty("files", out var files) && files.ValueKind == JsonValueKind.Array)
            {
                foreach (var f in files.EnumerateArray())
                {
                    var primary = f.TryGetProperty("primary", out var p) && p.ValueKind == JsonValueKind.True;
                    var entry = new VersionFile
                    {
                        FileName = Str(f, "filename"),
                        Url = Str(f, "url"),
                        Size = Long(f, "size"),
                        Primary = primary,
                        Sha1 = f.TryGetProperty("hashes", out var hs) ? Str(hs, "sha1") : "",
                    };
                    info.Files.Add(entry);
                }
                info.Primary = info.Files.FirstOrDefault(f => f.Primary) ?? info.Files.FirstOrDefault();
            }

            if (v.TryGetProperty("dependencies", out var deps) && deps.ValueKind == JsonValueKind.Array)
            {
                foreach (var d in deps.EnumerateArray())
                {
                    info.Dependencies.Add(new VersionDependency
                    {
                        ProjectId = Str(d, "project_id"),
                        VersionId = Str(d, "version_id"),
                        DependencyType = Str(d, "dependency_type"),
                    });
                }
            }

            list.Add(info);
        }

        return list;
    }

    public sealed class VersionInfo
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string VersionNumber { get; set; } = "";
        public string VersionType { get; set; } = "release";
        public DateTimeOffset? DatePublished { get; set; }
        public long Downloads { get; set; }
        public List<string> GameVersions { get; set; } = new();
        public List<string> Loaders { get; set; } = new();
        public List<VersionFile> Files { get; set; } = new();
        public VersionFile? Primary { get; set; }
        public List<VersionDependency> Dependencies { get; set; } = new();
    }

    public sealed class VersionFile
    {
        public string FileName { get; set; } = "";
        public string Url { get; set; } = "";
        public long Size { get; set; }
        public bool Primary { get; set; }
        public string Sha1 { get; set; } = "";
    }

    public sealed class VersionDependency
    {
        public string ProjectId { get; set; } = "";
        public string VersionId { get; set; } = "";
        public string DependencyType { get; set; } = "required";
    }

    /* ==================================================================
       安装
       ================================================================== */

    public static async Task<OpResult> InstallAsync(
        string projectId, string? versionId, long instanceId, string kind,
        string? gameVersion, string? loader, CancellationToken ct = default)
    {
        var inst = InstanceService.Get(instanceId);
        if (inst is null) return OpResult.Fail("目标实例不存在。");

        var versions = await VersionsAsync(projectId, gameVersion, loader, ct);
        if (versions.Count == 0)
            return OpResult.Fail("没有与该实例版本 / 加载器兼容的版本。");

        var chosen = versionId is not null
            ? versions.FirstOrDefault(v => v.Id == versionId) ?? versions[0]
            : versions[0];

        if (chosen.Primary is null)
            return OpResult.Fail("该版本没有可下载的文件。");

        if (TypeMap.TryGetValue(kind, out var mt) && mt == "modpack")
            return await InstallModpackAsync(chosen, ct);

        var folder = InstanceService.ContentFolder(inst.Path, kind);
        Directory.CreateDirectory(folder);
        var dest = Path.Combine(folder, chosen.Primary.FileName);

        try
        {
            await Http.DownloadAsync(chosen.Primary.Url, dest, ct, chosen.Primary.Size > 0 ? chosen.Primary.Size : null);
            Logger.Info($"[modrinth] 已安装 {chosen.Primary.FileName} → {folder}");
            return OpResult.Success($"已安装 {chosen.Primary.FileName}", dest);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "[modrinth] 下载失败");
            return OpResult.Fail($"下载失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 安装整合包：下载 .mrpack，解析 modrinth.index.json，按其清单下载全部文件，
    /// 再创建一个新实例并把文件铺进去。
    /// </summary>
    public static async Task<OpResult> InstallModpackAsync(VersionInfo version, CancellationToken ct = default)
    {
        if (version.Primary is null) return OpResult.Fail("整合包缺少下载文件。");

        var tmpDir = Path.Combine(StarCorePaths.CacheRoot, "modpacks");
        Directory.CreateDirectory(tmpDir);
        var mrpack = Path.Combine(tmpDir, version.Primary.FileName);

        try
        {
            await Http.DownloadAsync(version.Primary.Url, mrpack, ct);
            return await ImportModpackFileAsync(mrpack, version.Name, ct);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "[modrinth] 整合包下载失败");
            return OpResult.Fail($"整合包下载失败：{ex.Message}");
        }
    }

    /// <summary>从本地 .mrpack / .zip 导入整合包，创建实例并铺设文件。</summary>
    public static async Task<OpResult> ImportModpackFileAsync(string file, string? nameHint, CancellationToken ct = default)
    {
        if (!File.Exists(file)) return OpResult.Fail("文件不存在。");

        var workDir = Path.Combine(StarCorePaths.CacheRoot, "modpack-extract", Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(workDir);

        try
        {
            ZipFile.ExtractToDirectory(file, workDir, true);
        }
        catch (Exception ex)
        {
            return OpResult.Fail($"无法解压整合包：{ex.Message}");
        }

        var indexFile = Path.Combine(workDir, "modrinth.index.json");
        if (!File.Exists(indexFile))
        {
            // 不是 Modrinth 格式：退化为"把 overrides 里的内容拷进一个新实例"
            return await ImportBareZipAsync(workDir, file, nameHint, ct);
        }

        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(indexFile, ct));
        var root = doc.RootElement;

        var packName = root.TryGetProperty("name", out var n) ? n.GetString() ?? "整合包" : "整合包";
        var gameVersion = "1.20.1";
        if (root.TryGetProperty("dependencies", out var deps) &&
            deps.TryGetProperty("minecraft", out var mc) && mc.ValueKind == JsonValueKind.String)
        {
            gameVersion = mc.GetString()!;
        }
        var loader = "vanilla";
        var loaderVersion = "";
        if (deps.ValueKind == JsonValueKind.Object)
        {
            foreach (var kv in deps.EnumerateObject())
            {
                if (kv.Name is "fabric-loader" or "quilt-loader" or "forge" or "neoforge")
                {
                    loader = kv.Name switch
                    {
                        "fabric-loader" => "fabric",
                        "quilt-loader" => "quilt",
                        "forge" => "forge",
                        _ => "neoforge",
                    };
                    loaderVersion = kv.Value.GetString() ?? "";
                }
            }
        }

        var inst = InstanceService.Create(nameHint ?? packName, gameVersion, loader, loaderVersion);
        InstanceService.EnsureLayout(inst.Path);

        // 1) 铺 overrides（整合包自带的配置与资源）
        foreach (var overlay in new[] { "overrides", "client-overrides" })
        {
            var src = Path.Combine(workDir, overlay);
            if (!Directory.Exists(src)) continue;
            CopyTree(src, inst.Path);
        }

        // 2) 按清单下载文件
        var jobs = new List<(string Url, string Dest, long Size)>();
        if (root.TryGetProperty("files", out var fs) && fs.ValueKind == JsonValueKind.Array)
        {
            foreach (var f in fs.EnumerateArray())
            {
                var path = f.TryGetProperty("path", out var p) ? p.GetString() : null;
                if (string.IsNullOrEmpty(path)) continue;
                if (!f.TryGetProperty("downloads", out var ds) || ds.ValueKind != JsonValueKind.Array) continue;

                var url = ds.EnumerateArray().FirstOrDefault().GetString();
                if (string.IsNullOrEmpty(url)) continue;

                var size = f.TryGetProperty("fileSize", out var sz) && sz.TryGetInt64(out var s) ? s : 0;
                jobs.Add((url, Path.Combine(inst.Path, path.Replace('/', Path.DirectorySeparatorChar)), size));
            }
        }

        var taskId = TaskCenter.Create($"导入整合包 {inst.Name}");
        TaskCenter.Start(taskId, "正在下载整合包内容…");

        var done = 0;
        var doneBytes = 0L;
        var totalBytes = jobs.Sum(j => j.Size);

        await Parallel.ForEachAsync(
            jobs,
            new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(SettingsService.Current.MaxThreads, 1, 32), CancellationToken = ct },
            async (job, token) =>
            {
                try
                {
                    var w = await Http.DownloadAsync(job.Url, job.Dest, token, job.Size > 0 ? job.Size : null);
                    Interlocked.Add(ref doneBytes, w);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Logger.Warn($"[modrinth] 整合包内文件下载失败：{ex.Message}");
                }

                var d = Interlocked.Increment(ref done);
                TaskCenter.Progress(taskId, d, jobs.Count, Interlocked.Read(ref doneBytes), totalBytes, Path.GetFileName(job.Dest));
            });

        TaskCenter.Complete(taskId, "整合包导入完成");

        try { Directory.Delete(workDir, true); } catch { }

        Logger.Info($"[modrinth] 整合包已导入为实例 #{inst.Id}「{inst.Name}」");
        return OpResult.Success($"已导入整合包「{inst.Name}」", inst.Path);
    }

    private static Task<OpResult> ImportBareZipAsync(string workDir, string file, string? nameHint, CancellationToken ct)
    {
        try
        {
            var name = nameHint ?? Path.GetFileNameWithoutExtension(file);
            var inst = InstanceService.Create(name, null, "vanilla");
            InstanceService.EnsureLayout(inst.Path);

            // 常见的几种布局都尝试一遍
            foreach (var candidate in new[] { "overrides", ".minecraft", "" })
            {
                var src = string.IsNullOrEmpty(candidate) ? workDir : Path.Combine(workDir, candidate);
                if (Directory.Exists(src)) CopyTree(src, inst.Path);
            }

            Logger.Info($"[modrinth] 已按通用 zip 导入为实例 #{inst.Id}");
            return Task.FromResult(OpResult.Success($"已导入「{inst.Name}」（未识别的格式，已尽力还原目录结构）", inst.Path));
        }
        catch (Exception ex)
        {
            return Task.FromResult(OpResult.Fail($"导入失败：{ex.Message}"));
        }
    }

    private static void CopyTree(string src, string dst)
    {
        foreach (var f in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(src, f);
            var target = Path.Combine(dst, rel);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(f, target, true);
            }
            catch (Exception ex)
            {
                Logger.Debug($"[modrinth] 复制失败 {rel}：{ex.Message}");
            }
        }
    }

    /* ==================================================================
       JSON 取值
       ================================================================== */

    private static string Str(JsonElement el, string name)
        => el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? "" : "";

    private static long Long(JsonElement el, string name)
        => el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) && v.TryGetInt64(out var l) ? l : 0;

    private static DateTimeOffset? Date(JsonElement el, string name)
        => el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) && v.TryGetDateTimeOffset(out var d) ? d : null;

    private static List<string> StrList(JsonElement el, string name)
    {
        var list = new List<string>();
        if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array)
        {
            foreach (var i in v.EnumerateArray())
                if (i.ValueKind == JsonValueKind.String) list.Add(i.GetString()!);
        }
        return list;
    }
}
