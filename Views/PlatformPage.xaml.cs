using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using MyApp.Models;
using MyApp.Services;
using Windows.Storage.Pickers;

namespace MyApp.Views;

/// <summary>
/// 单个平台分区页：URL 输入、任务列表（绑定 DownloadService 对应平台的集合）、历史记录。
/// 六个平台共用这一个类，构造时传入平台名。
/// </summary>
public sealed partial class PlatformPage : Page
{
    /// <summary>状态色板查找失败时的兜底刷子（理论上不可达）。</summary>
    private static readonly Brush StatusBrushFallback = new SolidColorBrush(Microsoft.UI.Colors.Gray);

    public string PlatformName { get; }

    /// <summary>当前平台的全量历史（真源），HistoryItems 是按搜索词过滤后的视图。</summary>
    private readonly List<HistoryRecord> _historyMaster = new();

    private ObservableCollection<HistoryRecord> HistoryItems { get; } = new();

    public ObservableCollection<DownloadItem> Tasks => App.Downloads.GetItems(PlatformName);

    public string FolderDisplay => $"保存到：{App.Downloads.OutputDir}";

    private int _noticeToken;

    public PlatformPage(string platformName)
    {
        PlatformName = platformName;
        InitializeComponent();
        AskQualityCheck.IsChecked = App.Downloads.AskQuality;

        Tasks.CollectionChanged += OnTasksChanged;
        HistoryItems.CollectionChanged += OnHistoryChanged;
        HistoryStore.Added += OnStoreRecordAdded;
        HistoryStore.Removed += OnStoreRecordRemoved;
        HistoryStore.Cleared += OnStoreCleared;
        Unloaded += (_, _) =>
        {
            Tasks.CollectionChanged -= OnTasksChanged;
            HistoryItems.CollectionChanged -= OnHistoryChanged;
            HistoryStore.Added -= OnStoreRecordAdded;
            HistoryStore.Removed -= OnStoreRecordRemoved;
            HistoryStore.Cleared -= OnStoreCleared;
        };

        foreach (var record in HistoryStore.Snapshot().Where(r => r.Platform == PlatformName))
        {
            _historyMaster.Add(record);
        }

        RebuildHistoryView();
        UpdateEmptyHints();
        Loaded += (_, _) => UrlBox.Focus(FocusState.Programmatic);
    }

    // ---- 可见性 / 文案转换（x:Bind 函数绑定用） ----

    public static Visibility ActiveVis(bool isActive)
        => isActive ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility CompletedVis(DownloadStatus status)
        => status == DownloadStatus.Completed ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility RetryVis(DownloadStatus status)
        => status is DownloadStatus.Failed or DownloadStatus.Cancelled
            ? Visibility.Visible
            : Visibility.Collapsed;

    public static Visibility BarVis(DownloadStatus status)
        => status is DownloadStatus.Resolving or DownloadStatus.Downloading or DownloadStatus.Processing
            ? Visibility.Visible
            : Visibility.Collapsed;

    public static bool IsBarIndeterminate(DownloadStatus status)
        => status is DownloadStatus.Resolving or DownloadStatus.Processing;

    public static Visibility ErrorVis(string error)
        => string.IsNullOrEmpty(error) ? Visibility.Collapsed : Visibility.Visible;

    public static string ProgressText(double progress) => $"{Math.Round(progress):0}%";

    public static string StatusWord(DownloadStatus status) => status switch
    {
        DownloadStatus.Pending => "排队中",
        DownloadStatus.Resolving => "解析中",
        DownloadStatus.Downloading => "下载中",
        DownloadStatus.Processing => "合并中",
        DownloadStatus.Completed => "已完成",
        DownloadStatus.Failed => "失败",
        DownloadStatus.Cancelled => "已取消",
        _ => string.Empty,
    };

    /// <summary>状态胶囊的前景色（按当前明暗主题取色）。</summary>
    public static Brush PillForeground(DownloadStatus status) => StatusBrush(status, foreground: true);

    /// <summary>状态胶囊的背景色（低透明度同色系浅底，明暗主题通用）。</summary>
    public static Brush PillBackground(DownloadStatus status) => StatusBrush(status, foreground: false);

    private static Brush StatusBrush(DownloadStatus status, bool foreground)
    {
        var dark = Application.Current.RequestedTheme == ApplicationTheme.Dark;
        string? key = status switch
        {
            DownloadStatus.Resolving or DownloadStatus.Downloading
                => foreground ? (dark ? "StatusAttentionFgDark" : "StatusAttentionFg") : "StatusAttentionBg",
            DownloadStatus.Processing
                => foreground ? (dark ? "StatusCautionFgDark" : "StatusCautionFg") : "StatusCautionBg",
            DownloadStatus.Completed
                => foreground ? (dark ? "StatusSuccessFgDark" : "StatusSuccessFg") : "StatusSuccessBg",
            DownloadStatus.Failed
                => foreground ? (dark ? "StatusCriticalFgDark" : "StatusCriticalFg") : "StatusCriticalBg",
            DownloadStatus.Pending or DownloadStatus.Cancelled
                => foreground ? (dark ? "StatusNeutralFgDark" : "StatusNeutralFg") : "StatusNeutralBg",
            _ => null,
        };

        if (key is not null
            && Application.Current.Resources.TryGetValue(key, out var value)
            && value is Brush brush)
        {
            return brush;
        }

        return StatusBrushFallback;
    }

    /// <summary>清晰度、速度、剩余时间拼成一行元信息。</summary>
    public static string MetaText(string quality, string speed, string eta)
    {
        var parts = new List<string>(3);
        if (!string.IsNullOrEmpty(quality))
        {
            parts.Add(quality);
        }

        if (!string.IsNullOrEmpty(speed))
        {
            parts.Add(speed);
        }

        if (!string.IsNullOrEmpty(eta))
        {
            parts.Add($"剩余 {eta}");
        }

        return string.Join("  ·  ", parts);
    }

    public static Visibility MetaVis(string quality, string speed, string eta)
        => quality.Length == 0 && speed.Length == 0 && eta.Length == 0
            ? Visibility.Collapsed
            : Visibility.Visible;

    public static string FormatTime(DateTime time) => $"{time:yy-MM-dd HH:mm}";

    public static Visibility StatusOkVis(string statusText)
        => statusText == "已完成" ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility StatusFailVis(string statusText)
        => statusText != "已完成" ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility HistoryFileVis(string? filePath)
        => string.IsNullOrEmpty(filePath) ? Visibility.Collapsed : Visibility.Visible;

    // ---- 入队 ----

    /// <summary>供外壳的剪贴板自动填充调用：填入链接、刷新路由提示并聚焦输入框。</summary>
    public void SetUrlText(string url)
    {
        UrlBox.Text = url;
        UpdateRouteHint();
        HideNotice();
        UrlBox.Focus(FocusState.Programmatic);
    }

    private void OnUrlBoxKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            TryEnqueue();
        }
    }

    private void OnDownloadClick(object sender, RoutedEventArgs e) => TryEnqueue();

    private void OnUrlBoxTextChanged(object sender, TextChangedEventArgs e) => UpdateRouteHint();

    /// <summary>输入过程中实时提示：链接属于其他支持平台时告知归队去向，避免「点了没反应」的困惑。</summary>
    private void UpdateRouteHint()
    {
        var url = UrlBox.Text.Trim();
        string? hint = null;
        if (url.Length > 0 && Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            var detected = PlatformDetector.Detect(url);
            if (detected != PlatformName)
            {
                hint = DownloadService.SupportedPlatforms.Contains(detected)
                    ? $"该链接属于「{detected}」，点击下载后会自动归入对应分区"
                    : $"「{detected}」暂不支持下载";
            }
        }

        RouteHint.Text = hint ?? string.Empty;
        RouteHint.Visibility = hint is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void TryEnqueue()
    {
        var url = UrlBox.Text.Trim();
        if (url.Length == 0 || !Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            ShowNotice("请输入有效的 http(s) 链接", InfoBarSeverity.Warning);
            return;
        }

        var (accepted, detected, item) = App.Downloads.Enqueue(url);
        if (!accepted)
        {
            ShowNotice(detected == "其他"
                ? "暂不支持该链接所属的平台"
                : $"「{detected}」暂不在支持列表，请在导航栏选择已支持的平台",
                InfoBarSeverity.Warning);
            return;
        }

        HideNotice();
        UrlBox.Text = string.Empty;
        UpdateRouteHint();
        UrlBox.Focus(FocusState.Keyboard);

        if (item is not null && item.Platform != PlatformName)
        {
            ShowNotice($"已识别为「{item.Platform}」链接，任务已加入「{item.Platform}」分区",
                InfoBarSeverity.Informational);
        }
    }

    /// <summary>输入卡内的通知条；提示类（Informational/Success）6 秒后自动收起。</summary>
    private void ShowNotice(string message, InfoBarSeverity severity)
    {
        InputNotice.Message = message;
        InputNotice.Severity = severity;
        InputNotice.IsOpen = true;

        if (severity is InfoBarSeverity.Informational or InfoBarSeverity.Success)
        {
            var token = ++_noticeToken;
            _ = Task.Delay(TimeSpan.FromSeconds(6)).ContinueWith(
                _ => DispatcherQueue.TryEnqueue(() =>
                {
                    if (token == _noticeToken)
                    {
                        InputNotice.IsOpen = false;
                    }
                }));
        }
    }

    private void HideNotice()
    {
        _noticeToken++;
        InputNotice.IsOpen = false;
    }

    // ---- 任务列表操作 ----

    private void OnCancelItemClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is DownloadItem item)
        {
            App.Downloads.Cancel(item);
        }
    }

    private void OnTaskRetryClick(object sender, RoutedEventArgs e)
    {
        // 失败/取消的任务原样重新入队（重新解析，产生新任务行）
        if ((sender as FrameworkElement)?.DataContext is DownloadItem item && !item.IsActive)
        {
            App.Downloads.Enqueue(item.Url);
        }
    }

    private void OnTaskOpenItemClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is DownloadItem item)
        {
            App.Downloads.OpenContainingFolder(item);
        }
    }

    private void OnClearFinishedClick(object sender, RoutedEventArgs e)
        => App.Downloads.RemoveFinished(PlatformName);

    private void OnAskQualityChanged(object sender, RoutedEventArgs e)
    {
        // 构造期间赋 IsChecked 也会触发本事件，与 AppSettings 写回等值无害
        App.Downloads.AskQuality = AskQualityCheck.IsChecked == true;
    }

    private async void OnPickFolderClick(object sender, RoutedEventArgs e)
    {
        if (App.MainWindow is null)
        {
            return;
        }

        var picker = new FolderPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));
        picker.SuggestedStartLocation = PickerLocationId.Downloads;
        picker.FileTypeFilter.Add("*");

        if (await picker.PickSingleFolderAsync() is { } folder)
        {
            App.Downloads.OutputDir = folder.Path;
            Bindings.Update();
        }
    }

    // ---- 历史记录 ----

    private void OnStoreRecordAdded(HistoryRecord record)
    {
        if (record.Platform != PlatformName || _historyMaster.Any(r => r.Id == record.Id))
        {
            return;
        }

        _historyMaster.Insert(0, record);
        RebuildHistoryView();
    }

    private void OnStoreRecordRemoved(HistoryRecord record)
    {
        _historyMaster.RemoveAll(r => r.Id == record.Id);
        RebuildHistoryView();
    }

    private void OnStoreCleared()
    {
        _historyMaster.Clear();
        foreach (var record in HistoryStore.Snapshot().Where(r => r.Platform == PlatformName))
        {
            _historyMaster.Add(record);
        }

        RebuildHistoryView();
    }

    private void OnHistorySearchTextChanged(object sender, TextChangedEventArgs e) => RebuildHistoryView();

    /// <summary>按搜索词重建过滤视图；空词显示全量。构造期间 HistorySearchBox 为 null，显示全量。</summary>
    private void RebuildHistoryView()
    {
        var term = HistorySearchBox?.Text?.Trim() ?? string.Empty;
        HistoryItems.Clear();
        foreach (var record in _historyMaster)
        {
            if (term.Length == 0 || record.Title.Contains(term, StringComparison.OrdinalIgnoreCase))
            {
                HistoryItems.Add(record);
            }
        }

        if (HistoryEmptyCaption is not null && _historyMaster.Count > 0)
        {
            HistoryEmptyCaption.Text = term.Length == 0
                ? "下载完成的任务会自动归档在这里"
                : $"没有标题包含「{term}」的记录";
        }
    }

    private void OnHistoryOpenClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is HistoryRecord record)
        {
            FileLocations.Reveal(record.FilePath);
        }
    }

    private void OnHistoryItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is HistoryRecord record && !string.IsNullOrEmpty(record.FilePath))
        {
            FileLocations.Reveal(record.FilePath);
        }
    }

    private void OnHistoryDeleteClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is HistoryRecord record)
        {
            HistoryStore.Remove(record);
        }
        else
        {
            // 正常不会走到这里；留下诊断线索便于排查（crash.log）
            var element = sender as FrameworkElement;
            Ui.Dialogs.LogDiagnostic("history-delete", new Exception(
                $"DataContext 非记录：{element?.DataContext?.GetType().FullName ?? "null"}"));
        }
    }

    private async void OnClearHistoryClick(object sender, RoutedEventArgs e)
    {
        if (App.MainWindow?.Content?.XamlRoot is not { } root)
        {
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = "清空历史记录",
            Content = $"确定删除「{PlatformName}」的全部历史记录吗？已下载的文件不会被移除。",
            PrimaryButtonText = "清空",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            HistoryStore.Clear(PlatformName);
        }
    }

    // ---- 空态提示与计数 ----

    private void OnTasksChanged(object? sender, NotifyCollectionChangedEventArgs e) => UpdateEmptyHints();

    private void OnHistoryChanged(object? sender, NotifyCollectionChangedEventArgs e) => UpdateEmptyHints();

    private void UpdateEmptyHints()
    {
        TasksEmptyHint.Visibility = Tasks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        HistoryEmptyHint.Visibility = HistoryItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        TasksCountBlock.Text = Tasks.Count == 0 ? string.Empty : $"共 {Tasks.Count} 项";
        HistoryCountBlock.Text = HistoryItems.Count == 0 ? string.Empty : $"共 {HistoryItems.Count} 条";
    }
}
