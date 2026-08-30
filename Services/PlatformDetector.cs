namespace MyApp.Services;

public static class PlatformDetector
{
    public static string Detect(string url)
    {
        var lower = url.ToLowerInvariant();
        if (lower.Contains("bilibili.com") || lower.Contains("b23.tv"))
        {
            return "哔哩哔哩";
        }

        if (lower.Contains("douyin.com") || lower.Contains("iesdouyin.com"))
        {
            return "抖音";
        }

        if (lower.Contains("xiaohongshu.com") || lower.Contains("xhslink.com"))
        {
            return "小红书";
        }

        if (lower.Contains("kuaishou.com"))
        {
            return "快手";
        }

        if (lower.Contains("weibo.com") || lower.Contains("weibo.cn"))
        {
            return "微博";
        }

        if (lower.Contains("youtube.com") || lower.Contains("youtu.be"))
        {
            return "YouTube";
        }

        if (lower.Contains("twitter.com") || lower.Contains("x.com"))
        {
            return "X";
        }

        if (lower.Contains("instagram.com"))
        {
            return "Instagram";
        }

        if (lower.Contains("tiktok.com"))
        {
            return "TikTok";
        }

        return "其他";
    }
}
