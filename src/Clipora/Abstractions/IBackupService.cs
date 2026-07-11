using Clipora.Models;

namespace Clipora.Abstractions;

/// <summary>
/// 备份与恢复服务。
/// 版本化 .clpbak ZIP 归档 = db(VACUUM INTO 一致快照) + 受管 payload + manifest。
/// 导入为合并式、按 ContentHash 去重、纯增量、fail-closed。
/// </summary>
public interface IBackupService
{
    /// <summary>导出当前活动项到 .clpbak 归档。</summary>
    Task<BackupExportResult> ExportAsync(string destFilePath, IProgress<BackupProgress>? progress, CancellationToken ct = default);

    /// <summary>预检归档：校验完整性、版本兼容性、条目数，不写入任何数据。</summary>
    Task<BackupPreview> InspectAsync(string archivePath, CancellationToken ct = default);

    /// <summary>合并式去重导入：绝不删除/覆盖现有条目或文件。</summary>
    Task<BackupImportResult> ImportAsync(string archivePath, IProgress<BackupProgress>? progress, CancellationToken ct = default);
}
