using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using MyApp.Models;
using MyApp.Services;

namespace MyApp.Ui;

/// <summary>
/// 服务层回调的统一弹窗入口。下载解析发生在后台线程，这里负责：
/// 封送回 UI 线程创建/展示（避免空 Message 的 COMException），
/// 以及用全局信号量排队——WinUI 同一时刻只允许一个 ContentDialog。
/// </summary>
public static class Dialogs
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    private static DispatcherQueue _dispatcher = null!;

    /// <summary>App 启动时在 UI 线程调用一次。</summary>
    public static void Initialize(DispatcherQueue dispatcher) => _dispatcher = dispatcher;

    public static Task<QualityOption?> ShowQualityAsync(DownloadItem item, IReadOnlyList<QualityOption> qualities)
    {
        if (qualities.Count == 0)
        {
            return Task.FromResult<QualityOption?>(null);
        }

        return MarshalAsync(async () =>
        {
            await Gate.WaitAsync().ConfigureAwait(true);
            try
            {
                return await ShowQualityOnUiAsync(item, qualities).ConfigureAwait(true);
            }
            finally
            {
                Gate.Release();
            }
        });
    }

    public static Task<IReadOnlyList<GalleryItem>?> ShowGalleryPickerAsync(DownloadItem item, ImageNote note)
    {
        if (note.Items.Count == 0)
        {
            return Task.FromResult<IReadOnlyList<GalleryItem>?>(null);
        }

        // 单条目没有选择价值，直接放行，避免每次弹窗的摩擦
        if (note.Items.Count == 1)
        {
            return Task.FromResult<IReadOnlyList<GalleryItem>?>(note.Items);
        }

        return MarshalAsync(async () =>
        {
            await Gate.WaitAsync().ConfigureAwait(true);
            try
            {
                return await ShowGalleryPickerOnUiAsync(item, note).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                LogDiagnostic("gallery-picker", ex);
                return null;
            }
            finally
            {
                Gate.Release();
            }
        });
    }

    internal static void LogDiagnostic(string scope, Exception ex)
    {
        try
        {
            File.AppendAllText(
                Path.Combine(AppFolders.DataDir, "crash.log"),
                $"[{DateTime.Now:HH:mm:ss}] {scope}: {ex}\n\n");
        }
        catch
        {
            // 诊断日志写失败时静默
        }
    }

    private static async Task<T?> MarshalAsync<T>(Func<Task<T?>> uiWork)
    {
        if (_dispatcher.HasThreadAccess)
        {
            return await uiWork().ConfigureAwait(true);
        }

        var tcs = new TaskCompletionSource<T?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _dispatcher.TryEnqueue(async () =>
        {
            try
            {
                tcs.SetResult(await uiWork());
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });

        return await tcs.Task.ConfigureAwait(false);
    }

    private static async Task<QualityOption?> ShowQualityOnUiAsync(DownloadItem item, IReadOnlyList<QualityOption> qualities)
    {
        if (App.MainWindow?.Content?.XamlRoot is not { } root)
        {
            return null;
        }

        var picker = new RadioButtons
        {
            ItemsSource = qualities.Select(q => q.Label).ToList(),
            SelectedIndex = 0,
        };

        // 选项较多（B站可达十余档）时在固定高度内滚动，避免撑破对话框
        var content = new ScrollViewer
        {
            Content = picker,
            MaxHeight = 360,
            Margin = new Thickness(0, 8, 0, 4),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };

        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = $"选择清晰度 · {item.Platform}",
            Content = content,
            PrimaryButtonText = "开始下载",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary || picker.SelectedIndex < 0)
        {
            return null;
        }

        return qualities[picker.SelectedIndex];
    }

    private static async Task<IReadOnlyList<GalleryItem>?> ShowGalleryPickerOnUiAsync(DownloadItem item, ImageNote note)
    {
        if (App.MainWindow?.Content?.XamlRoot is not { } root)
        {
            return null;
        }

        var tiles = note.Items.Select(i => new GalleryTile(i)).ToList();

        var gridView = new GridView
        {
            SelectionMode = ListViewSelectionMode.Multiple,
            ItemsSource = tiles,
            ItemTemplate = LoadTileTemplate(),
            Margin = new Thickness(-8, 4, -4, 0),
        };

        var selectedCount = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            Style = Application.Current.Resources["CaptionTextBlockStyle"] as Style,
            Foreground = Application.Current.Resources["TextFillColorSecondaryBrush"] as Brush,
        };

        Action refreshCount = () =>
        {
            var n = gridView.SelectionMode == ListViewSelectionMode.None ? 0 : gridView.SelectedItems.Count;
            selectedCount.Text = $"已选 {n} / {tiles.Count}";
        };

        var selectAll = new HyperlinkButton
        {
            Content = "全选",
            Padding = new Thickness(4),
        };
        var invert = new HyperlinkButton
        {
            Content = "反选",
            Padding = new Thickness(4),
            Margin = new Thickness(0, 0, 8, 0),
        };

        selectAll.Click += (_, _) =>
        {
            gridView.SelectAll();
            refreshCount();
        };
        invert.Click += (_, _) =>
        {
            foreach (var tile in tiles)
            {
                if (gridView.SelectedItems.Contains(tile))
                {
                    gridView.SelectedItems.Remove(tile);
                }
                else
                {
                    gridView.SelectedItems.Add(tile);
                }
            }

            refreshCount();
        };

        var header = new Grid { ColumnSpacing = 4 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(selectedCount, 0);
        Grid.SetColumn(selectAll, 2);
        Grid.SetColumn(invert, 3);
        header.Children.Add(selectedCount);
        header.Children.Add(selectAll);
        header.Children.Add(invert);

        // 内容标题：解析出的作品名，帮用户确认选对了链接
        var noteTitleBlock = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(note.Title) ? item.Title : note.Title,
            FontSize = 14,
            MaxLines = 1,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 0, 0, 2),
        };

        var content = new Grid
        {
            RowSpacing = 4,
            MaxHeight = 480,
        };
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(noteTitleBlock, 0);
        Grid.SetRow(header, 1);
        Grid.SetRow(gridView, 2);
        content.Children.Add(noteTitleBlock);
        content.Children.Add(header);
        content.Children.Add(gridView);

        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = $"选择要下载的内容 · {item.Platform}",
            Content = content,
            PrimaryButtonText = "开始下载",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };
        dialog.Resources["ContentDialogMaxWidth"] = 900d;

        void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var n = gridView.SelectedItems.Count;
            selectedCount.Text = $"已选 {n} / {tiles.Count}";
            dialog.IsPrimaryButtonEnabled = n > 0;
            dialog.PrimaryButtonText = n > 0 ? $"下载所选 {n} 项" : "请至少选择一项";
        }

        gridView.SelectionChanged += OnSelectionChanged;
        gridView.SelectAll();
        OnSelectionChanged(gridView, default!);

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary || gridView.SelectedItems.Count == 0)
        {
            return null;
        }

        var chosen = new List<GalleryItem>();
        var picked = gridView.SelectedItems.ToHashSet();
        foreach (var tile in tiles.Where(picked.Contains))
        {
            chosen.Add(tile.Item);
        }

        return chosen;
    }

    private static DataTemplate LoadTileTemplate()
    {
        const string xaml = """
            <DataTemplate
                xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
                <Grid Width="132" Height="132" Margin="2" CornerRadius="8"
                      Background="{ThemeResource SubtleFillColorTertiaryBrush}">
                    <Border CornerRadius="8">
                        <Image Source="{Binding Thumb}" Stretch="UniformToFill" />
                    </Border>
                    <Border
                        HorizontalAlignment="Left" VerticalAlignment="Bottom"
                        Margin="4" Padding="3"
                        CornerRadius="4"
                        Background="#B3000000"
                        Visibility="{Binding VideoBadgeVis}">
                        <FontIcon FontSize="11" Foreground="White" Glyph="&#xE8B2;" />
                    </Border>
                </Grid>
            </DataTemplate>
            """;
        return (DataTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load(xaml);
    }
}

/// <summary>图集勾选格子的绑定模型（运行时加载的模板走经典 Binding，无需 INPC）。</summary>
public sealed class GalleryTile
{
    private static readonly Dictionary<string, BitmapImage> ThumbCache = new();

    public GalleryTile(GalleryItem item)
    {
        Item = item;
        // 视频条目优先用卡片封面图当缩略图；直接拿视频直链去解码会失败
        var thumbUrl = item.ThumbUrl ?? (!item.IsVideo ? item.Url : null);
        if (thumbUrl is not null && Uri.TryCreate(thumbUrl, UriKind.Absolute, out var uri))
        {
            if (!ThumbCache.TryGetValue(thumbUrl, out var bitmap))
            {
                bitmap = new BitmapImage(uri);
                ThumbCache[thumbUrl] = bitmap;
            }

            Thumb = bitmap;
        }

        VideoBadgeVis = item.IsVideo ? Visibility.Visible : Visibility.Collapsed;
    }

    public GalleryItem Item { get; }

    public ImageSource? Thumb { get; }

    public Visibility VideoBadgeVis { get; }
}
