using System;
using System.IO;
using System.Windows.Interop;
using System.Windows.Threading;
using Clipora.Abstractions;
using Clipora.Interop;
using Clipora.Models;

namespace Clipora.Services;

/// <summary>
/// <see cref="IClipboardMonitor"/> 实现：用隐藏消息窗接收 <c>WM_CLIPBOARDUPDATE</c>，
/// 经分类器生成记录后入库，并通知 UI。含防自捕获、暂停、大小上限处理。
/// </summary>
public sealed class ClipboardMonitorService : IClipboardMonitor
{
    private readonly IClipStore _store;
    private readonly IContentClassifier _classifier;
    private readonly ISettingsService _settings;
    private readonly ISourceResolver _sourceResolver;

    private HwndSource? _source;
    private DispatcherTimer? _privacyRetryTimer;
    private readonly ClipboardPrivacyRetryState _privacyRetryState = new();
    public event EventHandler<ClipItem>? ClipCaptured;
    public event EventHandler<long>? ItemOverSized;

    public ClipboardMonitorService(IClipStore store, IContentClassifier classifier, ISettingsService settings, ISourceResolver sourceResolver)
    {
        _store = store;
        _classifier = classifier;
        _settings = settings;
        _sourceResolver = sourceResolver;
    }

    public void Start()
    {
        if (_source is not null)
            return;

        var parameters = new HwndSourceParameters("CliporaClipboardListener")
        {
            Width = 0,
            Height = 0,
            WindowStyle = 0,
        };
        _source = new HwndSource(parameters);
        _source.AddHook(WndProc);
        NativeMethods.AddClipboardFormatListener(_source.Handle);
    }

    public void Stop()
    {
        CancelPendingPrivacyRetry();
        if (_source is null)
            return;

        NativeMethods.RemoveClipboardFormatListener(_source.Handle);
        _source.RemoveHook(WndProc);
        _source.Dispose();
        _source = null;
    }

    public bool Import(System.Windows.IDataObject dataObject)
    {
        ArgumentNullException.ThrowIfNull(dataObject);

        ClipItem? item;
        try
        {
            item = _classifier.Classify(dataObject);
        }
        catch
        {
            return false;
        }

        return StoreAndNotify(item);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_CLIPBOARDUPDATE)
        {
            OnClipboardUpdate();
            handled = true;
        }
        return IntPtr.Zero;
    }

    private void OnClipboardUpdate()
    {
        CancelPendingPrivacyRetry();

        // 暂停是第一道判断：暂停时不读取剪贴板标记、来源进程或系统排除格式。
        if (_settings.Current.Paused)
            return;

        uint sequence = NativeMethods.GetClipboardSequenceNumber();
        EvaluateAndCapture(sequence, isRetry: false);
    }

    private void EvaluateAndCapture(uint sequence, bool isRetry)
    {
        if (_source is null || _settings.Current.Paused)
            return;

        // Clipora 主动写回的数据可能触发不止一条更新消息；持久数据标记比一次性布尔可靠。
        if (ClipboardInternalWriteMarker.IsPresentOnClipboard())
            return;

        IReadOnlyCollection<string> excludedApps = _settings.Current.ExcludedApps;
        string? foregroundProcessName = null;
        if (excludedApps.Count > 0)
        {
            try { foregroundProcessName = _sourceResolver.GetForegroundProcessName(); }
            catch { foregroundProcessName = null; }
        }

        ClipboardExclusionState systemExclusion = ReadSystemExclusion();
        PrivacyCaptureDecision decision = PrivacyCapturePolicy.Decide(
            paused: false,
            foregroundProcessName,
            excludedApps,
            systemExclusion,
            isRetry);

        if (decision == PrivacyCaptureDecision.Retry)
        {
            SchedulePrivacyRetry(sequence);
            return;
        }
        if (decision == PrivacyCaptureDecision.Skip)
        {
            return;
        }

        ClipItem? item;
        try
        {
            item = _classifier.Classify();
        }
        catch
        {
            return; // 剪贴板被其他程序占用等情况，跳过本次。
        }

        StoreAndNotify(item);
    }

    private void SchedulePrivacyRetry(uint sequence)
    {
        CancelPendingPrivacyRetry();
        if (_source is null)
            return;

        _privacyRetryState.Schedule(sequence);
        _privacyRetryTimer = new DispatcherTimer(DispatcherPriority.Background, _source.Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(50),
        };
        _privacyRetryTimer.Tick += OnPrivacyRetryTick;
        _privacyRetryTimer.Start();
    }

    private void OnPrivacyRetryTick(object? sender, EventArgs e)
    {
        DispatcherTimer? timer = _privacyRetryTimer;
        if (timer is not null)
        {
            timer.Stop();
            timer.Tick -= OnPrivacyRetryTick;
        }
        _privacyRetryTimer = null;

        if (_source is null)
        {
            _privacyRetryState.Cancel();
            return;
        }

        uint currentSequence = NativeMethods.GetClipboardSequenceNumber();
        if (!_privacyRetryState.TryConsume(currentSequence))
            return;
        EvaluateAndCapture(currentSequence, isRetry: true);
    }

    private void CancelPendingPrivacyRetry()
    {
        if (_privacyRetryTimer is not null)
        {
            _privacyRetryTimer.Stop();
            _privacyRetryTimer.Tick -= OnPrivacyRetryTick;
            _privacyRetryTimer = null;
        }
        _privacyRetryState.Cancel();
    }

    private bool StoreAndNotify(ClipItem? item)
    {
        if (item is null)
            return false;

        bool isFileReferenceAtLimit = item.Type == ClipType.File && item.SizeBytes >= _settings.Current.MaxItemBytes;
        bool isOverLimit = item.SizeBytes > _settings.Current.MaxItemBytes || isFileReferenceAtLimit;
        if (isOverLimit)
        {
            ItemOverSized?.Invoke(this, item.SizeBytes);
            if (item.Type != ClipType.File)
                return false;
        }

        item.Id = _store.Add(item, _settings.Current.MergeDuplicates);
        ClipCaptured?.Invoke(this, item);
        return true;
    }

    public void Dispose() => Stop();

    /// <summary>
    /// 读取系统剪贴板排除标记（4.4.2b）。命中任一即排除：
    /// <c>ExcludeClipboardContentFromMonitorProcessing</c> 存在；或 <c>CanIncludeInClipboardHistory</c> 存在且 DWORD 值为 0。
    /// 异常或存在但无法解析时返回 Unknown，由调用方只重试一次后 fail-closed。
    /// </summary>
    private static ClipboardExclusionState ReadSystemExclusion()
    {
        try
        {
            bool hasExcludeFormat = System.Windows.Clipboard.ContainsData("ExcludeClipboardContentFromMonitorProcessing");
            if (hasExcludeFormat)
                return ClipboardExclusionState.Excluded;

            bool hasHistoryFlag = System.Windows.Clipboard.ContainsData("CanIncludeInClipboardHistory");
            if (!hasHistoryFlag)
                return ClipboardExclusionState.Allowed;

            int historyValue = 0;
            bool historyValueKnown = false;

            try
            {
                var data = System.Windows.Clipboard.GetData("CanIncludeInClipboardHistory");
                if (data is int i)
                {
                    historyValue = i;
                    historyValueKnown = true;
                }
                else if (data is uint u && u <= int.MaxValue)
                {
                    historyValue = (int)u;
                    historyValueKnown = true;
                }
                else if (data is MemoryStream ms)
                {
                    byte[] bytes = ms.ToArray();
                    if (bytes.Length >= 4)
                    {
                        historyValue = BitConverter.ToInt32(bytes, 0);
                        historyValueKnown = true;
                    }
                }
            }
            catch { }

            return ClipboardExclusionMarker.Evaluate(
                hasExcludeFormat,
                hasHistoryFlag,
                historyValue,
                historyValueKnown);
        }
        catch
        {
            return ClipboardExclusionState.Unknown;
        }
    }
}
