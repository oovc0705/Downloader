using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using MyApp.Services;
using MyApp.Ui;

namespace MyApp.Views;

/// <summary>
/// 设置页：代理、并发数、音频提取、主题、配色方案、剪贴板自动填充。
/// 全部修改即时写回 AppSettings；并发数由启动时创建的信号量决定，重启生效。
/// </summary>
public sealed partial class SettingsPage : Page
{
    private bool _loading = true;

    public SettingsPage()
    {
        InitializeComponent();

        var settings = AppSettings.Instance;

        ProxyToggle.IsOn = settings.ProxyEnabled;
        ProxyUrlBox.Text = settings.ProxyUrl;
        ProxyUrlBox.IsEnabled = settings.ProxyEnabled;
        UpdateProxyHint();

        ConcurrencyBox.Value = Math.Clamp(settings.MaxConcurrency, 1, 4);
        ExtractAudioSwitch.IsOn = settings.ExtractAudio;

        ThemeButtons.SelectedIndex = settings.ThemeMode switch
        {
            "light" => 1,
            "dark" => 2,
            _ => 0,
        };

        PaletteButtons.ItemsSource = ThemePalette.Presets;
        var presetIndex = ThemePalette.Presets.ToList().FindIndex(p => p.Id == settings.ThemePreset);
        PaletteButtons.SelectedIndex = presetIndex >= 0 ? presetIndex : 0;

        ClipboardSwitch.IsOn = settings.AutoFillClipboard;

        _loading = false;
    }

    // ---- 网络 ----

    private void OnProxyToggled(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        AppSettings.Instance.ProxyEnabled = ProxyToggle.IsOn;
        AppSettings.Instance.Save();
        ProxyUrlBox.IsEnabled = ProxyToggle.IsOn;
        UpdateProxyHint();
    }

    private void OnProxyUrlChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        AppSettings.Instance.ProxyUrl = ProxyUrlBox.Text.Trim();
        AppSettings.Instance.Save();
        UpdateProxyHint();
    }

    private void UpdateProxyHint()
    {
        var url = ProxyUrlBox.Text.Trim();
        if (!ProxyToggle.IsOn)
        {
            ProxyHint.Visibility = Visibility.Collapsed;
            return;
        }

        var valid = url.Length > 0 && Uri.TryCreate(url, UriKind.Absolute, out _);
        ProxyHint.Text = url.Length == 0
            ? "请填写代理地址，例如 http://127.0.0.1:7890"
            : valid
                ? "✓ 地址有效，对新发起的下载任务立即生效"
                : "⚠ 地址格式无效，请检查（示例：http://127.0.0.1:7890）";
        ProxyHint.Foreground = (Brush)Application.Current.Resources[
            valid ? "SystemFillColorSuccessBrush" : "SystemFillColorCriticalBrush"];
        ProxyHint.Visibility = Visibility.Visible;
    }

    // ---- 下载 ----

    private void OnConcurrencyChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_loading)
        {
            return;
        }

        AppSettings.Instance.MaxConcurrency = (int)Math.Clamp(Math.Round(sender.Value), 1, 4);
        AppSettings.Instance.Save();
    }

    private void OnExtractAudioToggled(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        AppSettings.Instance.ExtractAudio = ExtractAudioSwitch.IsOn;
        AppSettings.Instance.Save();
    }

    // ---- 外观 ----

    private void OnThemeSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        var mode = ThemeButtons.SelectedIndex switch
        {
            1 => "light",
            2 => "dark",
            _ => "system",
        };

        AppSettings.Instance.ThemeMode = mode;
        AppSettings.Instance.Save();
        ThemeHelper.Apply(mode);
    }

    private void OnPaletteSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || PaletteButtons.SelectedItem is not PaletteOption preset)
        {
            return;
        }

        AppSettings.Instance.ThemePreset = preset.Id;
        AppSettings.Instance.Save();
        ThemePalette.Apply(preset.Id);
    }

    // ---- 常规 ----

    private void OnClipboardToggled(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        AppSettings.Instance.AutoFillClipboard = ClipboardSwitch.IsOn;
        AppSettings.Instance.Save();
    }
}
