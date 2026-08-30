using System.Net.Http;
using System.Text.Json;

namespace MyApp.Services;

/// <summary>
/// 小红书图文笔记解析与下载。yt-dlp 只支持小红书视频，图文帖会报
/// "No video formats found"，这里直接抓取页面中的 __INITIAL_STATE__
/// 提取图片原始地址后逐张下载。
/// </summary>
public static class XhsNoteService
{
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 5,
        };
        var client = new HttpClient(handler);
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36");
        client.DefaultRequestHeaders.Referrer = new Uri("https://www.xiaohongshu.com/");
        return client;
    }

    public static async Task<ImageNote?> FetchNoteAsync(string url, CancellationToken ct)
    {
        using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return ParseNote(html);
    }

    public static ImageNote? ParseNote(string html)
    {
        try
        {
            if (!JsonFragmentExtractor.TryParse(html, out var doc))
            {
                return null;
            }

            using (doc)
            {
                var root = doc.RootElement;

                if (!root.TryGetProperty("note", out var noteRoot)
                    || !noteRoot.TryGetProperty("noteDetailMap", out var detailMap)
                    || detailMap.ValueKind != JsonValueKind.Object)
                {
                    return null;
                }

                foreach (var detail in detailMap.EnumerateObject())
                {
                    if (detail.Value.ValueKind != JsonValueKind.Object
                        || !detail.Value.TryGetProperty("note", out var note)
                        || note.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    var images = new List<GalleryItem>();
                    if (note.TryGetProperty("imageList", out var imageList) && imageList.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var image in imageList.EnumerateArray())
                        {
                            var address = GetImageAddress(image);
                            if (!string.IsNullOrEmpty(address))
                            {
                                images.Add(new GalleryItem(address!));
                            }
                        }
                    }

                    if (images.Count == 0)
                    {
                        continue;
                    }

                    var title = "(小红书图文)";
                    if (note.TryGetProperty("title", out var titleEl)
                        && titleEl.ValueKind == JsonValueKind.String
                        && !string.IsNullOrWhiteSpace(titleEl.GetString()))
                    {
                        title = titleEl.GetString()!;
                    }
                    else if (note.TryGetProperty("desc", out var descEl)
                        && descEl.ValueKind == JsonValueKind.String
                        && !string.IsNullOrWhiteSpace(descEl.GetString()))
                    {
                        title = descEl.GetString()!.Trim();
                    }

                    return new ImageNote(title, images);
                }

                return null;
            }
        }
        catch (Exception)
        {
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

    private static string? GetImageAddress(JsonElement image)
    {
        if (image.TryGetProperty("urlDefault", out var def) && def.ValueKind == JsonValueKind.String)
        {
            var value = def.GetString();
            if (!string.IsNullOrEmpty(value))
            {
                return value;
            }
        }

        if (image.TryGetProperty("urlPre", out var pre) && pre.ValueKind == JsonValueKind.String)
        {
            var value = pre.GetString();
            if (!string.IsNullOrEmpty(value))
            {
                return value;
            }
        }

        if (image.TryGetProperty("infoList", out var infos) && infos.ValueKind == JsonValueKind.Array)
        {
            string? last = null;
            foreach (var info in infos.EnumerateArray())
            {
                if (info.TryGetProperty("url", out var urlEl) && urlEl.ValueKind == JsonValueKind.String)
                {
                    last = urlEl.GetString();
                }
            }

            return last;
        }

        return null;
    }
}
