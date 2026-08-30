using System.Net.Http;
using System.Text;

namespace MyApp.Services;

public static class TtwidCookieProvider
{
    private static readonly HttpClient Http = new();
    private static DateTime _fetchedAt;

    private static string FilePath => Path.Combine(Path.GetTempPath(), "MyApp_douyin_cookies.txt");

    public static async Task<string?> GetCookiesFileAsync(CancellationToken ct)
    {
        if (File.Exists(FilePath)
            && new FileInfo(FilePath).Length > 0
            && DateTime.UtcNow - _fetchedAt < TimeSpan.FromHours(20))
        {
            return FilePath;
        }

        try
        {
            const string body = """
                {"region":"cn","aid":1768,"needFid":false,"service":"www.ixigua.com","migrate_info":{"ticket":"","source":"node"},"cbUrlProtocol":"https","union":true}
                """;
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://ttwid.bytedance.com/ttwid/union/register/")
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);

            string? ttwid = null;
            if (response.Headers.TryGetValues("Set-Cookie", out var cookies))
            {
                foreach (var cookie in cookies)
                {
                    if (cookie.StartsWith("ttwid=", StringComparison.OrdinalIgnoreCase))
                    {
                        ttwid = cookie.Split(';')[0]["ttwid=".Length..];
                        break;
                    }
                }
            }

            if (string.IsNullOrEmpty(ttwid))
            {
                return null;
            }

            var verify = GenerateVerifyId();
            var expires = DateTimeOffset.UtcNow.AddDays(30).ToUnixTimeSeconds();
            var content = "# Netscape HTTP Cookie File\n"
                + $".douyin.com\tTRUE\t/\tTRUE\t{expires}\tttwid\t{ttwid}\n"
                + $".douyin.com\tTRUE\t/\tTRUE\t{expires}\ts_v_web_id\t{verify}\n"
                + $".iesdouyin.com\tTRUE\t/\tTRUE\t{expires}\tttwid\t{ttwid}\n";
            await File.WriteAllTextAsync(FilePath, content, ct).ConfigureAwait(false);
            _fetchedAt = DateTime.UtcNow;
            return FilePath;
        }
        catch
        {
            return null;
        }
    }

    private static string GenerateVerifyId()
    {
        const string chars = "abcdefghijklmnopqrstuvwxyz0123456789";
        var segments = new[] { 8, 5, 5, 4, 8 };
        return "verify_" + string.Join("_", segments.Select(len =>
        {
            var buf = new char[len];
            for (var i = 0; i < len; i++)
            {
                buf[i] = chars[Random.Shared.Next(chars.Length)];
            }

            return new string(buf);
        }));
    }
}
