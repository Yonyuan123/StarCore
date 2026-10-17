using System.Text.Json;
using StarCore.Core.App;
using StarCore.Core.Models;

namespace StarCore.Core.Services;

/// <summary>
/// 游戏实例的增删改查。
///
/// 每个实例是一个自包含的目录，内部结构与官方 .minecraft 一致
/// （mods / resourcepacks / shaderpacks / saves / versions / libraries / assets），
/// 因此实例之间完全隔离，也可以整目录拷走直接用。
/// </summary>
public static class InstanceService
{
    private sealed class Store
    {
        public long ActiveId { get; set; }
        public long NextId { get; set; } = 1;
        public List<InstanceInfo> Instances { get; set; } = new();
    }

    private static readonly object Gate = new();
    private static Store _store = new();
    private static string? _instancesFile;

    private static string InstancesFile => _instancesFile ??= Path.Combine(StarCorePaths.DataRoot, "instances.json");

    /// <summary>子目录名 → 内容种类，内容扫描与目录创建共用同一份定义。</summary>
    private static readonly (string Folder, string Kind)[] Folders =
    {
        ("mods", "mod"),
        ("resourcepacks", "resourcepack"),
        ("shaderpacks", "shader"),
        ("saves", "world"),
        ("datapacks", "datapack"),
        ("backups", "backup"),
        ("config", "config"),
        ("versions", "version"),
        ("libraries", "library"),
        ("assets", "asset"),
        ("logs", "log"),
        ("crash-reports", "crash"),
    };

    public static IReadOnlyList<InstanceInfo> All
    {
        get { lock (Gate) return _store.Instances.ToList(); }
    }

    public static long? ActiveId
    {
        get { lock (Gate) return _store.Instances.Any(i => i.Id == _store.ActiveId) ? _store.ActiveId : _store.Instances.FirstOrDefault()?.Id; }
    }

    public static void Initialize()
    {
        // 先清掉上次没删干净的目录，再收纳 —— 顺序反了会把残留目录当成正常实例捡回来
        SweepDeletedDirs();

        lock (Gate)
        {
            try
            {
                var file = InstancesFile;
                if (File.Exists(file))
                    _store = Json.Deserialize<Store>(File.ReadAllText(file)) ?? new Store();
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "[instance] 读取实例索引失败");
                _store = new Store();
            }

            // 补齐磁盘上存在但索引里没有的实例目录（例如用户手工拷进来）
            AdoptOrphans();
        }
    }

    public static void Persist()
    {
        lock (Gate)
        {
            try { File.WriteAllText(InstancesFile, JsonSerializer.Serialize(_store, Json.Pretty)); }
            catch (Exception ex) { Logger.Error(ex, "[instance] 保存实例索引失败"); }
        }
    }

    /// <summary>列出实例，并附带派生统计（模组数、占用空间等）。</summary>
    public static List<InstanceInfo> List(bool withStats = true)
    {
        List<InstanceInfo> snapshot;
        lock (Gate) snapshot = _store.Instances.Select(Clone).ToList();

        if (!withStats) return snapshot;
        foreach (var i in snapshot) FillStats(i);
        return snapshot;
    }

    public static InstanceInfo? Get(long id)
    {
        lock (Gate)
        {
            var hit = _store.Instances.FirstOrDefault(i => i.Id == id);
            if (hit is null) return null;
            var copy = Clone(hit);
            FillStats(copy);
            return copy;
        }
    }

    /// <summary>
    /// 按 PCL 的命名习惯推一个默认实例名。
    ///
    /// 为什么要照抄 PCL 的规则：用户从别处迁移过来时，实例名是他最熟悉的东西，
    /// 一眼就能和 PCL 里的对得上；而且「1.20.1-Fabric_0.15.0」这种名字自带全部信息，
    /// 在实例列表里不需要再看第二行就知道这是什么。
    /// </summary>
    public static string SuggestName(string? versionId, string? loader, string? loaderVersion)
    {
        var baseName = string.IsNullOrWhiteSpace(versionId) ? "新实例" : versionId.Trim();
        var l = string.IsNullOrWhiteSpace(loader) ? "vanilla" : loader.Trim().ToLowerInvariant();

        // 加载器版本号里的 "+build.1" 之类后缀对用户没有意义，去掉
        var lv = (loaderVersion ?? "").Replace("+build", "").Trim();

        var suffix = l switch
        {
            "fabric"      => lv.Length > 0 ? $"-Fabric_{lv}" : "-Fabric",
            "legacyfabric"=> lv.Length > 0 ? $"-LegacyFabric_{lv}" : "-LegacyFabric",
            "quilt"       => lv.Length > 0 ? $"-Quilt_{lv}" : "-Quilt",
            "forge"       => lv.Length > 0 ? $"-Forge_{lv}" : "-Forge",
            "neoforge"    => lv.Length > 0 ? $"-NeoForge_{lv}" : "-NeoForge",
            "optifine"    => lv.Length > 0 ? $"-OptiFine_{lv}" : "-OptiFine",
            "liteloader"  => "-LiteLoader",
            _             => "",
        };

        return baseName + suffix;
    }

    /// <summary>
    /// 生成一个不与现有实例重名的名字。
    /// 「安装即建实例」意味着用户可能对同一个版本装多次（例如分别装 Forge 与 Fabric），
    /// 不加后缀就会撞名，之后在列表里根本分不清谁是谁。
    /// </summary>
    public static string UniqueName(string desired)
    {
        var name = desired;
        var n = 2;
        lock (Gate)
        {
            while (_store.Instances.Any(i => string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase)))
                name = $"{desired} ({n++})";
        }
        return name;
    }

    public static InstanceInfo Create(string name, string? versionId, string loader, string? loaderVersion = null)
    {
        InstanceInfo info;
        lock (Gate)
        {
            var id = _store.NextId++;
            info = new InstanceInfo
            {
                Id = id,
                Name = string.IsNullOrWhiteSpace(name) ? $"实例 {id}" : name.Trim(),
                VersionId = string.IsNullOrWhiteSpace(versionId) ? null : versionId,
                Loader = string.IsNullOrWhiteSpace(loader) ? "vanilla" : loader,
                LoaderVersion = loaderVersion,
                Path = Path.Combine(StarCorePaths.InstancesRoot, id.ToString()),
                MemoryMb = SuggestMemory(),
            };
            _store.Instances.Add(info);
            if (_store.ActiveId == 0) _store.ActiveId = id;
        }

        EnsureLayout(info.Path);
        Persist();
        Logger.Info($"[instance] 已创建实例 #{info.Id}「{info.Name}」");
        return info;
    }

    public static bool Remove(long id, bool deleteFiles)
    {
        InstanceInfo? target;
        lock (Gate)
        {
            target = _store.Instances.FirstOrDefault(i => i.Id == id);
            if (target is null) return false;
            _store.Instances.Remove(target);
            if (_store.ActiveId == id) _store.ActiveId = _store.Instances.FirstOrDefault()?.Id ?? 0;
        }
        Persist();
        InvalidateStats(target.Path);

        if (deleteFiles && !string.IsNullOrEmpty(target.Path))
        {
            // 先把这个实例上还在跑的任务（安装 / 下载）取消掉。
            // 不取消就删，下载线程手里的文件句柄还开着，目录删一半会失败，
            // 留下一个半截目录；而它下一轮启动又会被"收纳未登记目录"当正常实例捡回来。
            TaskCenter.CancelByOwner(id);
            DeleteInstanceDir(target.Path);
        }
        return true;
    }

    /// <summary>
    /// 删除实例目录。失败就多试几次 —— Windows 上"文件正被另一个线程写着"
    /// 是很常见的瞬时状态，退避重试基本都能过。
    /// 实在删不掉就把它改名成 <c>&lt;id&gt;.deleted-&lt;时间戳&gt;</c>：
    /// 这个名字不是纯数字，<see cref="AdoptOrphans"/> 会跳过它，
    /// 于是它不会再被当成一个正常实例冒出来；下次启动时会被清理掉。
    /// </summary>
    private static void DeleteInstanceDir(string path)
    {
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                if (!Directory.Exists(path)) return;
                Directory.Delete(path, true);
                Logger.Info($"[instance] 已删除实例目录 {path}");
                return;
            }
            catch (Exception ex)
            {
                if (attempt == 3)
                {
                    Logger.Warn($"[instance] 删除实例目录失败（第 {attempt} 次）：{ex.Message}");

                    try
                    {
                        var parked = $"{path}.deleted-{DateTime.Now:yyyyMMddHHmmss}";
                        Directory.Move(path, parked);
                        Logger.Warn($"[instance] 已把它改名为 {Path.GetFileName(parked)}，将在下次启动时清理");
                    }
                    catch (Exception ex2)
                    {
                        Logger.Error(ex2, $"[instance] 重命名残留实例目录也失败：{path}");
                    }
                    return;
                }
                Thread.Sleep(250 * attempt);
            }
        }
    }

    /// <summary>清掉上次没删干净的实例目录（<c>&lt;id&gt;.deleted-*</c>）。启动时调用。</summary>
    private static void SweepDeletedDirs()
    {
        try
        {
            if (!Directory.Exists(StarCorePaths.InstancesRoot)) return;
            foreach (var dir in Directory.EnumerateDirectories(StarCorePaths.InstancesRoot))
            {
                if (!Path.GetFileName(dir).Contains(".deleted-", StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    Directory.Delete(dir, true);
                    Logger.Info($"[instance] 已清理残留的已删除实例目录 {Path.GetFileName(dir)}");
                }
                catch (Exception ex)
                {
                    Logger.Debug($"[instance] 清理残留目录失败（下次再试）：{ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Debug($"[instance] 扫描残留实例目录失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 设置实例的自定义封面：把图片复制进数据目录 covers/ 下（以实例 id 命名），
    /// 记录相对路径。返回可直接给前端的 DataHost URL。
    /// </summary>
    public static string SetCustomIcon(long id, string sourcePath)
    {
        var t = _store.Instances.FirstOrDefault(i => i.Id == id)
                ?? throw new InvalidOperationException("实例不存在。");

        var ext = System.IO.Path.GetExtension(sourcePath).ToLowerInvariant();
        if (ext is not ".png" and not ".jpg" and not ".jpeg" and not ".webp" and not ".gif")
            throw new InvalidOperationException("仅支持 PNG / JPG / WEBP / GIF 图片。");

        var dir = System.IO.Path.Combine(StarCorePaths.DataRoot, "covers");
        System.IO.Directory.CreateDirectory(dir);
        var dest = System.IO.Path.Combine(dir, $"{id}{ext}");
        System.IO.File.Copy(sourcePath, dest, true);

        t.CustomCover = $"covers/{id}{ext}";
        Persist();
        return App.WebAssets.Data(t.CustomCover);
    }

    public static InstanceInfo? Update(long id, IDictionary<string, object?> patch)
    {
        lock (Gate)
        {
            var t = _store.Instances.FirstOrDefault(i => i.Id == id);
            if (t is null) return null;

            foreach (var (k, v) in patch)
            {
                switch (k)
                {
                    case "name": if (v?.ToString() is { Length: > 0 } n) t.Name = n.Trim(); break;
                    case "versionId": t.VersionId = string.IsNullOrWhiteSpace(v?.ToString()) ? null : v!.ToString(); break;
                    case "loader": if (v?.ToString() is { Length: > 0 } l) t.Loader = l; break;
                    case "loaderVersion": t.LoaderVersion = v?.ToString(); break;
                    case "memoryMb": t.MemoryMb = Math.Clamp(ToInt(v, t.MemoryMb), 512, 65536); break;
                    case "javaPath": t.JavaPath = string.IsNullOrWhiteSpace(v?.ToString()) ? null : v!.ToString(); break;
                    case "fullscreen": t.Fullscreen = ToBool(v, t.Fullscreen); break;
                    case "windowWidth": t.WindowWidth = Math.Clamp(ToInt(v, t.WindowWidth), 320, 16384); break;
                    case "windowHeight": t.WindowHeight = Math.Clamp(ToInt(v, t.WindowHeight), 240, 16384); break;
                    case "jvmArgs": t.JvmArgs = v?.ToString() ?? ""; break;
                    case "gameArgs": t.GameArgs = v?.ToString() ?? ""; break;
                    case "preLaunchCommand": t.PreLaunchCommand = string.IsNullOrWhiteSpace(v?.ToString()) ? null : v!.ToString(); break;
                    case "postLaunchCommand": t.PostLaunchCommand = string.IsNullOrWhiteSpace(v?.ToString()) ? null : v!.ToString(); break;
                    case "gameDirMode": t.GameDirMode = v?.ToString() == "shared" ? "shared" : "isolated"; break;
                    case "sharedGameDir": t.SharedGameDir = string.IsNullOrWhiteSpace(v?.ToString()) ? null : v!.ToString(); break;
                    case "hidden": t.Hidden = ToBool(v, t.Hidden); break;
                    default: Logger.Debug($"[instance] 忽略未知字段 {k}"); break;
                }
            }
            Persist();
            return Get(id);
        }
    }

    public static bool SetActive(long id)
    {
        lock (Gate)
        {
            if (_store.Instances.All(i => i.Id != id)) return false;
            _store.ActiveId = id;
        }
        Persist();
        return true;
    }

    /// <summary>复制实例：复制配置与模组，默认不复制存档（存档往往很大）。</summary>
    public static InstanceInfo? Duplicate(long id, string? newName)
    {
        var src = Get(id);
        if (src is null) return null;

        var copy = Create(newName ?? $"{src.Name} 副本", src.VersionId, src.Loader, src.LoaderVersion);
        Update(copy.Id, new Dictionary<string, object?>
        {
            ["memoryMb"] = src.MemoryMb,
            ["javaPath"] = src.JavaPath,
            ["fullscreen"] = src.Fullscreen,
            ["windowWidth"] = src.WindowWidth,
            ["windowHeight"] = src.WindowHeight,
            ["jvmArgs"] = src.JvmArgs,
            ["gameArgs"] = src.GameArgs,
        });

        try
        {
            CopyDir(Path.Combine(src.Path, "mods"), Path.Combine(copy.Path, "mods"));
            CopyDir(Path.Combine(src.Path, "config"), Path.Combine(copy.Path, "config"));
            CopyDir(Path.Combine(src.Path, "resourcepacks"), Path.Combine(copy.Path, "resourcepacks"));
            CopyDir(Path.Combine(src.Path, "shaderpacks"), Path.Combine(copy.Path, "shaderpacks"));
            Logger.Info($"[instance] 已复制实例 #{id} → #{copy.Id}");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "[instance] 复制实例内容时出错（配置已复制）");
        }

        return Get(copy.Id);
    }

    /* ==================================================================
       内容目录
       ================================================================== */

    // —— 目录派生：转发到 StarCorePaths，让调用方不必同时依赖两个类 ——

    public static string VersionDir(string instancePath) => StarCorePaths.VersionDir(instancePath);
    public static string LibraryDir(string instancePath) => StarCorePaths.LibraryDir(instancePath);
    public static string AssetDir(string instancePath) => StarCorePaths.AssetDir(instancePath);

    public static string ContentFolder(string instancePath, string kind)
    {
        var folder = Folders.FirstOrDefault(f => f.Kind == kind).Folder ?? kind;
        return Path.Combine(instancePath, folder);
    }

    public static void EnsureLayout(string instancePath)
    {
        foreach (var (folder, _) in Folders)
        {
            try { Directory.CreateDirectory(Path.Combine(instancePath, folder)); }
            catch (Exception ex) { Logger.Warn($"[instance] 创建目录失败 {folder}：{ex.Message}"); }
        }
    }

    /* ==================================================================
       统计
       ================================================================== */

    private static void FillStats(InstanceInfo info)
    {
        try
        {
            if (string.IsNullOrEmpty(info.Path) || !Directory.Exists(info.Path))
            {
                info.Installed = false;
                return;
            }

            info.ModCount = CountFiles(Path.Combine(info.Path, "mods"), "*.jar", "*.jar.disabled", "*.zip.disabled");
            info.ResourcePackCount = CountFiles(Path.Combine(info.Path, "resourcepacks"), "*.zip", "*.zip.disabled");
            info.ShaderCount = CountFiles(Path.Combine(info.Path, "shaderpacks"), "*.zip", "*.zip.disabled");
            info.WorldCount = CountDirs(Path.Combine(info.Path, "saves"));
            info.SizeBytes = DirSize(info.Path);

            /* "已安装"必须以**真的能启动**为准，而不是"versions 目录存在"。
               EnsureLayout 会无条件建出 versions/，所以旧判据对刚建好的空实例
               也返回 true，界面上就会出现"已安装但一点就崩"的实例，
               启动页的版本选择器里也混进一堆没装任何东西的实例。 */
            info.Installed = HasLaunchableVersion(info.Path);

            // 图标：装了加载器就显示加载器方块（那才是这个实例的身份），否则显示版本类型的方块
            info.LoaderIcon = Icons.ForLoader(info.Loader);
            var vt = info.VersionId is null ? null : VersionManifestService.Find(info.VersionId)?.Type;
            // 用户自定义封面优先；它存放在数据目录 covers/ 下，转成 DataHost URL 给前端
            info.Cover = string.IsNullOrWhiteSpace(info.CustomCover)
                ? Icons.ForInstance(info.VersionId, vt, info.Loader)
                : App.WebAssets.Data(info.CustomCover);
        }
        catch (Exception ex)
        {
            Logger.Debug($"[instance] 统计实例 {info.Id} 失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 这个实例里是否至少存在一个"完整"的版本 —— 版本描述与客户端主 jar 同时在。
    ///
    /// 只要求存在一个就够了：加载器版本（如 1.20.1-fabric-0.15.0）目录里通常没有 jar，
    /// jar 在它继承的原版目录（1.20.1/1.20.1.jar）里，所以逐目录成对判断最省事也最准确。
    /// </summary>
    private static bool HasLaunchableVersion(string instancePath)
    {
        try
        {
            var vdir = Path.Combine(instancePath, "versions");
            if (!Directory.Exists(vdir)) return false;

            foreach (var d in Directory.EnumerateDirectories(vdir))
            {
                var id = Path.GetFileName(d);
                if (string.IsNullOrEmpty(id)) continue;
                if (File.Exists(Path.Combine(d, id + ".json")) && File.Exists(Path.Combine(d, id + ".jar")))
                    return true;
            }
        }
        catch (Exception ex)
        {
            Logger.Debug($"[instance] 检查可用版本失败：{ex.Message}");
        }
        return false;
    }

    private static int CountFiles(string dir, params string[] patterns)
    {
        if (!Directory.Exists(dir)) return 0;
        var n = 0;
        foreach (var p in patterns) n += Directory.EnumerateFiles(dir, p).Count();
        return n;
    }

    private static int CountDirs(string dir)
        => Directory.Exists(dir) ? Directory.EnumerateDirectories(dir).Count() : 0;

    /* ------------------------------------------------------------------
       目录体积统计

       这是全项目最贵的单个操作：要给几万个文件各做一次 stat。
       而它挂在 List()（也就是 instance.list 这个前端每次进页面、每次
       instances.changed 都会调的方法）上 —— 无节制地重算足以让界面明显卡顿。

       所以加一层短 TTL 缓存：体积这种东西不需要实时精确，用户看到的
       "占用空间"晚一分钟更新完全无所谓，但不卡是能立刻感觉到的。
       ------------------------------------------------------------------ */

    private static readonly object SizeGate = new();
    private static readonly Dictionary<string, (long Bytes, DateTime At)> SizeCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>体积缓存的有效期。太短起不到作用，太长用户会以为统计坏了。</summary>
    private static readonly TimeSpan SizeTtl = TimeSpan.FromSeconds(60);

    /// <summary>
    /// 只取实例目录，不做任何统计。
    /// 给"只是想失效一下体积缓存"这种调用用 —— 走 <see cref="Get"/> 会顺手把
    /// 整个目录再遍历一遍，那就白缓存了。
    /// </summary>
    public static string? PathOf(long id)
    {
        lock (Gate) return _store.Instances.FirstOrDefault(i => i.Id == id)?.Path;
    }

    /// <summary>实例列表里的某个实例路径的内容变了，清掉它的体积缓存。</summary>
    public static void InvalidateStats(long instanceId) => InvalidateStats(PathOf(instanceId));

    /// <summary>某个实例的内容变了（装完版本、加删模组、导入整合包…）时清掉它的缓存。</summary>
    public static void InvalidateStats(string? instancePath)
    {
        if (string.IsNullOrEmpty(instancePath)) return;
        lock (SizeGate) SizeCache.Remove(instancePath);
    }

    private static long DirSize(string dir)
    {
        if (string.IsNullOrEmpty(dir)) return 0;

        lock (SizeGate)
        {
            if (SizeCache.TryGetValue(dir, out var hit) && DateTime.UtcNow - hit.At < SizeTtl)
                return hit.Bytes;
        }

        long total = 0;
        try
        {
            // 用枚举出来的 FileSystemInfo 而不是 new FileInfo(f)：
            // Windows 在目录项里已经带了文件大小，前者能直接读出来，
            // 后者会对每个文件再发一次查询 —— 上万个文件就是上万个 syscall。
            var root = new DirectoryInfo(dir);
            foreach (var fi in root.EnumerateFiles("*", SearchOption.AllDirectories))
            {
                try { total += fi.Length; } catch { /* 单文件失败跳过 */ }
            }
        }
        catch { /* 权限不足则返回已累加的部分 */ }

        lock (SizeGate)
        {
            // 只留最近用过的若干个，避免实例被删后缓存无限增长
            if (SizeCache.Count > 64) SizeCache.Clear();
            SizeCache[dir] = (total, DateTime.UtcNow);
        }
        return total;
    }

    private static void CopyDir(string src, string dst)
    {
        if (!Directory.Exists(src)) return;
        Directory.CreateDirectory(dst);
        foreach (var f in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(src, f);
            var target = Path.Combine(dst, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(f, target, true);
        }
    }

    /// <summary>按本机内存给出一个稳妥的默认值：一半，且落在 2–8 GB。</summary>
    private static int SuggestMemory()
    {
        var totalMb = (int)(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1024 / 1024);
        if (totalMb <= 0) totalMb = 8192;
        var half = totalMb / 2;
        return Math.Clamp(half, 2048, 8192);
    }

    private static InstanceInfo Clone(InstanceInfo i) => new()
    {
        Id = i.Id, Name = i.Name, VersionId = i.VersionId, Loader = i.Loader, LoaderVersion = i.LoaderVersion,
        Path = i.Path, MemoryMb = i.MemoryMb, JavaPath = i.JavaPath, Fullscreen = i.Fullscreen,
        WindowWidth = i.WindowWidth, WindowHeight = i.WindowHeight, JvmArgs = i.JvmArgs, GameArgs = i.GameArgs,
        PreLaunchCommand = i.PreLaunchCommand, PostLaunchCommand = i.PostLaunchCommand,
        CreatedAt = i.CreatedAt, LastPlayedAt = i.LastPlayedAt, TotalPlayTimeMs = i.TotalPlayTimeMs,
        // 这两个是派生字段，也要一起带上，否则列表会出现"图标时有时无"
        Cover = i.Cover, LoaderIcon = i.LoaderIcon,
    };

    public static void MarkPlayed(long id, DateTimeOffset start, DateTimeOffset end)
    {
        lock (Gate)
        {
            var t = _store.Instances.FirstOrDefault(i => i.Id == id);
            if (t is null) return;
            t.LastPlayedAt = end;
            t.TotalPlayTimeMs += (long)Math.Max(0, (end - start).TotalMilliseconds);
        }
        Persist();
    }

    /// <summary>把 instances 目录下未登记的实例目录纳入索引。</summary>
    private static void AdoptOrphans()
    {
        try
        {
            if (!Directory.Exists(StarCorePaths.InstancesRoot)) return;
            foreach (var dir in Directory.EnumerateDirectories(StarCorePaths.InstancesRoot))
            {
                var name = Path.GetFileName(dir);
                if (!long.TryParse(name, out var id)) continue;
                if (_store.Instances.Any(i => i.Id == id)) continue;

                _store.Instances.Add(new InstanceInfo { Id = id, Name = $"实例 {id}", Path = dir });
                if (id >= _store.NextId) _store.NextId = id + 1;
                Logger.Info($"[instance] 已收纳未登记的实例目录 #{id}");
            }
        }
        catch (Exception ex)
        {
            Logger.Debug($"[instance] 收纳实例目录时出错：{ex.Message}");
        }
    }

    private static int ToInt(object? v, int fallback) => v switch
    {
        int i => i, long l => (int)l, double d => (int)d,
        string s when int.TryParse(s, out var r) => r,
        JsonElement je when je.ValueKind == JsonValueKind.Number => je.GetInt32(),
        _ => fallback,
    };

    private static bool ToBool(object? v, bool fallback) => v switch
    {
        bool b => b,
        string s => s.Equals("true", StringComparison.OrdinalIgnoreCase),
        JsonElement je when je.ValueKind is JsonValueKind.True or JsonValueKind.False => je.GetBoolean(),
        _ => fallback,
    };
}
