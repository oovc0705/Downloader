using System.Diagnostics;

namespace MyApp.Services;

/// <summary>资源管理器定位/打开工具，任务列表与历史记录共用。</summary>
public static class FileLocations
{
    public static void Reveal(string? filePath)
    {
        if (string.IsNullOrEmpty(filePath))
        {
            return;
        }

        string argument;
        if (File.Exists(filePath))
        {
            argument = $"/select,\"{filePath}\"";
        }
        else
        {
            var dir = Path.GetDirectoryName(filePath);
            if (dir is null || !Directory.Exists(dir))
            {
                return;
            }

            argument = $"/open,\"{dir}\"";
        }

        using var explorer = Process.Start(
            new ProcessStartInfo("explorer.exe", argument)
            {
                UseShellExecute = true,
            });
    }
}
