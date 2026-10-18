using System.Windows.Media.Imaging;

namespace Clipora.Services;

/// <summary>悬浮卡片的独立加载通道；不占用悬停预览的“只保留最新请求”队列。</summary>
internal static class FloatingCardImageLoader
{
    // 每张图限制解码边长，多个小窗串行加载，避免高分辨率截图同时解码造成内存尖峰。
    private static readonly SemaphoreSlim DecodeGate = new(1, 1);
    internal const int MaximumEdge = 4096;

    public static async Task<BitmapSource?> LoadAsync(string path, CancellationToken cancellationToken)
    {
        await DecodeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var loader = new ImagePreviewLoader();
            // WIC 正在执行的解码不可中断；等其完成后再释放 gate，关闭窗口只取消发布结果。
            BitmapSource? result = await loader.LoadAsync(path, MaximumEdge, MaximumEdge).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return result;
        }
        finally
        {
            DecodeGate.Release();
        }
    }
}
