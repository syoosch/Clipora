using System;
using Clipora.Models;

namespace Clipora.Abstractions;

/// <summary>应用设置的读取与持久化（JSON）。实现：<c>SettingsService</c>。</summary>
public interface ISettingsService
{
    AppSettings Current { get; }

    /// <summary>将 <see cref="Current"/> 写入磁盘并触发 <see cref="Changed"/>。</summary>
    void Save();

    event EventHandler? Changed;
}
