using System;
using System.Collections.Generic;
using System.IO;
using Clipora.Abstractions;
using Clipora.Models;

namespace Clipora.Services;

/// <summary>内存 state store：供自检使用；生产 registry 实现留到 M4.2.3a。</summary>
internal sealed class MemoryStorageMigrationStateStore : IStorageMigrationStateStore
{
    private StorageMigrationState _state;

    public MemoryStorageMigrationStateStore(StorageMigrationState initialState)
    {
        _state = initialState;
    }

    public StorageMigrationState Read() => _state;

    public void ClearPending(Guid migrationId)
    {
        if (_state.MigrationId != migrationId)
            throw new InvalidOperationException(
                $"MigrationId mismatch: expected {_state.MigrationId}, got {migrationId}");
        _state = _state with { PendingRoot = null, MigrationId = null };
    }

    public void CommitTarget(string targetRoot, string sourceRoot, Guid migrationId)
    {
        if (_state.MigrationId != migrationId)
            throw new InvalidOperationException(
                $"MigrationId mismatch: expected {_state.MigrationId}, got {migrationId}");
        _state = new StorageMigrationState(
            ActiveRoot: targetRoot,
            PendingRoot: null,
            MigrationId: null,
            LastSourceRoot: sourceRoot);
    }
}

/// <summary>可配置故障注入器：自检用。</summary>
internal sealed class MemoryStorageMigrationFaultInjector : IStorageMigrationFaultInjector
{
    public StorageMigrationFaultPoint? FailAt { get; set; }
    public string FailMessage { get; set; } = "selftest fault injected";

    public void ThrowIfRequested(StorageMigrationFaultPoint point)
    {
        if (FailAt == point)
            throw new IOException(FailMessage);
    }
}

/// <summary>no-op 故障注入器：生产默认。</summary>
internal sealed class NoOpFaultInjector : IStorageMigrationFaultInjector
{
    public void ThrowIfRequested(StorageMigrationFaultPoint point) { }
}

/// <summary>容量探针抽象 + 默认实现。</summary>
internal interface ISpaceProbe
{
    /// <summary>检查 targetParent 所在卷是否有足够空间容纳 requiredBytes。不足时返回 false。</summary>
    bool HasSufficientSpace(string targetParent, long requiredBytes);
}

internal sealed class DefaultSpaceProbe : ISpaceProbe
{
    public bool HasSufficientSpace(string targetParent, long requiredBytes)
    {
        try
        {
            string full = Path.GetFullPath(targetParent);
            string root = Path.GetPathRoot(full) ?? full;
            var driveInfo = new DriveInfo(root);
            return driveInfo.AvailableFreeSpace >= requiredBytes;
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>可配置容量探针：自检用。</summary>
internal sealed class MemorySpaceProbe : ISpaceProbe
{
    public bool? ResultOverride { get; set; }

    public bool HasSufficientSpace(string targetParent, long requiredBytes)
    {
        if (ResultOverride.HasValue)
            return ResultOverride.Value;
        return new DefaultSpaceProbe().HasSufficientSpace(targetParent, requiredBytes);
    }
}

/// <summary>归属于自己的临时目录的简单列表，用于 selftest finally 清理。</summary>
internal sealed class SelfTestDirectoryTracker
{
    private readonly List<string> _dirs = new();

    /// <summary>当前仍跟踪的目录数（CleanAll() 后应为 0），供自检残留核对使用。</summary>
    public int RemainingCount => _dirs.Count;

    public string Track(string dir) { _dirs.Add(dir); return dir; }

    public void CleanAll()
    {
        foreach (string d in _dirs)
        {
            try { Directory.Delete(d, recursive: true); } catch { /* ignore */ }
        }
        _dirs.Clear();
    }
}
