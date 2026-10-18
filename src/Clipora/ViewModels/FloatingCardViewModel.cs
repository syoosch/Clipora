using System.Windows.Media.Imaging;
using Clipora.Common;
using Clipora.Services;

namespace Clipora.ViewModels;

/// <summary>本次运行中的图片快照；不持有数据库条目、不写剪贴板、不保存窗口会话。</summary>
public sealed class FloatingCardViewModel : ObservableObject, IDisposable
{
    private readonly string? _imagePath;
    private readonly CancellationTokenSource _lifetime = new();
    private Task? _loadTask;
    private BitmapSource? _image;
    private string _message = "正在载入图片…";
    private bool _disposed;

    public FloatingCardViewModel(ClipItemViewModel item)
    {
        ItemId = item.Id;
        _imagePath = item.PreviewImagePath;
    }

    public long ItemId { get; }
    public BitmapSource? Image
    {
        get => _image;
        private set => Set(ref _image, value);
    }
    public string Message
    {
        get => _message;
        private set { if (Set(ref _message, value)) OnPropertyChanged(nameof(HasMessage)); }
    }
    public bool HasMessage => Message.Length > 0;

    public Task LoadAsync() => _loadTask ??= LoadImageAsync();

    private async Task LoadImageAsync()
    {
        if (_disposed) return;
        try
        {
            BitmapSource? image = string.IsNullOrWhiteSpace(_imagePath) ? null
                : await FloatingCardImageLoader.LoadAsync(_imagePath, _lifetime.Token);
            if (_disposed) return;
            Image = image;
            Message = image is null ? "图片无法载入，文件可能已被移除或损坏" : string.Empty;
        }
        catch (OperationCanceledException) { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        _lifetime.Dispose();
        Image = null;
    }
}
