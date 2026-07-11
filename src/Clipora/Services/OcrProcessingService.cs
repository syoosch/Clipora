using System.IO;
using System.Threading.Channels;
using Clipora.Abstractions;
using Clipora.Models;

namespace Clipora.Services;

/// <summary>
/// 后台串行 OCR 处理服务（M5.1）。
/// 新图片捕获后入队识别；首启回填历史图片。
/// 串行单张处理，不阻塞捕获与 UI。
/// </summary>
public sealed class OcrProcessingService : IDisposable
{
    private readonly IOcrService _ocr;
    private readonly IClipStore _store;
    private readonly ISettingsService _settings;
    private readonly Channel<long> _channel;
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;
    private bool _disposed;

    /// <summary>一次取多少待处理项。</summary>
    private const int BatchSize = 1; // 串行单张

    public OcrProcessingService(IOcrService ocr, IClipStore store, ISettingsService settings)
    {
        _ocr = ocr ?? throw new ArgumentNullException(nameof(ocr));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _channel = Channel.CreateBounded<long>(new BoundedChannelOptions(100)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });
    }

    /// <summary>启动后台处理循环（不阻塞）。</summary>
    public void Start()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(OcrProcessingService));

        if (!_ocr.IsAvailable)
            return; // 引擎不可用，整体 no-op

        bool ocrEnabled = _settings.Current.OcrEnabled;
        if (!ocrEnabled)
            return; // 用户关闭 OCR，不做任何事

        // 启动后台循环
        _loop = Task.Run(() => RunLoopAsync(_cts.Token));

        // 首启回填历史图片
        if (!_settings.Current.OcrBackfillCompleted)
        {
            _store.MarkLegacyImagesPending();
            _settings.Current.OcrBackfillCompleted = true;
            _settings.Save();
        }
    }

    /// <summary>新图片捕获后入队（非阻塞）。</summary>
    public bool TryEnqueue(long clipItemId)
    {
        if (_disposed || _settings?.Current.OcrEnabled != true || !_ocr.IsAvailable)
            return false;

        return _channel.Writer.TryWrite(clipItemId);
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            long clipId;
            try
            {
                clipId = await _channel.Reader.ReadAsync(ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ChannelClosedException)
            {
                break;
            }

            await ProcessOneAsync(clipId, ct);
        }

        // Channel 关闭后：排空剩余
        while (_channel.Reader.TryRead(out long remaining))
        {
            if (ct.IsCancellationRequested)
                break;
            await ProcessOneAsync(remaining, ct);
        }
    }

    private async Task ProcessOneAsync(long clipId, CancellationToken ct)
    {
        try
        {
            // 检查是否仍开启
            if (_settings.Current.OcrEnabled != true || !_ocr.IsAvailable)
                return;

            ClipItem? item = _store.GetById(clipId);
            if (item is null || item.OcrStatus != OcrStatus.Pending)
                return; // 已处理或非待处理状态

            // 图片必须存在
            string imagePath = item.RefPath ?? string.Empty;
            if (string.IsNullOrEmpty(imagePath) || !File.Exists(imagePath))
            {
                _store.SetOcrResult(clipId, OcrOutcome.Failed, null);
                return;
            }

            // 执行 OCR（含 CancellationToken 传递）
            Abstractions.OcrResult result = await _ocr.RecognizeAsync(imagePath, ct);

            // 回写结果
            _store.SetOcrResult(clipId, result.Outcome,
                result.Outcome == OcrOutcome.Recognized ? result.Text : null);

            // 队列中下一张之间轻微延时（友好空闲）
            if (!ct.IsCancellationRequested)
                await Task.Delay(100, ct);
        }
        catch (OperationCanceledException)
        {
            // 取消，不回写结果（下次仍可重试 Pending）
        }
        catch (Exception)
        {
            // 异常不回写（保留 Pending 供下次重试）
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        _cts.Cancel();
        _channel.Writer.TryComplete();
        _cts.Dispose();
    }
}
