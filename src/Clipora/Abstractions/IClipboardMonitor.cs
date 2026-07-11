using System;
using System.Windows;
using Clipora.Models;

namespace Clipora.Abstractions;

/// <summary>后台剪贴板监听。捕获到内容后已入库，并通过 <see cref="ClipCaptured"/> 通知 UI。</summary>
public interface IClipboardMonitor : IDisposable
{
    void Start();

    void Stop();

    /// <summary>将用户主动拖入的外部数据分类并写入历史；成功返回 true。</summary>
    bool Import(IDataObject dataObject);

    /// <summary>捕获并入库一条记录（已带 Id）。</summary>
    event EventHandler<ClipItem>? ClipCaptured;

    /// <summary>内容超过单条大小上限、未存入（携带字节数，供 UI 提示）。</summary>
    event EventHandler<long>? ItemOverSized;
}
