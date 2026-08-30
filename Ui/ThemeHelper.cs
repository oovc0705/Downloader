using Microsoft.UI.Xaml;

namespace MyApp.Ui;

/// <summary>
/// 主题切换：WinUI 的 Application.RequestedTheme 加载后只读，
/// 运行时切换挂在窗口根元素上（RequestedTheme 级联到全部子树）。
/// </summary>
public static class ThemeHelper
{
    public static ElementTheme ToElementTheme(string mode) => mode switch
    {
        "light" => ElementTheme.Light,
        "dark" => ElementTheme.Dark,
        _ => ElementTheme.Default,
    };

    /// <summary>立即把设置里的主题应用到窗口根内容；Default 表示跟随系统。
    /// Window.Content 的静态类型是 UIElement，RequestedTheme 在其子类 FrameworkElement 上。</summary>
    public static void Apply(string mode)
    {
        if (App.MainWindow?.Content is FrameworkElement root)
        {
            root.RequestedTheme = ToElementTheme(mode);
        }
    }

    /// <summary>
    /// 配色方案热切换的兜底刷新：把窗口根元素与 RootFrame 的 RequestedTheme 同帧翻到对侧再复原，
    /// 强制全树 ThemeResource 重新求值。两次赋值在同一个分发回调内同步完成、中间不产生渲染帧，
    /// 因此无可见闪烁（此前"翻转有闪烁风险"指异步翻转两帧的情形）。
    /// 根元素与 RootFrame 都要翻：RootFrame 可能带着显式 light/dark（启动时按设置挂上），
    /// 其有效主题不随根翻转变化，单独翻才会让其子树重算。
    /// </summary>
    public static void ForceResourceRefresh()
    {
        (App.MainWindow as MainWindow)?.ForceThemeResourceRefresh();
    }
}
