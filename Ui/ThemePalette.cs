using Microsoft.UI.Xaml;

namespace MyApp.Ui;

/// <summary>配色方案元数据（设置页色板选择器展示用）。</summary>
public sealed record PaletteOption(string Id, string Name, string AccentHex, string BackgroundHex);

/// <summary>
/// 配色方案（强调色 + 底色搭配）运行时热切换。
/// 每个方案是 Styles/Palettes/ 下的独立资源字典（含 Light/Default 两套主题键），与深浅模式
/// （ThemeHelper）相互独立、可任意组合。
///
/// 切换机制（2026-08-29 重写）：不再替换合并字典里的字典实例——实测实例替换后部分元素
/// （尤其窗口根 Grid 的底色）的 ThemeResource 绑定不会重新求值，表现为"切方案底色不变"。
/// 现在的流程是：
///   1. 启动时定位 App.xaml 合并进来的方案字典，作为常驻工作字典（_installed）；
///   2. Apply 时把目标方案的每个键【原位覆盖】进工作字典（逐键写入走资源变更通知）；
///   3. 再由 ThemeHelper.ForceResourceRefresh 同帧翻转 RequestedTheme 强制全树重算兜底
///      （两次赋值同步完成、中间不产生渲染帧，无可见闪烁）。
/// WarmUp() 预加载五套模板字典进缓存，Apply() 只做内存内覆盖，无磁盘解析延迟；
/// 缓存里的模板字典只读不写，工作字典独立于缓存，反复切换不会互相污染。
/// </summary>
public static class ThemePalette
{
    public static readonly IReadOnlyList<PaletteOption> Presets = new[]
    {
        // BackgroundHex = 各方案浅色底色（与 Palettes/*.xaml 的 ApplicationPageBackgroundThemeBrush 一致）
        new PaletteOption("amber", "琥珀暖纸", "#C96A28", "#F5E9D5"),
        new PaletteOption("rose", "玫瑰暖粉", "#C25069", "#F8E7EB"),
        new PaletteOption("ocean", "青空海洋", "#0F7B8A", "#E4EEF1"),
        new PaletteOption("forest", "森野绿意", "#3E7C4F", "#E9F0DE"),
        new PaletteOption("classic", "经典蓝灰", "#0078D4", "#EFF1F5"),
    };

    public static string DefaultPresetId => Presets[0].Id;

    private static readonly Dictionary<string, ResourceDictionary> Cache = new();

    private static readonly object Sync = new();

    private static bool _warmedUp;

    /// <summary>常驻工作字典：唯一面对可视树的方案字典（App.xaml 合并项或自建项），内容随切换被覆盖。</summary>
    private static ResourceDictionary? _installed;

    private static string? _appliedId;

    /// <summary>启动时把全部方案字典解析进内存缓存（文件很小，开销可忽略）。</summary>
    public static void WarmUp()
    {
        lock (Sync)
        {
            if (_warmedUp)
            {
                return;
            }

            foreach (var preset in Presets)
            {
                GetOrLoad(preset.Id);
            }

            _warmedUp = true;
        }
    }

    /// <summary>切换配色方案；未知 Id 回落默认。原地覆盖键值 + 主题翻转，触发全树 ThemeResource 重算。</summary>
    public static void Apply(string presetId)
    {
        WarmUp();

        if (Presets.All(p => p.Id != presetId))
        {
            presetId = DefaultPresetId;
        }

        EnsureInstalled();

        if (_appliedId == presetId)
        {
            return;
        }

        OverwriteValues(_installed!, GetOrLoad(presetId));
        _appliedId = presetId;

        // 兜底：部分控件对逐键变更也不敏感，同帧翻转主题强制全部 ThemeResource 重新求值
        ThemeHelper.ForceResourceRefresh();
    }

    /// <summary>
    /// 定位常驻工作字典：优先 App.xaml 合并的方案字典（按 Source 路径识别，排在 Common 之后故优先级正确）；
    /// 找不到（极端情况：编译后合并项丢失 Source）则自建一份追加到合并链尾。
    /// </summary>
    private static void EnsureInstalled()
    {
        if (_installed != null)
        {
            return;
        }

        var resources = Application.Current.Resources;
        foreach (var merged in resources.MergedDictionaries)
        {
            if (merged.Source?.OriginalString.Contains("/Styles/Palettes/", StringComparison.OrdinalIgnoreCase) == true)
            {
                _installed = merged;
                return;
            }
        }

        _installed = new ResourceDictionary();
        resources.MergedDictionaries.Add(_installed);
    }

    /// <summary>把 source 的全部键（主题字典逐键 + 平铺键）原位写入 target，触发按键资源变更通知。</summary>
    private static void OverwriteValues(ResourceDictionary target, ResourceDictionary source)
    {
        foreach (var themeKey in source.ThemeDictionaries.Keys.ToList())
        {
            var src = (ResourceDictionary)source.ThemeDictionaries[themeKey]!;
            if (target.ThemeDictionaries.TryGetValue(themeKey, out var existing) && existing is ResourceDictionary dst)
            {
                foreach (var entry in src)
                {
                    dst[entry.Key] = entry.Value;
                }
            }
            else
            {
                // 目标缺该主题键时挂独立副本——绝不能直接引用缓存模板实例，否则后续覆盖会污染缓存
                var copy = new ResourceDictionary();
                foreach (var entry in src)
                {
                    copy[entry.Key] = entry.Value;
                }

                target.ThemeDictionaries[themeKey] = copy;
            }
        }

        foreach (var entry in source)
        {
            target[entry.Key] = entry.Value;
        }
    }

    private static ResourceDictionary GetOrLoad(string presetId)
    {
        lock (Sync)
        {
            if (!Cache.TryGetValue(presetId, out var dict))
            {
                dict = new ResourceDictionary { Source = new Uri($"ms-appx:///Styles/Palettes/{presetId}.xaml") };
                Cache[presetId] = dict;
            }

            return dict;
        }
    }
}
