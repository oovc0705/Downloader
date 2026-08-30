using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MyApp.Services;

/// <summary>
/// 抖音图集（图文帖）解析与下载。yt-dlp 的抖音提取器只认 /video/ 路由，
/// 图文帖（/note/ 或短链跳转后的 note）会报 "Unsupported URL"。
/// 这里用 iPhone UA + ttwid Cookie 请求 iesdouyin 分享页（SSR 通道），
/// 从 window._ROUTER_DATA 取 item_list 中 images 数组的图片直链。
/// </summary>
public static partial class DouyinGalleryService
{
    private const string MobileUa =
        "Mozilla/5.0 (iPhone; CPU iPhone OS 17_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.0 Mobile/15E148 Safari/604.1";

    [GeneratedRegex(@"(?:video|note|slides)/(\d+)", RegexOptions.Compiled)]
    private static partial Regex ItemIdPattern();

    /// <summary>用户主页/发现页弹窗播放形态（浏览器地址栏复制的链接常见此形态）。</summary>
    [GeneratedRegex(@"[?&](?:modal_id|item_id)=(\d+)", RegexOptions.Compiled)]
    private static partial Regex ModalIdPattern();

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
        };
        var client = new HttpClient(handler);
        client.DefaultRequestHeaders.UserAgent.ParseAdd(MobileUa);
        return client;
    }

    public static async Task<ImageNote?> FetchNoteAsync(string url, CancellationToken ct)
    {
        var itemId = ExtractItemId(url);
        if (itemId.Length == 0)
        {
            itemId = await ResolveShortLinkAsync(url, ct).ConfigureAwait(false);
        }

        if (itemId.Length == 0)
        {
            throw new YtDlpException("无法从该链接解析出抖音内容 ID。支持 App 内「复制链接」得到的 v.douyin.com 短链、" +
                "网页版 /video/、/note/ 链接，或带 modal_id 参数的用户主页链接");
        }

        var html = await FetchSharePageAsync(itemId, ct).ConfigureAwait(false);
        DumpSharePageForDiagnostics(html);
        return ParseNote(html, itemId);
    }

    /// <summary>
    /// 把分享页内嵌 JSON 落盘到 数据目录\debug\douyin_last.json（始终覆盖，只留最后一次），
    /// 用于核对拼贴/实况类作品的真实字段结构；解析失败时退存原始 HTML。
    /// </summary>
    private static void DumpSharePageForDiagnostics(string html)
    {
        try
        {
            var dir = Path.Combine(AppFolders.DataDir, "debug");
            Directory.CreateDirectory(dir);
            var content = JsonFragmentExtractor.Extract(html) ?? html;
            File.WriteAllText(Path.Combine(dir, "douyin_last.json"), content);
        }
        catch
        {
            // 诊断转储失败不影响主流程
        }
    }

    /// <summary>依次尝试已知的 ID 形态：路径路由（/video/、/note/、/slides/）与查询参数（modal_id 等）。</summary>
    private static string ExtractItemId(string url)
    {
        var match = ItemIdPattern().Match(url);
        if (match.Success)
        {
            return match.Groups[1].Value;
        }

        match = ModalIdPattern().Match(url);
        return match.Success ? match.Groups[1].Value : string.Empty;
    }

    public static ImageNote? ParseNote(string html, string itemId)
    {
        if (!JsonFragmentExtractor.TryParse(html, out var doc))
        {
            return null;
        }

        using (doc)
        {
            foreach (var page in doc.RootElement.GetProperty("loaderData").EnumerateObject())
            {
                if (page.Value.ValueKind != JsonValueKind.Object
                    || !page.Value.TryGetProperty("videoInfoRes", out var info)
                    || info.ValueKind != JsonValueKind.Object
                    || !info.TryGetProperty("item_list", out var items)
                    || items.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var item in items.EnumerateArray())
                {
                    if (!item.TryGetProperty("images", out var images) || images.ValueKind != JsonValueKind.Array)
                    {
                        continue;
                    }

                    var entries = new List<GalleryItem>();
                    foreach (var image in images.EnumerateArray())
                    {
                        var poster = ExtractFirstUrl(image);
                        if (!string.IsNullOrEmpty(poster))
                        {
                            entries.Add(new GalleryItem(poster!));
                        }

                        // 拼贴类作品：每个卡片除封面图外还挂一段独立视频。
                        // 注意：嵌套结构未经真实链接验证过，取不到时保持纯图集行为（降级安全）。
                        if (image.TryGetProperty("videos", out var clips) && clips.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var clip in clips.EnumerateArray())
                            {
                                var videoUrl = clip.TryGetProperty("play_addr", out var playAddr)
                                    ? ExtractFirstUrl(playAddr)
                                    : null;
                                videoUrl ??= ExtractFirstUrl(clip);
                                if (!string.IsNullOrEmpty(videoUrl))
                                {
                                    entries.Add(new GalleryItem(videoUrl!, poster, IsVideo: true));
                                }
                            }
                        }
                    }

                    if (entries.Count == 0)
                    {
                        continue;
                    }

                    var title = "(抖音图文)";
                    if (item.TryGetProperty("desc", out var descEl)
                        && descEl.ValueKind == JsonValueKind.String
                        && !string.IsNullOrWhiteSpace(descEl.GetString()))
                    {
                        title = descEl.GetString()!.Trim();
                    }

                    return new ImageNote(title, entries);
                }

                // item_list 为空时给出可读的错误（如内容已删除、需要登录）
                if (info.TryGetProperty("filter_list", out var filters) && filters.ValueKind == JsonValueKind.Array)
                {
                    foreach (var filter in filters.EnumerateArray())
                    {
                        var reason = filter.TryGetProperty("filter_reason", out var r) ? r.GetString() : null;
                        if (string.Equals(reason, "SYSTEM_ITEM_NOT_EXIST", StringComparison.Ordinal))
                        {
                            throw new YtDlpException("该抖音内容不存在或已被删除");
                        }

                        if (!string.IsNullOrEmpty(reason))
                        {
                            throw new YtDlpException($"该抖音内容无法获取（{reason}），可能需要登录或已被限制访问");
                        }
                    }
                }
            }

            return null;
        }
    }

    public static Task<string> DownloadImagesAsync(
        IReadOnlyList<GalleryItem> items,
        string outputDir,
        string title,
        Action<YtDlpProgress> onProgress,
        CancellationToken ct)
        => GalleryImageDownloader.DownloadAsync(Http, items, outputDir, title, onProgress, ct);

    /// <summary>从节点的 url_list 多 CDN 副本里取第一个可用直链。</summary>
    private static string? ExtractFirstUrl(JsonElement node)
    {
        if (node.ValueKind != JsonValueKind.Object
            || !node.TryGetProperty("url_list", out var urlList)
            || urlList.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var candidate in urlList.EnumerateArray())
        {
            if (candidate.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(candidate.GetString()))
            {
                return candidate.GetString();
            }
        }

        return null;
    }

    /// <summary>把分享短链（v.douyin.com/xxx/）解析为内容 ID。</summary>
    private static async Task<string> ResolveShortLinkAsync(string url, CancellationToken ct)
    {
        try
        {
            var ttwid = await GetTtwidValueAsync(ct).ConfigureAwait(false);

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            // 短链跳转同样受风控影响：不带 ttwid 时可能返回 200 验证页而非 302
            if (!string.IsNullOrEmpty(ttwid))
            {
                request.Headers.TryAddWithoutValidation("Cookie", $"ttwid={ttwid}");
            }

            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            var current = response;
            for (var hop = 0; hop < 5 && (int)current.StatusCode is 301 or 302 or 303 or 307 or 308; hop++)
            {
                var location = current.Headers.Location;
                if (location is null)
                {
                    break;
                }

                var itemId = ExtractItemId(location.ToString());
                if (itemId.Length > 0)
                {
                    return itemId;
                }

                current.Dispose();
                using var next = new HttpRequestMessage(HttpMethod.Get, location);
                next.Headers.TryAddWithoutValidation("Referer", "https://www.douyin.com/");
                current = await Http.SendAsync(next, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 短链解析失败按无 ID 处理，由上层报错
        }

        return string.Empty;
    }

    private static async Task<string> FetchSharePageAsync(string itemId, CancellationToken ct)
    {
        var shareUrl = $"https://www.iesdouyin.com/share/video/{itemId}/";

        using var request = new HttpRequestMessage(HttpMethod.Get, shareUrl);
        var ttwid = await GetTtwidValueAsync(ct).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(ttwid))
        {
            request.Headers.TryAddWithoutValidation("Cookie", $"ttwid={ttwid}");
        }

        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
    }

    /// <summary>从 TtwidCookieProvider 生成的 Netscape Cookie 文件里取 ttwid 值（内部会按需刷新）。</summary>
    private static async Task<string?> GetTtwidValueAsync(CancellationToken ct)
    {
        var cookieFile = await TtwidCookieProvider.GetCookiesFileAsync(ct).ConfigureAwait(false);
        if (cookieFile is null || !File.Exists(cookieFile))
        {
            return null;
        }

        foreach (var line in File.ReadLines(cookieFile))
        {
            var parts = line.Split('\t');
            if (parts.Length >= 7 && parts[5] == "ttwid")
            {
                return parts[6];
            }
        }

        return null;
    }
}
