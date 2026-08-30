namespace MyApp.Models;

/// <summary>一条已结束下载任务的历史索引记录（序列化到本地 history.json）。</summary>
public sealed class HistoryRecord
{
    public Guid Id { get; set; }

    public string Url { get; set; } = "";

    public string Platform { get; set; } = "";

    public string Title { get; set; } = "";

    /// <summary>首个成功产出文件的完整路径；失败任务为空。</summary>
    public string? FilePath { get; set; }

    /// <summary>"已完成" / "失败"，用于列表直读展示。</summary>
    public string StatusText { get; set; } = "";

    public DateTime FinishedAt { get; set; }
}
