using System.IO.Compression;
using System.Text;

using StarCore.Core.App;

namespace StarCore.Core.Services;

/// <summary>
/// 存档与世界管理（第 5 批）：读取 level.dat、备份 / 恢复 / 删除 / 重命名。
///
/// level.dat 是 gzip 压缩的 NBT。这里只做<strong>只读</strong>解析 ——
/// NBT 的写入没有官方规范文档，写坏一个字节整个存档就没了，
/// 所以凡是"改"类操作（重命名等）都通过改目录名实现，绝不重写 level.dat。
/// </summary>
public static class WorldService
{
    // ------------------------------------------------------------------
    // 对外模型
    // ------------------------------------------------------------------

    public sealed class WorldInfo
    {
        public string DirName { get; set; } = "";
        public string Name { get; set; } = "";
        public long LastPlayed { get; set; }        // ms epoch；0 表示未知
        public string GameType { get; set; } = "";  // 生存/创造/冒险/旁观
        public string Difficulty { get; set; } = "";// 和平/简单/普通/困难
        public string VersionName { get; set; } = "";
        public bool Hardcore { get; set; }
        public long SizeBytes { get; set; }
    }

    public sealed class BackupInfo
    {
        public string File { get; set; } = "";
        public string Name { get; set; } = "";
        public long SizeBytes { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
    }

    /// <summary>
    /// 启动前自动备份：只备份自上次备份后有改动的存档（按 level.dat / region
    /// 目录的最近写入时间比较），每个存档保留最近 5 份，超出删最旧。
    /// 全量每次都备会让几十 GB 的存档把启动拖到不可用。
    /// </summary>
    public static void AutoBackup(string gameDir)
    {
        var saves = Path.Combine(gameDir, "saves");
        if (!Directory.Exists(saves)) return;

        var idDir = Path.Combine(StarCorePaths.DataRoot, "backups", "worlds", InstanceIdOf(gameDir));
        foreach (var world in Directory.EnumerateDirectories(saves))
        {
            var name = Path.GetFileName(world);
            try
            {
                // 判断"有没有改动"：取存档内最近的文件写入时间，与最近一份备份的时间比较
                var latest = Directory.EnumerateFiles(world, "*", SearchOption.AllDirectories)
                    .Select(File.GetLastWriteTimeUtc).DefaultIfEmpty(DateTime.MinValue).Max();
                var lastBackup = Directory.EnumerateFiles(idDir, $"{name}_*.zip")
                    .Select(File.GetLastWriteTimeUtc).DefaultIfEmpty(DateTime.MinValue).Max();

                if (latest <= lastBackup) continue; // 没动过，不用备

                var files = Directory.EnumerateFiles(idDir, $"{name}_*.zip").OrderBy(f => f).ToList();
                if (files.Count >= 5)
                {
                    try { File.Delete(files[0]); } catch { }
                }

                BackupByDir(gameDir, world);
                Logger.Info($"[world] 启动前已自动备份存档 {name}");
            }
            catch (Exception ex)
            {
                Logger.Warn($"[world] 自动备份 {name} 失败：{ex.Message}");
            }
        }
    }

    private static void BackupByDir(string gameDir, string worldDir)
    {
        var dir = Path.Combine(StarCorePaths.DataRoot, "backups", "worlds", InstanceIdOf(gameDir));
        Directory.CreateDirectory(dir);
        var stamp = DateTimeOffset.Now.ToString("yyyy-MM-dd_HH-mm-ss");
        var file = Path.Combine(dir, $"{Path.GetFileName(worldDir)}_{stamp}.zip");
        using var zip = ZipFile.Open(file, ZipArchiveMode.Create);
        foreach (var f in Directory.EnumerateFiles(worldDir, "*", SearchOption.AllDirectories))
        {
            if (Path.GetFileName(f).Equals("session.lock", StringComparison.OrdinalIgnoreCase)) continue;
            zip.CreateEntryFromFile(f, Path.GetRelativePath(worldDir, f).Replace('\\', '/'), CompressionLevel.Fastest);
        }
    }

    // ------------------------------------------------------------------
    // 列表与读取
    // ------------------------------------------------------------------

    public static List<WorldInfo> List(string instancePath)
    {
        var result = new List<WorldInfo>();
        var saves = Path.Combine(instancePath, "saves");
        if (!Directory.Exists(saves)) return result;

        foreach (var dir in Directory.EnumerateDirectories(saves).OrderByDescending(DirLastPlayed))
        {
            var info = new WorldInfo { DirName = Path.GetFileName(dir) };
            try
            {
                ReadLevelDat(dir, info);
            }
            catch (Exception ex)
            {
                // level.dat 读不出来（损坏/非标准）不影响其它存档；名字用目录名兜底
                Logger.Debug($"[world] level.dat 解析失败 {info.DirName}：{ex.Message}");
                info.Name ??= info.DirName;
            }
            info.Name = string.IsNullOrWhiteSpace(info.Name) ? info.DirName : info.Name;
            info.SizeBytes = InstanceService.DirSize(dir);
            result.Add(info);
        }
        return result;
    }

    private static long DirLastPlayed(string dir)
    {
        try { return File.GetLastWriteTimeUtc(Path.Combine(dir, "level.dat")).Ticks; }
        catch { return 0; }
    }

    /// <summary>
    /// 给内容列表用的世界摘要标签（游戏模式 · 难度 / 版本 / 最后游玩）。
    /// 挂掉就返回空列表 —— 标签只是锦上添花，不能因为它把列表搞炸。
    /// </summary>
    public static List<string> PeekTags(string worldDir)
    {
        var info = new WorldInfo { DirName = Path.GetFileName(worldDir) };
        try { ReadLevelDat(worldDir, info); } catch { return []; }

        var tags = new List<string>();
        var mode = info.GameType;
        if (info.Hardcore) mode = "极限";
        if (!string.IsNullOrEmpty(mode))
        {
            var t1 = mode;
            if (!string.IsNullOrEmpty(info.Difficulty) && !info.Hardcore) t1 += $" · {info.Difficulty}";
            tags.Add(t1);
        }
        if (!string.IsNullOrEmpty(info.VersionName)) tags.Add(info.VersionName);
        if (info.LastPlayed > 0)
            tags.Add(DateTimeOffset.FromUnixTimeMilliseconds(info.LastPlayed).ToLocalTime().ToString("yyyy-MM-dd HH:mm") + " 游玩");
        return tags;
    }

    // ------------------------------------------------------------------
    // 备份 / 恢复 / 删除 / 重命名
    // ------------------------------------------------------------------

    /// <summary>把整个存档目录压成 zip，放进数据目录 backups/worlds/。返回备份文件全路径。</summary>
    public static string Backup(string instancePath, string worldDir, CancellationToken ct = default)
    {
        var src = Path.Combine(instancePath, "saves", worldDir);
        if (!Directory.Exists(src))
            throw new InvalidOperationException("存档不存在。");

        var dir = Path.Combine(StarCorePaths.DataRoot, "backups", "worlds", InstanceIdOf(instancePath));
        Directory.CreateDirectory(dir);
        var stamp = DateTimeOffset.Now.ToString("yyyy-MM-dd_HH-mm-ss");
        var file = Path.Combine(dir, $"{worldDir}_{stamp}.zip");

        // 手动枚举而不是 CreateFromDirectory：条目名统一用 /，
        // 并且跳过会话锁文件（游戏正在运行时 session.lock 可能被占用）。
        using var zip = ZipFile.Open(file, ZipArchiveMode.Create);
        foreach (var f in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            if (Path.GetFileName(f).Equals("session.lock", StringComparison.OrdinalIgnoreCase)) continue;
            zip.CreateEntryFromFile(f, Path.GetRelativePath(src, f).Replace('\\', '/'), CompressionLevel.Optimal);
        }

        Logger.Info($"[world] 已备份存档 {worldDir} → {file}");
        return file;
    }

    /// <summary>
    /// 从备份恢复存档。恢复前把当前的存档目录改名保留（worldName.pre-restore-时间戳），
    /// 恢复失败也能手动改回来 —— 恢复操作本身就是"用户存档可能坏了"的场景，
    /// 这个过程绝不能再破坏任何东西。
    /// </summary>
    public static string Restore(string instancePath, string worldDir, string backupFile)
    {
        var saves = Path.Combine(instancePath, "saves");
        var target = Path.Combine(saves, worldDir);
        if (!File.Exists(backupFile))
            throw new InvalidOperationException("备份文件不存在。");
        // 只允许恢复数据目录 backups/ 下的备份：这是内核方法，不能变成任意文件读取器
        if (!Path.GetFullPath(backupFile).StartsWith(
                Path.GetFullPath(Path.Combine(StarCorePaths.DataRoot, "backups")) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("备份文件路径无效。");

        Directory.CreateDirectory(saves);

        // 现有存档改名保留
        if (Directory.Exists(target))
        {
            var keep = target + $".pre-restore-{DateTimeOffset.Now:yyyyMMdd-HHmmss}";
            Directory.Move(target, keep);
            Logger.Info($"[world] 恢复前已保留当前存档为 {Path.GetFileName(keep)}");
        }

        try
        {
            using var zip = ZipFile.OpenRead(backupFile);
            foreach (var entry in zip.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name)) continue; // 目录条目
                var dest = Path.Combine(target, entry.FullName.Replace('/', Path.DirectorySeparatorChar));
                // 防路径穿越：解压出来的文件必须还在 target 里面
                if (!Path.GetFullPath(dest).StartsWith(Path.GetFullPath(target) + Path.DirectorySeparatorChar,
                        StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("备份内包含非法路径，已中止恢复。");
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                entry.ExtractToFile(dest, true);
            }
            Logger.Info($"[world] 存档 {worldDir} 已从 {Path.GetFileName(backupFile)} 恢复");
            return $"已恢复。恢复前的存档保留在 {worldDir}.pre-restore-… 目录，确认无误后可手动删除。";
        }
        catch
        {
            // 恢复失败：把保留的旧存档放回去，不能让用户两头落空
            var kept = Directory.GetDirectories(saves, $"{worldDir}.pre-restore-*")
                .OrderByDescending(d => d).FirstOrDefault();
            if (kept is not null && !Directory.Exists(target))
                Directory.Move(kept, target);
            throw;
        }
    }

    public static List<BackupInfo> ListBackups(string instancePath, string worldDir)
    {
        var result = new List<BackupInfo>();
        var dir = Path.Combine(StarCorePaths.DataRoot, "backups", "worlds", InstanceIdOf(instancePath));
        if (!Directory.Exists(dir)) return result;
        foreach (var f in Directory.EnumerateFiles(dir, $"{worldDir}_*.zip"))
        {
            result.Add(new BackupInfo
            {
                File = f,
                Name = Path.GetFileName(f),
                SizeBytes = new FileInfo(f).Length,
                CreatedAt = DateTimeOffset.Now - (DateTimeOffset.Now - File.GetLastWriteTime(f)),
            });
        }
        return result.OrderByDescending(b => b.CreatedAt).ToList();
    }

    public static void DeleteBackup(string backupFile)
    {
        if (File.Exists(backupFile)) File.Delete(backupFile);
    }

    public static void Delete(string instancePath, string worldDir)
    {
        var target = Path.Combine(instancePath, "saves", worldDir);
        if (!Directory.Exists(target))
            throw new InvalidOperationException("存档不存在。");
        // 目录名做合法性约束：只允许存在于 saves 下的直接子目录，拒绝路径穿越
        if (Path.GetFileName(worldDir) != worldDir || worldDir.Contains(".."))
            throw new InvalidOperationException("存档名无效。");
        Directory.Delete(target, true);
        Logger.Info($"[world] 已删除存档 {worldDir}");
    }

    /// <summary>重命名存档：改目录名。绝不重写 level.dat 里的 LevelName（见类注释）。</summary>
    public static string Rename(string instancePath, string worldDir, string newName)
    {
        if (string.IsNullOrWhiteSpace(newName) || newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new InvalidOperationException("名称包含非法字符。");
        var saves = Path.Combine(instancePath, "saves");
        var target = Path.Combine(saves, worldDir);
        if (!Directory.Exists(target))
            throw new InvalidOperationException("存档不存在。");
        var dest = Path.Combine(saves, newName);
        if (Directory.Exists(dest))
            throw new InvalidOperationException("已存在同名目录。");
        Directory.Move(target, dest);
        return newName;
    }

    private static string InstanceIdOf(string instancePath)
        => InstanceService.List().FirstOrDefault(i =>
               string.Equals(i.Path, instancePath, StringComparison.OrdinalIgnoreCase))?.Id.ToString()
           ?? "unknown";

    // ------------------------------------------------------------------
    // NBT 只读解析（level.dat）
    // ------------------------------------------------------------------

    private static void ReadLevelDat(string worldDir, WorldInfo info)
    {
        var file = Path.Combine(worldDir, "level.dat");
        using var fs = File.OpenRead(file);
        using var gz = new GZipStream(fs, CompressionMode.Decompress);
        using var ms = new MemoryStream();
        gz.CopyTo(ms);
        ms.Position = 0;

        var r = new BigEndianReader(ms);
        var root = ReadTag(r);
        if (root is not CompoundTag compound) return;
        if (compound.Value.TryGetValue("Data", out var data) && data is CompoundTag dc)
        {
            if (dc.Value.TryGetValue("LevelName", out var n) && n is StringTag sn) info.Name = sn.Value;
            if (dc.Value.TryGetValue("LastPlayed", out var lp) && lp is LongTag sl) info.LastPlayed = sl.Value;
            if (dc.Value.TryGetValue("GameType", out var gt) && gt is IntTag sg)
                info.GameType = sg.Value switch { 0 => "生存", 1 => "创造", 2 => "冒险", 3 => "旁观", _ => $"类型{sg.Value}" };
            if (dc.Value.TryGetValue("Difficulty", out var df) && df is IntTag sd)
                info.Difficulty = sd.Value switch { 0 => "和平", 1 => "简单", 2 => "普通", 3 => "困难", _ => "" };
            if (dc.Value.TryGetValue("hardcore", out var hc) && hc is ByteTag sb) info.Hardcore = sb.Value != 0;
            if (dc.Value.TryGetValue("Version", out var ver) && ver is CompoundTag vc &&
                vc.Value.TryGetValue("Name", out var vn) && vn is StringTag svn)
                info.VersionName = svn.Value;
        }
    }

    // ---- NBT 标签模型（只读需要的那部分）----

    private abstract record NbtTag;
    private sealed record ByteTag(byte Value) : NbtTag;
    private sealed record ShortTag(short Value) : NbtTag;
    private sealed record IntTag(int Value) : NbtTag;
    private sealed record LongTag(long Value) : NbtTag;
    private sealed record FloatTag(float Value) : NbtTag;
    private sealed record DoubleTag(double Value) : NbtTag;
    private sealed record ByteArrayTag(byte[] Value) : NbtTag;
    private sealed record StringTag(string Value) : NbtTag;
    private sealed record ListTag(NbtTag[] Value) : NbtTag;
    private sealed record CompoundTag(Dictionary<string, NbtTag> Value) : NbtTag;
    private sealed record IntArrayTag(int[] Value) : NbtTag;
    private sealed record LongArrayTag(long[] Value) : NbtTag;

    private sealed class BigEndianReader
    {
        private readonly Stream _s;
        public BigEndianReader(Stream s) => _s = s;

        public byte ReadByte()
        {
            var b = _s.ReadByte();
            return b < 0 ? throw new EndOfStreamException() : (byte)b;
        }
        public ushort ReadUShort()
        {
            Span<byte> b = stackalloc byte[2];
            _s.ReadExactly(b);
            return (ushort)((b[0] << 8) | b[1]);
        }
        public short ReadShort() => (short)ReadUShort();
        public int ReadInt()
        {
            Span<byte> b = stackalloc byte[4];
            _s.ReadExactly(b);
            return (b[0] << 24) | (b[1] << 16) | (b[2] << 8) | b[3];
        }
        public long ReadLong()
        {
            Span<byte> b = stackalloc byte[8];
            _s.ReadExactly(b);
            long v = 0;
            for (var i = 0; i < 8; i++) v = (v << 8) | b[i];
            return v;
        }
        public float ReadFloat() => Int32BitsToFloat(ReadInt());
        public double ReadDouble() => Int64BitsToDouble(ReadLong());
        public string ReadString() => Encoding.UTF8.GetString(ReadBytes(ReadUShort()));
        public byte[] ReadBytes(int n)
        {
            var buf = new byte[n];
            _s.ReadExactly(buf);
            return buf;
        }

        private static float Int32BitsToFloat(int v) => BitConverter.Int32BitsToSingle(v);
        private static double Int64BitsToDouble(long v) => BitConverter.Int64BitsToDouble(v);
    }

    private static NbtTag ReadTag(BigEndianReader r) => ReadPayload(r, r.ReadByte());

    private static NbtTag ReadPayload(BigEndianReader r, byte type) => type switch
    {
        1 => new ByteTag(r.ReadByte()),
        2 => new ShortTag(r.ReadShort()),
        3 => new IntTag(r.ReadInt()),
        4 => new LongTag(r.ReadLong()),
        5 => new FloatTag(r.ReadFloat()),
        6 => new DoubleTag(r.ReadDouble()),
        7 => new ByteArrayTag(r.ReadBytes(r.ReadInt())),
        8 => new StringTag(r.ReadString()),
        9 => ReadList(r),
        10 => ReadCompound(r),
        11 => ReadIntArray(r),
        12 => ReadLongArray(r),
        _ => throw new InvalidDataException($"未知的 NBT 标签类型 {type}"),
    };

    private static ListTag ReadList(BigEndianReader r)
    {
        var elemType = r.ReadByte();
        var count = r.ReadInt();
        if (count < 0 || count > 1 << 22) throw new InvalidDataException("列表长度异常");
        var items = new NbtTag[count];
        for (var i = 0; i < count; i++) items[i] = ReadPayload(r, elemType);
        return new ListTag(items);
    }

    private static CompoundTag ReadCompound(BigEndianReader r)
    {
        var dict = new Dictionary<string, NbtTag>();
        while (true)
        {
            var t = r.ReadByte();
            if (t == 0) return new CompoundTag(dict);
            var name = r.ReadString();
            dict[name] = ReadPayload(r, t);
        }
    }

    private static IntArrayTag ReadIntArray(BigEndianReader r)
    {
        var n = r.ReadInt();
        var a = new int[n];
        for (var i = 0; i < n; i++) a[i] = r.ReadInt();
        return new IntArrayTag(a);
    }

    private static LongArrayTag ReadLongArray(BigEndianReader r)
    {
        var n = r.ReadInt();
        var a = new long[n];
        for (var i = 0; i < n; i++) a[i] = r.ReadLong();
        return new LongArrayTag(a);
    }
}
