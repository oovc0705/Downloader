using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MyApp.Services;

/// <summary>图集里的单个媒体条目：图片或分段视频（拼贴类作品）。</summary>
public sealed record GalleryItem(string Url, string? ThumbUrl = null, bool IsVideo = false);

/// <summary>图文/图集类内容的统一解析结果（小红书图文、抖音图集共用）。</summary>
public sealed record ImageNote(string Title, IReadOnlyList<GalleryItem> Items);

/// <summary>
/// 从 HTML 中提取内嵌 JSON（如 window.__INITIAL_STATE__ / window._ROUTER_DATA）。
/// 用大括号配平扫描而非贪婪正则，避免脚本块里出现多个 </script> 时截错范围。
/// </summary>
public static partial class JsonFragmentExtractor
{
    [GeneratedRegex(@"window\.(__INITIAL_STATE__|_ROUTER_DATA)\s*=\s*", RegexOptions.Compiled)]
    private static partial Regex AssignmentMarker();

    public static string? Extract(string html)
    {
        var marker = AssignmentMarker().Match(html);
        if (!marker.Success)
        {
            return null;
        }

        var start = html.IndexOf('{', marker.Index + marker.Length);
        if (start < 0)
        {
            return null;
        }

        var depth = 0;
        var inString = false;
        var escaped = false;
        for (var i = start; i < html.Length; i++)
        {
            var c = html[i];
            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (c == '\\')
                {
                    escaped = true;
                }
                else if (c == '"')
                {
                    inString = false;
                }
            }
            else if (c == '"')
            {
                inString = true;
            }
            else if (c == '{')
            {
                depth++;
            }
            else if (c == '}')
            {
                depth--;
                if (depth == 0)
                {
                    var json = html[start..(i + 1)];
                    // JS 对象字面量含 bare undefined，转成合法 JSON
                    return Regex.Replace(json, @"([\[:,\s])undefined(?![\w])", "$1null");
                }
            }
        }

        return null;
    }

    public static bool TryParse(string html, out JsonDocument document)
    {
        var json = Extract(html);
        if (json is null)
        {
            document = JsonDocument.Parse("null");
            return false;
        }

        try
        {
            document = JsonDocument.Parse(json);
            return true;
        }
        catch (JsonException)
        {
            document = JsonDocument.Parse("null");
            return false;
        }
    }
}

/// <summary>
/// 图集媒体批量下载：逐张落盘、按「标题 (n).扩展名」命名、上报进度，返回首个成功文件路径。
/// 条目可以是图片或分段视频（拼贴类作品），扩展名优先按响应 Content-Type，缺失时回退 URL 后缀。
/// </summary>
public static class GalleryImageDownloader
{
    public static async Task<string> DownloadAsync(
        HttpClient http,
        IReadOnlyList<GalleryItem> items,
        string outputDir,
        string title,
        Action<YtDlpProgress> onProgress,
        CancellationToken ct)
    {
        var baseName = SanitizeFileName(title);
        var firstFile = string.Empty;

        for (var i = 0; i < items.Count; i++)
        {
            ct.ThrowIfCancellationRequested();

            using var response = await http.GetAsync(items[i].Url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var ext = ResolveExtension(items[i], response.Content.Headers.ContentType?.MediaType);
            var fileName = items.Count == 1 ? $"{baseName}{ext}" : $"{baseName} ({i + 1}){ext}";
            var path = Path.Combine(outputDir, fileName);

            await using (var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var file = File.Create(path))
            {
                await stream.CopyToAsync(file, ct).ConfigureAwait(false);
            }

            if (firstFile.Length == 0)
            {
                firstFile = path;
            }

            onProgress(new YtDlpProgress { Percent = (i + 1) * 100.0 / items.Count });
        }

        return firstFile;
    }

    public static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name.Length);
        foreach (var ch in name)
        {
            sb.Append(Array.IndexOf(invalid, ch) >= 0 ? '_' : ch);
        }

        var result = sb.ToString().Trim().TrimEnd('.');
        if (result.Length > 80)
        {
            result = result[..80].TrimEnd();
        }

        return result.Length == 0 ? "图文内容" : result;
    }

    private static string ResolveExtension(GalleryItem item, string? mediaType)
    {
        var mapped = mediaType switch
        {
            "image/png" => ".png",
            "image/jpeg" => ".jpg",
            "image/webp" => ".webp",
            "image/gif" => ".gif",
            "image/bmp" => ".bmp",
            "video/mp4" => ".mp4",
            "video/quicktime" => ".mov",
            "video/webm" => ".webm",
            _ when mediaType?.StartsWith("video/") == true => ".mp4",
            _ => null,
        };
        if (mapped is not null)
        {
            return mapped;
        }

        // Content-Type 缺失或未映射：退回 URL 路径后缀（截去 query），仍无果按条目类型给默认值
        if (Uri.TryCreate(item.Url, UriKind.Absolute, out var uri))
        {
            var lastSegment = uri.AbsolutePath.Split('/')[^1];
            var dot = lastSegment.LastIndexOf('.');
            if (dot >= 0)
            {
                var ext = lastSegment[(dot + 1)..].ToLowerInvariant();
                if (ext is "png" or "jpg" or "jpeg" or "webp" or "gif" or "bmp"
                    or "mp4" or "mov" or "webm" or "m4v")
                {
                    return $".{(ext == "jpeg" ? "jpg" : ext)}";
                }
            }
        }

        return item.IsVideo ? ".mp4" : ".jpg";
    }
}
