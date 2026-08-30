using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MyApp.Services;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace MyApp;

/// <summary>
/// Provides application-specific behavior to supplement the default Application class.
/// </summary>
public partial class App : Application
{
    private Window? _window;

    public static Window? MainWindow { get; private set; }

    /// <summary>组合根：全局唯一的下载队列（含清晰度/图集选择回调）。</summary>
    public static DownloadService Downloads { get; private set; } = null!;

    /// <summary>
    /// Initializes the singleton application object.  This is the first line of authored code
    /// executed, and as such is the logical equivalent of main() or WinMain().
    /// </summary>
    public App()
    {
        InitializeComponent();
        // 诊断兜底：XAML stowed exception（0xc000027b）会直接杀进程，先落盘再阻止崩溃
        UnhandledException += (_, e) =>
        {
            e.Handled = true;
            try
            {
                File.AppendAllText(
                    Path.Combine(AppFolders.DataDir, "crash.log"),
                    $"[{DateTime.Now:HH:mm:ss}] app-unhandled: {e.Message}\n{e.Exception}\n\n");
            }
            catch
            {
                // 日志写失败时不干扰原异常
            }
        };
    }

    /// <summary>
    /// Invoked when the application is launched.
    /// </summary>
    /// <param name="args">Details about the launch request and process.</param>
    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        // 以下均在 UI 线程执行：DownloadService 需要 UI 线程的 DispatcherQueue
        // 把后台任务的进度封送回来；弹窗回调同样依赖该 Dispatcher。
        Downloads = new DownloadService();
        Downloads.QualityPrompt = Ui.Dialogs.ShowQualityAsync;
        Downloads.GalleryPrompt = Ui.Dialogs.ShowGalleryPickerAsync;
        Ui.Dialogs.Initialize(DispatcherQueue.GetForCurrentThread());

        // 在创建窗口前应用保存的配色方案，确保首帧就是正确配色
        Ui.ThemePalette.Apply(AppSettings.Instance.ThemePreset);
        _window = new MainWindow();
        MainWindow = _window;
        _window.Activate();
    }
}
