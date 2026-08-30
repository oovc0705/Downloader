using System.Text.Json;
using System.Text.Json.Serialization;

namespace MyApp.Services;

/// <summary>
/// 用户设置持久化（settings.json）。UI 与服务层都从 Instance 单例读取，
/// 修改属性后调用 Save() 即可落盘。新增字段对旧文件向后兼容（缺失即默认值）。
/// </summary>
public sealed class AppSettings
{
    private static readonly object Sync = new();
    private static AppSettings? _instance;

    public static AppSettings Instance
    {
        get
        {
            lock (Sync)
            {
                return _instance ??= Load();
            }
        }
    }

    public string OutputDir { get; set; } = DownloadService.DefaultOutputDir();

    public bool AskQuality { get; set; } = true;

    /// <summary>上次停留的平台分区，启动时恢复导航选中项。</summary>
    public string LastPlatform { get; set; } = "抖音";

    /// <summary>是否给 yt-dlp 传 --proxy（解决 YouTube/X/Instagram 等直连不通）。</summary>
    public bool ProxyEnabled { get; set; }

    public string ProxyUrl { get; set; } = "";

    /// <summary>最大并发下载数（1-4）；并发信号量在启动时创建，改后重启生效。</summary>
    public int MaxConcurrency { get; set; } = 2;

    /// <summary>下载完成后用 ffmpeg 提取 MP3（不保留视频文件）。</summary>
    public bool ExtractAudio { get; set; }

    /// <summary>应用主题："system" / "light" / "dark"。</summary>
    public string ThemeMode { get; set; } = "system";

    /// <summary>配色方案 Id（见 Ui/ThemePalette.Presets）：amber / rose / ocean / forest / classic。</summary>
    public string ThemePreset { get; set; } = "amber";

    /// <summary>切回窗口时自动检测剪贴板中的支持平台链接并填入对应分区。</summary>
    public bool AutoFillClipboard { get; set; } = true;

    public void Save()
    {
        var snapshot = this;
        Task.Run(async () =>
        {
            try
            {
                Directory.CreateDirectory(AppFolders.DataDir);
                var path = Path.Combine(AppFolders.DataDir, "settings.json");
                await using var stream = File.Create(path);
                await JsonSerializer.SerializeAsync(stream, snapshot, SettingsJsonContext.Default.AppSettings)
                    .ConfigureAwait(false);
            }
            catch
            {
                // 设置写失败不影响本次运行，下次修改会重试覆盖
            }
        });
    }

    private static AppSettings Load()
    {
        try
        {
            var path = Path.Combine(AppFolders.DataDir, "settings.json");
            if (File.Exists(path))
            {
                using var stream = File.OpenRead(path);
                if (JsonSerializer.Deserialize(stream, SettingsJsonContext.Default.AppSettings) is { } loaded)
                {
                    return loaded;
                }
            }
        }
        catch
        {
            // 损坏的设置文件按默认值启动
        }

        return new AppSettings();
    }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext;
