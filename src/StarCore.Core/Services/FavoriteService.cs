using System.Text.Json;
using StarCore.Core.App;
using StarCore.Core.Models;

namespace StarCore.Core.Services;

/// <summary>一条收藏记录。</summary>
public sealed class FavoriteItem
{
    public string Id { get; set; } = "";           // Modrinth 的 project id
    public string Slug { get; set; } = "";         // 用于拼接网页地址
    public string Title { get; set; } = "";
    public string? IconUrl { get; set; }
    public string? Description { get; set; }
    public string Kind { get; set; } = "mod";      // mod | resourcepack | shader | modpack | datapack
    public long Downloads { get; set; }
    public DateTimeOffset AddedAt { get; set; } = DateTimeOffset.Now;
}

/// <summary>
/// 收藏夹。
///
/// 为什么是本地收藏而不是"关注 Modrinth 账户"：
///   关注是 Modrinth 的账号级概念，必须登录他们的账号才能用，而 StarCore 的用户
///   大多只用离线账户 —— 那个按钮对他们等于永久是灰的。
///   收藏则是启动器自己的数据，不依赖任何账号，谁都能用，也更符合"我待会儿想装它"的真实意图。
/// </summary>
public static class FavoriteService
{
    private static readonly object Gate = new();
    private static List<FavoriteItem>? _items;

    private static string StoreFile => Path.Combine(StarCorePaths.DataRoot, "favorites.json");

    private static List<FavoriteItem> Items
    {
        get
        {
            if (_items is not null) return _items;
            lock (Gate)
            {
                if (_items is not null) return _items;
                try
                {
                    if (System.IO.File.Exists(StoreFile))
                        _items = Json.Deserialize<List<FavoriteItem>>(System.IO.File.ReadAllText(StoreFile)) ?? new();
                    else
                        _items = new();
                }
                catch (Exception ex)
                {
                    // 收藏文件损坏不该让启动器起不来，重建即可
                    Logger.Warn($"[favorite] 收藏文件读取失败，将重建：{ex.Message}");
                    _items = new();
                }
                return _items;
            }
        }
    }

    private static void Persist()
    {
        try
        {
            Directory.CreateDirectory(StarCorePaths.DataRoot);
            System.IO.File.WriteAllText(StoreFile, JsonSerializer.Serialize(Items, Json.Options));
        }
        catch (Exception ex)
        {
            Logger.Warn($"[favorite] 收藏写入失败：{ex.Message}");
        }
    }

    public static object List() => new
    {
        items = Items.OrderByDescending(i => i.AddedAt).ToList(),
        count = Items.Count,
    };

    /// <summary>收藏 / 取消收藏，返回操作后的状态。</summary>
    public static object Toggle(FavoriteItem item)
    {
        if (string.IsNullOrWhiteSpace(item.Id))
            return OpResult.Fail("缺少资源 ID。");

        lock (Gate)
        {
            var existing = Items.FirstOrDefault(i => i.Id == item.Id);
            if (existing is not null)
            {
                Items.Remove(existing);
                Persist();
                StarCoreHost.Emit("favorites.changed", null);
                return new { favorited = false, count = Items.Count };
            }

            Items.Add(item);
            Persist();
            StarCoreHost.Emit("favorites.changed", null);
            return new { favorited = true, count = Items.Count };
        }
    }

    public static object Remove(string id)
    {
        lock (Gate)
        {
            Items.RemoveAll(i => i.Id == id);
            Persist();
        }
        StarCoreHost.Emit("favorites.changed", null);
        return new { favorited = false, count = Items.Count };
    }

    /// <summary>某个资源是否已收藏。列表页要给每张卡片标心形。</summary>
    public static bool IsFavorited(string id)
    {
        lock (Gate) return Items.Any(i => i.Id == id);
    }

    /// <summary>批量查询，避免列表渲染时逐个加锁。</summary>
    public static HashSet<string> IdSet()
    {
        lock (Gate) return Items.Select(i => i.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }
}
