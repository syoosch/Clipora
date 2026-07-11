using Clipora.Models;

namespace Clipora.Abstractions;

/// <summary>存储迁移纯引擎。输入 source/target/id，输出受控结果；所有错误以 <see cref="StorageMigrationResult"/> 形式返回，不向上裸抛异常。</summary>
public interface IStorageMigrationEngine
{
    StorageMigrationResult Execute(
        StorageMigrationRequest request,
        IProgress<StorageMigrationProgress>? progress = null);
}

/// <summary>迁移状态持久化抽象。e 包自检使用内存实现；生产 registry 实现留到 M4.2.3a。</summary>
public interface IStorageMigrationStateStore
{
    StorageMigrationState Read();
    void ClearPending(Guid migrationId);
    void CommitTarget(string targetRoot, string sourceRoot, Guid migrationId);
}

/// <summary>故障注入抽象。生产默认 no-op；自检使用可配置注入器。</summary>
public interface IStorageMigrationFaultInjector
{
    void ThrowIfRequested(StorageMigrationFaultPoint point);
}
