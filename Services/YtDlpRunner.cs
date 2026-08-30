using System.Diagnostics;
using System.Text;
using MyApp.Models;

namespace MyApp.Services;

public sealed class YtDlpProgress
{
    public double? Percent { get; init; }
    public string? Speed { get; init; }
    public string? Eta { get; init; }
    public bool IsProcessing { get; init; }
    public string? FilePath { get; init; }
}

public sealed class YtDlpException : Exception
{
    public YtDlpException(string message) : base(message)
    {
    }
}

public sealed class YtDlpRunner
{
    public const string ProgressMarker = "@@PROGRESS@@";

    private static readonly string ToolsDir = Path.Combine(AppContext.BaseDirectory, "tools");

    private static string YtDlpPath => Path.Combine(ToolsDir, "yt-dlp.exe");

    public static bool IsToolAvailable() => File.Exists(YtDlpPath);

    public async Task<string> GetInfoJsonAsync(
        string url,
        CancellationToken ct,
        string? cookieSource = null,
        string? proxy = null)
    {
        EnsureTools();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(90));

        var psi = CreateStartInfo();
        psi.ArgumentList.Add("--dump-single-json");
        psi.ArgumentList.Add("--no-warnings");
        psi.ArgumentList.Add("--skip-download");
        ApplyCookieOptions(psi, cookieSource);
        ApplyPlatformOptions(psi, url);
        ApplyProxyOptions(psi, proxy);
        psi.ArgumentList.Add("--encoding");
        psi.ArgumentList.Add("utf-8");
        psi.ArgumentList.Add("--");
        psi.ArgumentList.Add(url);

        var (stdout, stderr, code) = await RunProcessAsync(psi, timeoutCts.Token).ConfigureAwait(false);
        if (code != 0 || string.IsNullOrWhiteSpace(stdout))
        {
            throw new YtDlpException(ExtractError(stderr, stdout));
        }

        return stdout;
    }

    public async Task DownloadAsync(
        DownloadItem item,
        string outputDir,
        string formatSelector,
        string? cookieSource,
        Action<YtDlpProgress> onProgress,
        CancellationToken ct,
        string? proxy = null,
        bool extractAudio = false)
    {
        EnsureTools();
        Directory.CreateDirectory(outputDir);

        var psi = CreateStartInfo();
        var outputTemplate = Path.Combine(outputDir, "%(title).100s [%(id)s].%(ext)s");

        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add(formatSelector);
        psi.ArgumentList.Add("--merge-output-format");
        psi.ArgumentList.Add("mp4");
        ApplyProxyOptions(psi, proxy);
        if (extractAudio)
        {
            // 下载完成后用 ffmpeg 提取音频，仅保留 MP3（[ExtractAudio] 阶段已在进度里映射为合并中）
            psi.ArgumentList.Add("-x");
            psi.ArgumentList.Add("--audio-format");
            psi.ArgumentList.Add("mp3");
            psi.ArgumentList.Add("--audio-quality");
            psi.ArgumentList.Add("0");
        }
        ApplyCookieOptions(psi, cookieSource);
        ApplyPlatformOptions(psi, item.Url);
        psi.ArgumentList.Add("--newline");
        psi.ArgumentList.Add("--progress");
        psi.ArgumentList.Add("--progress-template");
        psi.ArgumentList.Add($"download:{ProgressMarker}|%(progress._percent_str)s|%(progress._speed_str)s|%(progress._eta_str)s");
        psi.ArgumentList.Add("--no-simulate");
        psi.ArgumentList.Add("--print");
        psi.ArgumentList.Add("after_move:filepath");
        psi.ArgumentList.Add("--windows-filenames");
        psi.ArgumentList.Add("--concurrent-fragments");
        psi.ArgumentList.Add("4");
        psi.ArgumentList.Add("--encoding");
        psi.ArgumentList.Add("utf-8");
        psi.ArgumentList.Add("-o");
        psi.ArgumentList.Add(outputTemplate);
        psi.ArgumentList.Add("--");
        psi.ArgumentList.Add(item.Url);

        using var process = new Process { StartInfo = psi };
        var errorTail = new List<string>();
        string? errorMessage = null;

        process.Start();

        void KillTree()
        {
            try
            {
                using var killer = Process.Start(new ProcessStartInfo("taskkill", $"/PID {process.Id} /T /F")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                });
                killer?.WaitForExit(5000);
            }
            catch
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(true);
                    }
                }
                catch
                {
                    // process already gone
                }
            }
        }

        await using var _ = ct.Register(KillTree).ConfigureAwait(false);

        async Task PumpStdoutAsync()
        {
            while (await process.StandardOutput.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                line = line.TrimEnd('\r');
                if (line.StartsWith(ProgressMarker, StringComparison.Ordinal))
                {
                    var parts = line.Split('|');
                    double? percent = null;
                    if (parts.Length > 1 && double.TryParse(parts[1].Trim().TrimEnd('%'), out var p))
                    {
                        percent = p;
                    }

                    onProgress(new YtDlpProgress
                    {
                        Percent = percent,
                        Speed = parts.Length > 2 ? parts[2].Trim() : null,
                        Eta = parts.Length > 3 ? parts[3].Trim() : null,
                    });
                }
                else if (!string.IsNullOrWhiteSpace(line) && File.Exists(line.Trim()))
                {
                    onProgress(new YtDlpProgress { FilePath = line.Trim() });
                }
            }
        }

        var stdoutTask = PumpStdoutAsync();

        while (await process.StandardError.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            line = line.TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            if (line.Contains("[Merger]") || line.Contains("[ExtractAudio]") || line.Contains("[VideoConvert]"))
            {
                onProgress(new YtDlpProgress { IsProcessing = true });
            }

            if (line.Contains("ERROR:", StringComparison.Ordinal))
            {
                errorMessage ??= line[(line.IndexOf("ERROR:", StringComparison.Ordinal) + "ERROR:".Length)..].Trim();
            }

            errorTail.Add(line);
            if (errorTail.Count > 15)
            {
                errorTail.RemoveAt(0);
            }
        }

        await stdoutTask.ConfigureAwait(false);
        await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);

        if (ct.IsCancellationRequested)
        {
            throw new OperationCanceledException(ct);
        }

        if (process.ExitCode != 0)
        {
            throw new YtDlpException(errorMessage ?? ExtractError(string.Join(Environment.NewLine, errorTail), null));
        }
    }

    private static void ApplyPlatformOptions(ProcessStartInfo psi, string url)
    {
        if (url.Contains("douyin", StringComparison.OrdinalIgnoreCase))
        {
            psi.ArgumentList.Add("--referer");
            psi.ArgumentList.Add("https://www.douyin.com/");
        }
    }

    private static void ApplyProxyOptions(ProcessStartInfo psi, string? proxy)
    {
        if (string.IsNullOrWhiteSpace(proxy))
        {
            return;
        }

        psi.ArgumentList.Add("--proxy");
        psi.ArgumentList.Add(proxy.Trim());
    }

    private static void ApplyCookieOptions(ProcessStartInfo psi, string? cookieSource)    {
        if (string.IsNullOrEmpty(cookieSource))
        {
            return;
        }

        if (cookieSource.Contains('\\') || cookieSource.Contains('/')
            || File.Exists(cookieSource))
        {
            psi.ArgumentList.Add("--cookies");
            psi.ArgumentList.Add(cookieSource);
        }
        else
        {
            psi.ArgumentList.Add("--cookies-from-browser");
            psi.ArgumentList.Add(cookieSource);
        }
    }

    private static ProcessStartInfo CreateStartInfo()
    {
        return new ProcessStartInfo
        {
            FileName = YtDlpPath,
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
    }

    private static async Task<(string Stdout, string Stderr, int ExitCode)> RunProcessAsync(
        ProcessStartInfo psi, CancellationToken ct)
    {
        using var process = new Process { StartInfo = psi };
        process.Start();

        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
        await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);

        if (ct.IsCancellationRequested)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(true);
                }
            }
            catch
            {
                // already exited
            }

            throw new OperationCanceledException(ct);
        }

        return (stdoutTask.Result, stderrTask.Result, process.ExitCode);
    }

    private static string ExtractError(string? stderr, string? stdout)
    {
        foreach (var text in new[] { stderr, stdout })
        {
            if (string.IsNullOrEmpty(text))
            {
                continue;
            }

            var idx = text.LastIndexOf("ERROR:", StringComparison.Ordinal);
            if (idx >= 0)
            {
                var end = text.IndexOf('\n', idx);
                return end > idx ? text[(idx + 6)..end].Trim() : text[(idx + 6)..].Trim();
            }
        }

        return "下载失败，请检查链接是否有效（部分平台内容需要登录后才能下载）";
    }

    private static void EnsureTools()
    {
        if (!File.Exists(YtDlpPath))
        {
            throw new YtDlpException($"未找到解析引擎：{YtDlpPath}");
        }
    }
}
