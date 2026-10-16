using System.Collections.Concurrent;
using StarCore.Core.App;
using StarCore.Core.Models;

namespace StarCore.Core.Services;

/// <summary>
/// 后台任务注册表。
///
/// 安装、下载、校验、导入都注册成一个任务；界面通过 <see cref="Updated"/> 事件
/// 获得进度，不需要轮询。任务状态是"只写不改"的：每次变更都把整条 TaskInfo 推出去，
/// 前端做整体替换，避免两端各自维护增量逻辑。
/// </summary>
public static class TaskCenter
{
    private sealed class Entry
    {
        public TaskInfo Info { get; init; } = new();
        public CancellationTokenSource Cts { get; init; } = new();
    }

    private static readonly ConcurrentDictionary<string, Entry> Tasks = new();
    private static int _seq;

    /// <summary>
    /// 任务归属：taskId → 它服务的实例 id。
    /// 存在的理由很具体 —— 删实例时得先把还挂在它上面的安装/下载任务停掉，
    /// 否则那些线程手里的文件句柄还开着，目录删到一半就失败，留下半截目录。
    /// 这张表刻意放在 TaskCenter 而不是 InstallService：
    /// InstanceService 要能按实例取消任务，但让它反向依赖 InstallService 会形成环。
    /// </summary>
    private static readonly ConcurrentDictionary<string, long> Owners = new();

    /// <summary>任务状态变化时触发（整条对象）。</summary>
    public static event Action<TaskInfo>? Updated;

    public static string Create(string title, bool cancellable = true)
    {
        var id = $"t{Interlocked.Increment(ref _seq)}_{DateTimeOffset.Now.ToUnixTimeMilliseconds()}";
        var entry = new Entry { Info = new TaskInfo { Id = id, Title = title, State = "queued", Cancellable = cancellable } };
        Tasks[id] = entry;
        Publish(entry.Info);
        return id;
    }

    public static TaskInfo? Get(string id)
        => Tasks.TryGetValue(id, out var e) ? e.Info : null;

    public static IReadOnlyList<TaskInfo> All => Tasks.Values.Select(e => e.Info).ToList();

    public static CancellationToken Token(string id)
        => Tasks.TryGetValue(id, out var e) ? e.Cts.Token : CancellationToken.None;

    /// <summary>就地修改任务并推送。</summary>
    public static void Report(string id, Action<TaskInfo> mutate)
    {
        if (!Tasks.TryGetValue(id, out var e)) return;
        lock (e.Info) mutate(e.Info);
        Publish(e.Info);
    }

    public static void Start(string id, string? detail = null)
        => Report(id, t => { t.State = "running"; t.StartedAt = DateTimeOffset.Now; t.Detail = detail; });

    public static void Progress(string id, int done, int total, long doneBytes = 0, long totalBytes = 0, string? detail = null, double? speed = null)
        => Report(id, t =>
        {
            t.DoneFiles = done;
            t.TotalFiles = total;
            t.DoneBytes = doneBytes;
            t.TotalBytes = totalBytes;
            if (detail is not null) t.Detail = detail;
            if (speed is not null) t.Speed = speed;
            // 进度按"文件数"为主权重，字节数为辅：文件数更平滑，不会因单个大文件卡住
            var byFiles = total > 0 ? (double)done / total : 0;
            var byBytes = totalBytes > 0 ? (double)doneBytes / totalBytes : 0;
            t.Progress = Math.Clamp(Math.Max(byFiles, byBytes) * 100, 0, 100);
        });

    public static void Complete(string id, string? detail = null)
        => Report(id, t => { t.State = "done"; t.Progress = 100; t.Detail = detail; t.Speed = null; });

    public static void Fail(string id, string error)
        => Report(id, t => { t.State = "failed"; t.Error = error; t.Speed = null; });

    public static void Cancel(string id)
    {
        if (!Tasks.TryGetValue(id, out var e)) return;
        try { e.Cts.Cancel(); } catch { /* 已释放 */ }
        Report(id, t => { t.State = "cancelled"; t.Speed = null; });
    }

    public static void Remove(string id)
    {
        Owners.TryRemove(id, out _);
        if (Tasks.TryRemove(id, out var e)) { try { e.Cts.Dispose(); } catch { } }
    }

    /// <summary>把任务登记为"服务于某个实例"。</summary>
    public static void SetOwner(string id, long instanceId)
    {
        if (instanceId > 0) Owners[id] = instanceId;
    }

    /// <summary>
    /// 取消某个实例上所有还在跑的任务。删实例之前必须先调它 ——
    /// 否则下载线程手里的文件句柄会挡住目录删除。
    /// </summary>
    /// <returns>被取消的任务数。</returns>
    public static int CancelByOwner(long instanceId)
    {
        var n = 0;
        foreach (var kv in Owners.ToArray())
        {
            if (kv.Value != instanceId) continue;
            var info = Get(kv.Key);
            if (info is null || info.State is "done" or "failed" or "cancelled") continue;
            Cancel(kv.Key);
            n++;
        }
        if (n > 0) Logger.Info($"[task] 已取消实例 #{instanceId} 上 {n} 个进行中的任务");
        return n;
    }

    public static void ClearFinished()
    {
        foreach (var kv in Tasks.ToArray())
        {
            if (kv.Value.Info.State is "done" or "failed" or "cancelled") Remove(kv.Key);
        }
    }

    private static void Publish(TaskInfo info)
    {
        try { Updated?.Invoke(info); }
        catch (Exception ex) { Logger.Warn($"[task] 推送任务状态失败：{ex.Message}"); }
    }
}
