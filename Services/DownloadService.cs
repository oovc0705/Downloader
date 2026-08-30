using System.Collections.ObjectModel;
using Microsoft.UI.Dispatching;
using MyApp.Models;
using System.Text.Json;

namespace MyApp.Services;

public sealed class DownloadService
{
    private static readonly string[] CookieBrowserOrder = { "edge", "chrome", "firefox" };

    /// <summary>顶部导航与入队白名单共用的支持平台（快测试测过的六个）。</summary>
    public static readonly string[] SupportedPlatforms =
        { "抖音", "小红书", "哔哩哔哩", "YouTube", "X", "Instagram" };

    private readonly DispatcherQueue _dispatcher;
    private readonly YtDlpRunner _runner = new();
    private readonly SemaphoreSlim _gate;
    private readonly Dictionary<Guid, CancellationTokenSource> _cancellations = new();
    private readonly Dictionary<string, ObservableCollection<DownloadItem>> _itemsByPlatform;
    private readonly object _lock = new();
    private string? _workingCookieSource;

    public DownloadService()
    {
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        // 并发信号量启动时创建一次；设置页修改 MaxConcurrency 需重启生效
        var maxConcurrency = Math.Clamp(AppSettings.Instance.MaxConcurrency, 1, 4);
        _gate = new SemaphoreSlim(maxConcurrency, maxConcurrency);
        _itemsByPlatform = SupportedPlatforms.ToDictionary(p => p, _ => new ObservableCollection<DownloadItem>());
        HistoryStore.EnsureLoaded();
    }

    /// <summary>取某个平台的任务集合（每个平台分区页各自绑定自己的列表）。</summary>
    public ObservableCollection<DownloadItem> GetItems(string platform) => _itemsByPlatform[platform];

    public string OutputDir
    {
        get => AppSettings.Instance.OutputDir;
        set
        {
            AppSettings.Instance.OutputDir = value;
            AppSettings.Instance.Save();
        }
    }

    public bool AskQuality
    {
        get => AppSettings.Instance.AskQuality;
        set
        {
            AppSettings.Instance.AskQuality = value;
            AppSettings.Instance.Save();
        }
    }

    public Func<DownloadItem, IReadOnlyList<QualityOption>, Task<QualityOption?>>? QualityPrompt { get; set; }

    /// <summary>图集内容解析完成后弹选择器；返回 null 表示用户取消，返回勾选子集则只下所选。</summary>
    public Func<DownloadItem, ImageNote, Task<IReadOnlyList<GalleryItem>?>>? GalleryPrompt { get; set; }

    public static string DefaultOutputDir()
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "MediaDownloader");

    /// <summary>
    /// 读取代理设置供 yt-dlp --proxy 使用；未启用或地址非法时返回 null。
    /// 每次任务发起时实时读取，设置页修改后对新任务立即生效。
    /// </summary>
    public static string? EffectiveProxy()
    {
        var settings = AppSettings.Instance;
        if (!settings.ProxyEnabled)
        {
            return null;
        }

        var url = settings.ProxyUrl.Trim();
        return url.Length > 0 && Uri.TryCreate(url, UriKind.Absolute, out _)
            ? url
            : null;
    }

    /// <summary>
    /// 入队一个下载任务。按 URL 判定所属平台并写入对应分区的列表（跨页粘贴会自动路由）；
    /// 平台不在支持清单时返回 Accepted=false 且不产生任务。
    /// </summary>
    public (bool Accepted, string DetectedPlatform, DownloadItem? Item) Enqueue(string url)
    {
        url = url.Trim();
        var platform = PlatformDetector.Detect(url);
        if (!SupportedPlatforms.Contains(platform))
        {
            return (false, platform, null);
        }

        var item = new DownloadItem
        {
            Url = url,
            Platform = platform,
        };
        // 新任务置顶，与历史记录一致的「最新在上」阅读顺序
        GetItems(platform).Insert(0, item);
        _ = Task.Run(() => RunAsync(item));
        return (true, platform, item);
    }

    public void Cancel(DownloadItem item)
    {
        CancellationTokenSource? cts;
        lock (_lock)
        {
            _cancellations.TryGetValue(item.Id, out cts);
        }

        cts?.Cancel();
    }

    /// <summary>
    /// 移除某平台分区里所有已结束（完成/失败/取消）的任务，返回移除数量。
    /// 仅供 UI 线程调用（直接操作被列表绑定的集合）。
    /// </summary>
    public int RemoveFinished(string platform)
    {
        var items = GetItems(platform);
        var removed = 0;
        for (var i = items.Count - 1; i >= 0; i--)
        {
            if (items[i].IsActive)
            {
                continue;
            }

            var id = items[i].Id;
            items.RemoveAt(i);
            lock (_lock)
            {
                _cancellations.Remove(id);
            }

            removed++;
        }

        return removed;
    }

    public void OpenContainingFolder(DownloadItem item) => FileLocations.Reveal(item.FilePath);

    private async Task RunAsync(DownloadItem item)
    {
        var cts = new CancellationTokenSource();
        lock (_lock)
        {
            _cancellations[item.Id] = cts;
        }

        try
        {
            OnUi(item, i =>
            {
                i.Status = DownloadStatus.Resolving;
                i.Error = string.Empty;
            });

            // 代理每次任务实时读取：设置页改完对新任务立即生效
            var proxy = EffectiveProxy();

            string json;
            try
            {
                json = await WithCookieFallbackAsync(
                    src => _runner.GetInfoJsonAsync(item.Url, cts.Token, src, proxy),
                    item.Url,
                    cts.Token).ConfigureAwait(false);
            }
            catch (YtDlpException ex) when (item.Platform == "小红书"
                && ex.Message.Contains("No video formats found", StringComparison.OrdinalIgnoreCase))
            {
                // 图文笔记不含视频流，yt-dlp 必然失败，改走图片直链下载
                await DownloadGalleryAsync(item,
                    u => XhsNoteService.FetchNoteAsync(u, cts.Token),
                    XhsNoteService.DownloadImagesAsync,
                    "未能从小红书页面解析出图文内容，请使用 App 内复制的完整分享链接重试（需包含 xsec_token 参数）",
                    cts.Token).ConfigureAwait(false);
                return;
            }
            catch (YtDlpException ex) when (item.Platform == "抖音"
                && ex.Message.Contains("Unsupported URL", StringComparison.OrdinalIgnoreCase))
            {
                // 抖音图集（图文帖/拼贴多段视频）走 /note/ 路由，yt-dlp 提取器不支持，
                // 改走分享页 SSR 解析媒体直链
                await DownloadGalleryAsync(item,
                    u => DouyinGalleryService.FetchNoteAsync(u, cts.Token),
                    DouyinGalleryService.DownloadImagesAsync,
                    "未能从抖音页面解析出图文内容，请使用 App 内「复制链接」得到的完整分享链接重试",
                    cts.Token).ConfigureAwait(false);
                return;
            }

            string title;
            var qualities = new List<QualityOption>();
            using (var doc = JsonDocument.Parse(json))
            {
                var root = doc.RootElement;
                if (root.TryGetProperty("_type", out var typeEl)
                    && typeEl.ValueKind == JsonValueKind.String
                    && typeEl.GetString() == "playlist")
                {
                    // 合辑/播放列表无独立格式信息，直接下载会产生相互覆盖的多文件，给出明确报错
                    throw new YtDlpException("该链接包含多个视频（合辑或播放列表），暂不支持批量下载，请逐个粘贴单条视频链接");
                }

                title = root.TryGetProperty("title", out var titleEl) && titleEl.ValueKind == JsonValueKind.String
                    ? titleEl.GetString() ?? "(未知名)"
                    : "(未知名)";
                qualities = ParseQualities(root);
            }

            OnUi(item, i =>
            {
                i.Title = title;
                i.Qualities.Clear();
                i.Qualities.AddRange(qualities);
            });

            var selector = "bv*+ba/b";
            if (AskQuality && qualities.Count > 0 && QualityPrompt is not null)
            {
                // 直接传入解析结果，避免读取尚未在 UI 线程填充完的 item.Qualities
                var chosen = await QualityPrompt(item, qualities).ConfigureAwait(false);
                if (chosen is null)
                {
                    OnUi(item, i =>
                    {
                        i.Speed = string.Empty;
                        i.Eta = string.Empty;
                        i.Status = DownloadStatus.Cancelled;
                    });
                    return;
                }

                selector = chosen.Selector;
                OnUi(item, i => i.QualityLabel = chosen.Label);
            }

            await _gate.WaitAsync(cts.Token).ConfigureAwait(false);
            try
            {
                OnUi(item, i => i.Status = DownloadStatus.Downloading);

                await WithCookieFallbackAsync(
                    src => _runner.DownloadAsync(
                        item, OutputDir, selector, src, p => ApplyProgress(item, p), cts.Token,
                        proxy, AppSettings.Instance.ExtractAudio),
                    item.Url,
                    cts.Token).ConfigureAwait(false);

                OnUi(item, i =>
                {
                    i.Status = DownloadStatus.Completed;
                    i.Progress = 100;
                    i.Speed = string.Empty;
                    i.Eta = string.Empty;
                    if (i.FilePath is null)
                    {
                        i.FilePath = Path.Combine(OutputDir, i.Title);
                    }
                });
                RecordTerminal(item);
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (OperationCanceledException)
        {
            OnUi(item, i =>
            {
                if (i.IsActive)
                {
                    i.Status = DownloadStatus.Cancelled;
                    i.Speed = string.Empty;
                    i.Eta = string.Empty;
                }
            });
        }
        catch (YtDlpException ex)
        {
            OnUi(item, i =>
            {
                i.Status = DownloadStatus.Failed;
                i.Error = ErrorMessages.Friendly(ex.Message);
                i.Speed = string.Empty;
                i.Eta = string.Empty;
            });
            RecordTerminal(item);
        }
        catch (Exception ex)
        {
            OnUi(item, i =>
            {
                i.Status = DownloadStatus.Failed;
                i.Error = ErrorMessages.Friendly(
                    string.IsNullOrWhiteSpace(ex.Message) ? ex.ToString() : ex.Message);
                i.Speed = string.Empty;
                i.Eta = string.Empty;
            });
            RecordTerminal(item);
        }
        finally
        {
            lock (_lock)
            {
                _cancellations.Remove(item.Id);
            }

            cts.Dispose();
        }
    }

    /// <summary>
    /// 图文/图集统一通道：解析直链 →（多于一条时）弹出勾选器 → 只下载所选条目。
    /// </summary>
    private async Task DownloadGalleryAsync(
        DownloadItem item,
        Func<string, Task<ImageNote?>> fetchNote,
        Func<IReadOnlyList<GalleryItem>, string, string, Action<YtDlpProgress>, CancellationToken, Task<string>> downloadMedia,
        string notFoundMessage,
        CancellationToken ct)
    {
        var note = await fetchNote(item.Url).ConfigureAwait(false);
        if (note is null || note.Items.Count == 0)
        {
            throw new YtDlpException(notFoundMessage);
        }

        var chosen = note.Items;
        if (chosen.Count > 1 && GalleryPrompt is not null)
        {
            OnUi(item, i => i.Title = note.Title);

            chosen = await GalleryPrompt(item, note).ConfigureAwait(false);
            if (chosen is null || chosen.Count == 0)
            {
                OnUi(item, i =>
                {
                    i.Status = DownloadStatus.Cancelled;
                    i.Speed = string.Empty;
                    i.Eta = string.Empty;
                });
                return;
            }
        }

        OnUi(item, i =>
        {
            i.Title = note.Title;
            i.Status = DownloadStatus.Downloading;
        });

        Directory.CreateDirectory(OutputDir);
        var firstFile = await downloadMedia(
            chosen,
            OutputDir,
            note.Title,
            p => ApplyProgress(item, p),
            ct).ConfigureAwait(false);

        OnUi(item, i =>
        {
            i.Status = DownloadStatus.Completed;
            i.Progress = 100;
            i.Speed = string.Empty;
            i.Eta = string.Empty;
            i.FilePath = firstFile;
        });
        RecordTerminal(item);
    }

    /// <summary>任务进入终态（完成/失败）后写入历史记录。取消的任务不入档。</summary>
    private void RecordTerminal(DownloadItem item)
        => OnUi(item, i =>
        {
            if (i.Status is not (DownloadStatus.Completed or DownloadStatus.Failed))
            {
                return;
            }

            HistoryStore.Add(new HistoryRecord
            {
                Id = Guid.NewGuid(),
                Url = i.Url,
                Platform = i.Platform,
                Title = i.Title,
                FilePath = i.Status == DownloadStatus.Completed ? i.FilePath : null,
                StatusText = i.Status == DownloadStatus.Completed ? "已完成" : "失败",
                FinishedAt = DateTime.Now,
            });
        });

    private Task WithCookieFallbackAsync(Func<string?, Task> action, string url, CancellationToken ct)
        => WithCookieFallbackAsync<object?>(async src =>
        {
            await action(src).ConfigureAwait(false);
            return null;
        }, url, ct);

    private async Task<T> WithCookieFallbackAsync<T>(Func<string?, Task<T>> action, string url, CancellationToken ct)
    {
        try
        {
            return await action(_workingCookieSource).ConfigureAwait(false);
        }
        catch (YtDlpException ex) when (ex.Message.Contains("ookie", StringComparison.OrdinalIgnoreCase))
        {
            var candidates = new List<string?>(CookieBrowserOrder);

            if (url.Contains("douyin", StringComparison.OrdinalIgnoreCase))
            {
                var cookieFile = await TtwidCookieProvider.GetCookiesFileAsync(ct).ConfigureAwait(false);
                if (cookieFile is not null)
                {
                    candidates.Insert(0, cookieFile);
                }
            }

            foreach (var candidate in candidates)
            {
                if (candidate == _workingCookieSource)
                {
                    continue;
                }

                try
                {
                    var result = await action(candidate).ConfigureAwait(false);
                    _workingCookieSource = candidate;
                    return result;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    // try next source
                }
            }

            ct.ThrowIfCancellationRequested();
            throw new YtDlpException("该平台需要 Cookie 验证。请确认网络可访问对应平台，或关闭正在运行的浏览器后重试（Edge/Chrome 的 Cookie 数据库被占用时无法读取）");
        }
    }

    private static List<QualityOption> ParseQualities(JsonElement root)
    {
        var byHeight = new Dictionary<int, (double Tbr, string Id, long? Size, double Fps)>();
        if (root.TryGetProperty("formats", out var formats) && formats.ValueKind == JsonValueKind.Array)
        {
            foreach (var f in formats.EnumerateArray())
            {
                var vcodec = f.TryGetProperty("vcodec", out var vc) && vc.ValueKind == JsonValueKind.String
                    ? vc.GetString()
                    : null;
                if (string.IsNullOrEmpty(vcodec) || vcodec == "none")
                {
                    continue;
                }

                if (!f.TryGetProperty("height", out var h) || h.ValueKind != JsonValueKind.Number)
                {
                    continue;
                }

                var height = h.GetInt32();
                if (height < 144)
                {
                    continue;
                }

                var id = f.TryGetProperty("format_id", out var idEl) && idEl.ValueKind == JsonValueKind.String
                    ? idEl.GetString()
                    : null;
                if (string.IsNullOrEmpty(id))
                {
                    continue;
                }

                var tbr = f.TryGetProperty("tbr", out var t) && t.ValueKind == JsonValueKind.Number ? t.GetDouble() : 0;
                var fps = f.TryGetProperty("fps", out var fp) && fp.ValueKind == JsonValueKind.Number ? fp.GetDouble() : 0;
                long? size = null;
                if (f.TryGetProperty("filesize", out var fs) && fs.ValueKind == JsonValueKind.Number)
                {
                    size = fs.GetInt64();
                }
                else if (f.TryGetProperty("filesize_approx", out var fa) && fa.ValueKind == JsonValueKind.Number)
                {
                    size = fa.GetInt64();
                }

                if (!byHeight.TryGetValue(height, out var cur) || tbr > cur.Tbr)
                {
                    byHeight[height] = (tbr, id!, size, fps);
                }
            }
        }

        var result = byHeight
            .OrderByDescending(kv => kv.Key)
            .Select(kv =>
            {
                var (tbr, id, size, fps) = kv.Value;
                var label = $"{kv.Key}P";
                if (fps >= 50)
                {
                    label += $"{fps:0}";
                }

                if (size is long s && s > 0)
                {
                    label += $"（约 {s / 1024.0 / 1024:0.#} MB）";
                }

                return new QualityOption(label, $"{id}+ba/{id}/bv*+ba/b");
            })
            .ToList();

        result.Insert(0, new QualityOption("最佳画质", "bv*+ba/b"));
        return result;
    }

    private void ApplyProgress(DownloadItem item, YtDlpProgress p)
    {
        void Apply(DownloadItem i)
        {
            if (p.IsProcessing)
            {
                i.Status = DownloadStatus.Processing;
                return;
            }

            if (i.Status == DownloadStatus.Processing)
            {
                i.Status = DownloadStatus.Downloading;
            }

            if (p.Percent is { } percent)
            {
                i.Progress = percent;
            }

            if (p.Speed is not null)
            {
                i.Speed = p.Speed == "Unknown B/s" || p.Speed == "N/A" ? string.Empty : p.Speed;
            }

            if (p.Eta is not null)
            {
                i.Eta = p.Eta == "Unknown" ? string.Empty : p.Eta;
            }

            if (p.FilePath is not null)
            {
                i.FilePath = p.FilePath;
            }
        }

        OnUi(item, Apply);
    }

    private void OnUi(DownloadItem item, Action<DownloadItem> apply)
    {
        if (_dispatcher.HasThreadAccess)
        {
            apply(item);
        }
        else
        {
            _dispatcher.TryEnqueue(() => apply(item));
        }
    }
}
