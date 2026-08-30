using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace MyApp.Models;

public enum DownloadStatus
{
    Pending,
    Resolving,
    Downloading,
    Processing,
    Completed,
    Failed,
    Cancelled
}

public class DownloadItem : INotifyPropertyChanged
{
    public Guid Id { get; } = Guid.NewGuid();

    /// <summary>入队时确定后不再变化；因需进入 XAML 类型信息导出而用普通可变属性。</summary>
    public string Url { get; set; } = "";

    public string Platform { get; set; } = "";

    private string _title = "等待处理…";

    public string Title
    {
        get => _title;
        set => Set(ref _title, value);
    }

    private DownloadStatus _status = DownloadStatus.Pending;

    public DownloadStatus Status
    {
        get => _status;
        set
        {
            if (Set(ref _status, value))
            {
                OnPropertyChanged(nameof(StatusText));
                OnPropertyChanged(nameof(IsActive));
            }
        }
    }

    public string StatusText
    {
        get
        {
            var text = Status switch
            {
                DownloadStatus.Pending => "排队中",
                DownloadStatus.Resolving => "正在解析链接…",
                DownloadStatus.Downloading => "下载中",
                DownloadStatus.Processing => "合并处理中…",
                DownloadStatus.Completed => "已完成",
                DownloadStatus.Failed => "失败",
                DownloadStatus.Cancelled => "已取消",
                _ => string.Empty
            };
            return QualityLabel.Length == 0 ? text : $"{text} · {QualityLabel}";
        }
    }

    private string _qualityLabel = string.Empty;

    public string QualityLabel
    {
        get => _qualityLabel;
        set
        {
            if (Set(ref _qualityLabel, value))
            {
                OnPropertyChanged(nameof(QualityLabel));
                OnPropertyChanged(nameof(StatusText));
            }
        }
    }

    public List<QualityOption> Qualities { get; } = new();

    private double _progress;

    public double Progress
    {
        get => _progress;
        set => Set(ref _progress, value);
    }

    private string _speed = string.Empty;

    public string Speed
    {
        get => _speed;
        set => Set(ref _speed, value);
    }

    private string _eta = string.Empty;

    public string Eta
    {
        get => _eta;
        set => Set(ref _eta, value);
    }

    private string _error = string.Empty;

    public string Error
    {
        get => _error;
        set => Set(ref _error, value);
    }

    private string? _filePath;

    public string? FilePath
    {
        get => _filePath;
        set => Set(ref _filePath, value);
    }

    public bool IsActive => Status
        is DownloadStatus.Pending
        or DownloadStatus.Resolving
        or DownloadStatus.Downloading
        or DownloadStatus.Processing;

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
