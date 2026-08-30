namespace MyApp.Services;

/// <summary>
/// 把 yt-dlp/网络的英文错误原文映射为可读的中文提示，未命中时原样返回。
/// 只做包含匹配（区分大小写不敏感），命中顺序即优先级，具体规则在前。
/// </summary>
public static class ErrorMessages
{
    public static string Friendly(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return "解析或下载失败，未返回具体错误信息";
        }

        var r = raw;

        // —— 平台风控 / 登录类 ——
        if (r.Contains("Fresh cookies", StringComparison.OrdinalIgnoreCase))
        {
            return "平台要求浏览器 Cookie 验证，请确认 Edge/Chrome 已安装并登录后重试";
        }

        if (r.Contains("Sign in to confirm", StringComparison.OrdinalIgnoreCase))
        {
            return "平台要求登录验证（机器人检查），暂时无法解析该内容";
        }

        if (r.Contains("members-only", StringComparison.OrdinalIgnoreCase))
        {
            return "该内容为会员专享，无法下载";
        }

        if (r.Contains("Private video", StringComparison.OrdinalIgnoreCase)
            || r.Contains("video is private", StringComparison.OrdinalIgnoreCase))
        {
            return "该内容为私密内容，无法下载";
        }

        // —— 链接有效性 ——
        if (r.Contains("Unsupported URL", StringComparison.OrdinalIgnoreCase))
        {
            return "该链接类型暂不支持解析，请使用 App 内「复制链接」得到的分享链接";
        }

        if (r.Contains("not a valid URL", StringComparison.OrdinalIgnoreCase))
        {
            return "链接格式无效，请检查后重新粘贴";
        }

        if (r.Contains("HTTP Error 404", StringComparison.OrdinalIgnoreCase)
            || r.Contains("404 Not Found", StringComparison.OrdinalIgnoreCase)
            || r.Contains("Unable to extract", StringComparison.OrdinalIgnoreCase))
        {
            return "链接已失效或内容不存在（页面结构可能已变化）";
        }

        if (r.Contains("Video unavailable", StringComparison.OrdinalIgnoreCase))
        {
            return "内容不可用，可能已被删除或下架";
        }

        // —— 访问限制 ——
        if (r.Contains("HTTP Error 403", StringComparison.OrdinalIgnoreCase)
            || r.Contains("403 Forbidden", StringComparison.OrdinalIgnoreCase))
        {
            return "内容被平台限制访问，可稍后重试";
        }

        if (r.Contains("HTTP Error 429", StringComparison.OrdinalIgnoreCase)
            || r.Contains("Too Many Requests", StringComparison.OrdinalIgnoreCase))
        {
            return "请求过于频繁被限流，请稍后再试";
        }

        // —— 网络层 ——
        if (r.Contains("getaddrinfo", StringComparison.OrdinalIgnoreCase)
            || r.Contains("Connection", StringComparison.OrdinalIgnoreCase)
            || r.Contains("timed out", StringComparison.OrdinalIgnoreCase)
            || r.Contains("timeout", StringComparison.OrdinalIgnoreCase)
            || r.Contains("Unable to download", StringComparison.OrdinalIgnoreCase))
        {
            return "网络连接失败，请检查网络（境外平台需要可用代理，应用暂未提供代理设置）";
        }

        // —— 本地环节 ——
        if (r.Contains("No space left", StringComparison.OrdinalIgnoreCase))
        {
            return "磁盘空间不足，请清理保存目录所在分区";
        }

        if (r.Contains("Permission denied", StringComparison.OrdinalIgnoreCase)
            || r.Contains("Access denied", StringComparison.OrdinalIgnoreCase)
            || r.Contains("UnauthorizedAccess", StringComparison.OrdinalIgnoreCase))
        {
            return "文件写入被拒绝，请检查保存目录权限或换一个目录";
        }

        if (r.Contains("ffmpeg", StringComparison.OrdinalIgnoreCase))
        {
            return "音视频合并失败（ffmpeg 处理出错），可重试一次";
        }

        if (r.Contains("No video formats found", StringComparison.OrdinalIgnoreCase))
        {
            return "未能解析出可下载的视频流";
        }

        // 未命中：保留原文，便于排查
        return raw;
    }
}
