using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using MyApp.Models;

namespace MyApp.Services;

/// <summary>
/// 本地数据目录：%LOCALAPPDATA%\MyApp。应用为未打包形态，不用 MSIX 容器，
/// 设置与历史记录都落在该目录下的独立 json 文件。
/// </summary>
public static class AppFolders
{
    public static string DataDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MyApp");
}

public sealed class HistoryStore
{
    private static readonly object Sync = new();
    private static readonly SemaphoreSlim IoGate = new(1, 1);

    private static List<HistoryRecord> _records = new();
    private static bool _loaded;

    /// <summary>UI 线程增量更新事件；历史按时间倒序维护，新记录出现在最前。</summary>
    public static event Action<HistoryRecord>? Added;
    public static event Action<HistoryRecord>? Removed;
    public static event Action? Cleared;

    public static void EnsureLoaded()
    {
        lock (Sync)
        {
            if (_loaded)
            {
                return;
            }

            try
            {
                var path = Path.Combine(AppFolders.DataDir, "history.json");
                if (File.Exists(path))
                {
                    using var stream = File.OpenRead(path);
                    var stored = JsonSerializer.Deserialize(stream, HistoryJsonContext.Default.ListHistoryRecord);
                    // 按 Id 去重，避免手改文件或中断写入造成重复条目
                    _records = stored?
                        .DistinctBy(r => r.Id)
                        .OrderByDescending(r => r.FinishedAt)
                        .ToList() ?? new List<HistoryRecord>();
                }
            }
            catch
            {
                // 文件损坏时以空历史启动，后续写入会覆盖重建
                _records = new List<HistoryRecord>();
            }

            _loaded = true;
        }
    }

    public static void Add(HistoryRecord record)
    {
        HistoryRecord? replaced = null;
        lock (Sync)
        {
            if (_records.Any(r => r.Id == record.Id))
            {
                return;
            }

            _records.Insert(0, record);
            replaced = record;
        }

        PersistBackground();
        Added?.Invoke(record);
    }

    public static void Remove(HistoryRecord record)
    {
        lock (Sync)
        {
            _records.RemoveAll(r => r.Id == record.Id);
        }

        PersistBackground();
        Removed?.Invoke(record);
    }

    /// <summary>清空指定平台的记录；platform 为 null 时清空全部。</summary>
    public static void Clear(string? platform)
    {
        lock (Sync)
        {
            _records = platform is null
                ? new List<HistoryRecord>()
                : _records.Where(r => r.Platform != platform).ToList();
        }

        PersistBackground();
        Cleared?.Invoke();
    }

    /// <summary>全量快照副本（任意线程可用），供页面初始填充 / 清空事件后重建视图。</summary>
    public static IReadOnlyList<HistoryRecord> Snapshot()
    {
        lock (Sync)
        {
            return _records.ToList();
        }
    }

    private static void PersistBackground()
    {
        List<HistoryRecord> snapshot;
        lock (Sync)
        {
            snapshot = _records.ToList();
        }

        Task.Run(async () =>
        {
            await IoGate.WaitAsync().ConfigureAwait(false);
            try
            {
                Directory.CreateDirectory(AppFolders.DataDir);
                var path = Path.Combine(AppFolders.DataDir, "history.json");
                var tmp = path + ".tmp";
                await using (var stream = File.Create(tmp))
                {
                    await JsonSerializer.SerializeAsync(stream, snapshot, HistoryJsonContext.Default.ListHistoryRecord)
                        .ConfigureAwait(false);
                }

                File.Move(tmp, path, overwrite: true);
            }
            catch
            {
                // 磁盘故障等场景下静默放弃本次持久化，内存态不受影响
            }
            finally
            {
                IoGate.Release();
            }
        });
    }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(List<HistoryRecord>))]
internal sealed partial class HistoryJsonContext : JsonSerializerContext;
