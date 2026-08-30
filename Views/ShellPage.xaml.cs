using System.Text.RegularExpressions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using MyApp.Services;

namespace MyApp.Views;

/// <summary>
/// 顶层外壳：顶部导航栏按平台分区 + 设置入口，Frame 承载参数化的 PlatformPage。
/// 另提供剪贴板自动填充：切回窗口时检测到支持平台的链接，自动跳到对应分区并填入输入框。
/// </summary>
public sealed partial class ShellPage : Page
{
    private string? _lastClipboardText;

    public ShellPage()
    {
        InitializeComponent();

        // 恢复上次停留的平台分区；未收录（改名/回滚）时落到第一项
        var saved = AppSettings.Instance.LastPlatform;
        var target = NavView.MenuItems
            .OfType<NavigationViewItem>()
            .FirstOrDefault(i => (string?)i.Tag == saved) ?? (NavigationViewItem)NavView.MenuItems[0];
        NavView.SelectedItem = target;

        if (App.MainWindow is not null)
        {
            App.MainWindow.Activated += OnWindowActivated;
        }

        Unloaded += (_, _) =>
        {
            if (App.MainWindow is not null)
            {
                App.MainWindow.Activated -= OnWindowActivated;
            }
        };
    }

    [GeneratedRegex(@"https?://\S+")]
    private static partial Regex UrlPattern();

    private string? SelectedTag => NavView.SelectedItem is NavigationViewItem item ? item.Tag as string : null;

    private void OnNavSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        var tag = args.SelectedItemContainer?.Tag as string ?? string.Empty;

        if (tag == "settings")
        {
            if (ContentFrame.Content is not SettingsPage)
            {
                ContentFrame.Content = new SettingsPage();
            }

            return;
        }

        if (tag.Length == 0)
        {
            return;
        }

        if (ContentFrame.Content is PlatformPage page && page.PlatformName == tag)
        {
            return;
        }

        AppSettings.Instance.LastPlatform = tag;
        AppSettings.Instance.Save();
        ContentFrame.Content = new PlatformPage(tag);
    }

    // ---- 剪贴板自动填充 ----

    private async void OnWindowActivated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState == WindowActivationState.Deactivated
            || !AppSettings.Instance.AutoFillClipboard)
        {
            return;
        }

        try
        {
            var content = Clipboard.GetContent();
            if (!content.Contains(StandardDataFormats.Text))
            {
                return;
            }

            var text = (await content.GetTextAsync()).Trim();
            if (text.Length == 0 || text == _lastClipboardText)
            {
                return;
            }

            _lastClipboardText = text;

            // 分享口令里常带前后文字，取第一段 http(s) 子串
            var match = UrlPattern().Match(text);
            if (!match.Success)
            {
                return;
            }

            var url = match.Value.TrimEnd('.', '。', '，', ',');
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                return;
            }

            var platform = PlatformDetector.Detect(url);
            if (!DownloadService.SupportedPlatforms.Contains(platform))
            {
                return;
            }

            SelectPlatform(platform);
            if (ContentFrame.Content is PlatformPage target && target.PlatformName == platform)
            {
                target.SetUrlText(url);
            }
        }
        catch
        {
            // 剪贴板可能被其他进程占用，静默跳过本次检测
        }
    }

    private void SelectPlatform(string platform)
    {
        var item = NavView.MenuItems
            .OfType<NavigationViewItem>()
            .FirstOrDefault(i => (string?)i.Tag == platform);
        if (item is not null && !ReferenceEquals(NavView.SelectedItem, item))
        {
            // 触发 SelectionChanged 创建/切换到对应分区页
            NavView.SelectedItem = item;
        }
    }
}
