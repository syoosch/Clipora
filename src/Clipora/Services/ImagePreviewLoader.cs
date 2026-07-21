using System.IO;
using System.Windows.Media.Imaging;
using Clipora.Abstractions;

namespace Clipora.Services;

/// <summary>
/// 在单个长生命周期 STA 工作线程上串行解码预览图片。队列只保留最新等待项，避免快速划过时
/// 为已经失效的请求继续创建线程或并行解码完整图片。
/// </summary>
public sealed class ImagePreviewLoader : IImagePreviewLoader
{
    private const long MaximumCachedBytes = 8L * 1024 * 1024;

    private readonly object _gate = new();
    private readonly Thread _worker;
    private readonly Func<string, int, int, CancellationToken, BitmapSource?> _decoder;
    private PreviewRequest? _pending;
    private PreviewCacheEntry? _cache;
    private bool _disposed;

    public ImagePreviewLoader()
        : this(DecodeImage)
    {
    }

    internal ImagePreviewLoader(
        Func<string, int, int, CancellationToken, BitmapSource?> decoder)
    {
        _decoder = decoder ?? throw new ArgumentNullException(nameof(decoder));
        _worker = new Thread(WorkerLoop)
        {
            IsBackground = true,
            Name = "Clipora.ImagePreviewLoader",
        };
        _worker.SetApartmentState(ApartmentState.STA);
        _worker.Start();
    }

    public async Task<BitmapSource?> LoadAsync(
        string refPath,
        int maxPixelWidth,
        int maxPixelHeight,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(refPath)
            || maxPixelWidth <= 0
            || maxPixelHeight <= 0
            || cancellationToken.IsCancellationRequested)
        {
            return null;
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(refPath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        // 图片 RefPath 应始终位于本地磁盘。拒绝 UNC 和映射网络盘，避免损坏数据把预览变成网络访问；
        // 本地扩展路径 \\?\C:\... 仍允许。
        if (IsNetworkOrInvalidPath(fullPath))
            return null;

        var request = new PreviewRequest(fullPath, maxPixelWidth, maxPixelHeight, cancellationToken);
        lock (_gate)
        {
            if (_disposed)
                return null;

            _pending?.TryComplete(null);
            _pending = request;
            Monitor.Pulse(_gate);
        }

        using CancellationTokenRegistration registration = cancellationToken.Register(
            static state => ((PreviewRequest)state!).TryComplete(null),
            request);
        return await request.Task.ConfigureAwait(false);
    }

    public void Dispose()
    {
        PreviewRequest? pending;
        lock (_gate)
        {
            if (_disposed)
                return;

            _disposed = true;
            pending = _pending;
            _pending = null;
            _cache = null;
            Monitor.PulseAll(_gate);
        }

        pending?.TryComplete(null);
    }

    private void WorkerLoop()
    {
        while (true)
        {
            PreviewRequest? request;
            lock (_gate)
            {
                while (!_disposed && _pending is null)
                    Monitor.Wait(_gate);

                if (_disposed && _pending is null)
                    return;

                request = _pending;
                _pending = null;
            }

            if (request is null || request.IsCompleted || request.CancellationToken.IsCancellationRequested)
            {
                request?.TryComplete(null);
                continue;
            }

            BitmapSource? result = TryGetCached(request);
            if (result is null)
            {
                try
                {
                    result = _decoder(
                        request.Path,
                        request.MaxPixelWidth,
                        request.MaxPixelHeight,
                        request.CancellationToken);
                }
                catch
                {
                    result = null;
                }

                if (result is not null
                    && !request.CancellationToken.IsCancellationRequested
                    && !request.IsCompleted)
                {
                    StoreCache(request, result);
                }
            }

            if (request.CancellationToken.IsCancellationRequested || request.IsCompleted)
                request.TryComplete(null);
            else
                request.TryComplete(result);
        }
    }

    private BitmapSource? TryGetCached(PreviewRequest request)
    {
        PreviewCacheEntry? cache;
        lock (_gate)
            cache = _cache;

        if (cache is null
            || cache.MaxPixelWidth != request.MaxPixelWidth
            || cache.MaxPixelHeight != request.MaxPixelHeight
            || !string.Equals(cache.Path, request.Path, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (!TryGetFileIdentity(request.Path, out long length, out long lastWriteTicks)
            || length != cache.FileLength
            || lastWriteTicks != cache.LastWriteUtcTicks)
        {
            return null;
        }

        return cache.Bitmap;
    }

    private void StoreCache(PreviewRequest request, BitmapSource bitmap)
    {
        long bytesPerPixel = Math.Max(1, (bitmap.Format.BitsPerPixel + 7L) / 8L);
        long estimatedBytes;
        try
        {
            estimatedBytes = checked((long)bitmap.PixelWidth * bitmap.PixelHeight * bytesPerPixel);
        }
        catch (OverflowException)
        {
            return;
        }

        if (estimatedBytes > MaximumCachedBytes
            || !TryGetFileIdentity(request.Path, out long length, out long lastWriteTicks))
        {
            return;
        }

        lock (_gate)
        {
            if (_disposed)
                return;

            _cache = new PreviewCacheEntry(
                request.Path,
                request.MaxPixelWidth,
                request.MaxPixelHeight,
                length,
                lastWriteTicks,
                bitmap);
        }
    }

    private static BitmapSource? DecodeImage(
        string path,
        int maxPixelWidth,
        int maxPixelHeight,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested || !File.Exists(path))
            return null;

        int sourceWidth;
        int sourceHeight;
        try
        {
            using FileStream metadataStream = new(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            BitmapDecoder decoder = BitmapDecoder.Create(
                metadataStream,
                BitmapCreateOptions.DelayCreation | BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.None);
            if (decoder.Frames.Count == 0)
                return null;

            sourceWidth = decoder.Frames[0].PixelWidth;
            sourceHeight = decoder.Frames[0].PixelHeight;
        }
        catch
        {
            return null;
        }

        if (sourceWidth <= 0 || sourceHeight <= 0 || cancellationToken.IsCancellationRequested)
            return null;

        double decodeScale = Math.Min(
            1.0,
            Math.Min((double)maxPixelWidth / sourceWidth, (double)maxPixelHeight / sourceHeight));

        try
        {
            using FileStream imageStream = new(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
            bitmap.StreamSource = imageStream;
            if (decodeScale < 1.0)
            {
                double widthRatio = (double)sourceWidth / maxPixelWidth;
                double heightRatio = (double)sourceHeight / maxPixelHeight;
                if (widthRatio >= heightRatio)
                    bitmap.DecodePixelWidth = Math.Max(1, (int)Math.Floor(sourceWidth * decodeScale));
                else
                    bitmap.DecodePixelHeight = Math.Max(1, (int)Math.Floor(sourceHeight * decodeScale));
            }

            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    private static bool TryGetFileIdentity(string path, out long length, out long lastWriteUtcTicks)
    {
        length = 0;
        lastWriteUtcTicks = 0;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
                return false;

            length = info.Length;
            lastWriteUtcTicks = info.LastWriteTimeUtc.Ticks;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsNetworkOrInvalidPath(string fullPath)
    {
        try
        {
            if (fullPath.StartsWith(@"\\", StringComparison.Ordinal))
            {
                bool isExtendedLocal = fullPath.StartsWith(@"\\?\", StringComparison.Ordinal)
                    && !fullPath.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase);
                if (!isExtendedLocal)
                    return true;
            }

            string? root = Path.GetPathRoot(fullPath);
            return string.IsNullOrWhiteSpace(root)
                || new DriveInfo(root).DriveType == DriveType.Network;
        }
        catch
        {
            return true;
        }
    }

    private sealed class PreviewRequest
    {
        private readonly TaskCompletionSource<BitmapSource?> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public PreviewRequest(
            string path,
            int maxPixelWidth,
            int maxPixelHeight,
            CancellationToken cancellationToken)
        {
            Path = path;
            MaxPixelWidth = maxPixelWidth;
            MaxPixelHeight = maxPixelHeight;
            CancellationToken = cancellationToken;
        }

        public string Path { get; }
        public int MaxPixelWidth { get; }
        public int MaxPixelHeight { get; }
        public CancellationToken CancellationToken { get; }
        public Task<BitmapSource?> Task => _completion.Task;
        public bool IsCompleted => _completion.Task.IsCompleted;

        public void TryComplete(BitmapSource? bitmap) => _completion.TrySetResult(bitmap);
    }

    private sealed record PreviewCacheEntry(
        string Path,
        int MaxPixelWidth,
        int MaxPixelHeight,
        long FileLength,
        long LastWriteUtcTicks,
        BitmapSource Bitmap);
}
