using Microsoft.UI.Xaml;
using Microsoft.UI.Windowing;
using MyApp.Services;
using MyApp.Ui;
using MyApp.Views;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace MyApp;

/// <summary>
/// The application window. This hosts a Frame that displays pages. UI and
/// logic live in Views/ShellPage.xaml / Views/PlatformPage.xaml so they can
/// use Page features such as navigation events and the Loaded lifecycle.
/// </summary>
public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        AppWindow.SetIcon("Assets/AppIcon.ico");

        // 更舒适的默认窗口尺寸（内容区上限 1080 居中，窗口略大于内容）
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1120, 780));

        // 限制最小尺寸，避免缩得太小时布局挤压
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = 720;
            presenter.PreferredMinimumHeight = 560;
        }

        // 启动时应用持久化的主题偏好（挂窗口根元素，级联到全部页面与标题栏）
        if (Content is FrameworkElement themeRoot)
        {
            themeRoot.RequestedTheme = ThemeHelper.ToElementTheme(AppSettings.Instance.ThemeMode);
        }

        // Navigate the root frame to the shell page on startup.
        RootFrame.Navigate(typeof(ShellPage));
    }

    /// <summary>
    /// 配色热切换的兜底刷新：同帧把根元素与 RootFrame 的 RequestedTheme 翻到对侧再复原，
    /// 强制全树 ThemeResource 重新求值（同步两次赋值不产生中间渲染帧，无可见闪烁）。
    /// 供 Ui.ThemePalette.Apply 在每次切换后调用。RootFrame 若带显式主题（不同于根），
    /// 其有效主题不随根翻转，须单独翻才能让其子树重算。
    /// </summary>
    public void ForceThemeResourceRefresh()
    {
        FlipTheme(Content as FrameworkElement);
        FlipTheme(RootFrame);

        static void FlipTheme(FrameworkElement? element)
        {
            if (element == null)
            {
                return;
            }

            var original = element.RequestedTheme;
            element.RequestedTheme = original == ElementTheme.Light ? ElementTheme.Dark : ElementTheme.Light;
            element.RequestedTheme = original;
        }
    }
}
