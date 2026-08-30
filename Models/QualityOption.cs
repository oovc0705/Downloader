namespace MyApp.Models;

/// <summary>
/// 清晰度选项：Label 为显示名，Selector 传给 yt-dlp 的格式选择器。
/// 用普通可变属性而非 record（XAML 类型信息导出不支持 init-only）。
/// </summary>
public sealed class QualityOption
{
    public QualityOption(string label, string selector)
    {
        Label = label;
        Selector = selector;
    }

    public string Label { get; set; }

    public string Selector { get; set; }
}
