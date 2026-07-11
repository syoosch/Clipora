using System;
using System.IO;
using System.Windows.Interop;
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
        // Clipora 主动写回的数据可能触发不止一条更新消息；持久数据标记比“一次性布尔抑制”可靠。
        if (ClipboardInternalWriteMarker.IsPresentOnClipboard())
            return;

        // 隐私判定：暂停 / 系统排除标记 / 应用排除名单（纯函数，可测）
        string? foregroundProcessName = _sourceResolver.GetForegroundProcessName();
        bool systemExcluded = ReadSystemExclusion();
        if (!PrivacyCapturePolicy.ShouldCapture(
                paused: _settings.Current.Paused,
                foregroundProcessName: foregroundProcessName,
                excludedApps: _settings.Current.ExcludedApps,
                systemExcluded: systemExcluded))
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
    /// 异常时 fail-open 返回 false（不排除）。
    /// </summary>
    private static bool ReadSystemExclusion()
    {
        try
        {
            bool hasExcludeFormat = System.Windows.Clipboard.ContainsData("ExcludeClipboardContentFromMonitorProcessing");
            bool hasHistoryFlag = System.Windows.Clipboard.ContainsData("CanIncludeInClipboardHistory");
            int historyValue = 1; // 默认"允许包含在历史中"

            if (hasHistoryFlag)
            {
                try
                {
                    var data = System.Windows.Clipboard.GetData("CanIncludeInClipboardHistory");
                    if (data is int i)
                        historyValue = i;
                    else if (data is MemoryStream ms)
                    {
                        byte[] bytes = ms.ToArray();
                        if (bytes.Length >= 4)
                            historyValue = BitConverter.ToInt32(bytes, 0);
                    }
                }
                catch
                {
                    // DWORD 读取失败 → 保留默认值 1（不排除，fail-open）
                }
            }

            return ClipboardExclusionMarker.IsExcluded(hasExcludeFormat, hasHistoryFlag, historyValue);
        }
        catch
        {
            // 剪贴板被占用/异常 → 不排除（fail-open）
            return false;
        }
    }
}
