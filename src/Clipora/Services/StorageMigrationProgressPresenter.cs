using System;
using Clipora.Models;

namespace Clipora.Services;

/// <summary>迁移进度展示模型：纯数据，不引用 WPF/Dispatcher/ViewModel。</summary>
internal sealed record StorageMigrationProgressPresentation(
    string StageText,
    bool IsDeterminate,
    double Fraction);

/// <summary>
/// 纯阶段映射器：把引擎 <see cref="StorageMigrationProgress"/> 转成固定展示文案。
/// 不引用 WPF、Dispatcher 或 ViewModel；可独立单元测试。
/// </summary>
internal static class StorageMigrationProgressPresenter
{
    /// <summary>
    /// 固定阶段→文案映射。只有 Copying 且存在正数总量时为 determinate。
    /// 进度按 bytes 优先，TotalBytes&lt;=0 时回退 files；Fraction clamp [0,1]。
    /// </summary>
    public static StorageMigrationProgressPresentation Map(StorageMigrationProgress progress)
    {
        string stageText = progress.Phase switch
        {
            StorageMigrationPhase.Validating or StorageMigrationPhase.Checkpointing => "正在准备",
            StorageMigrationPhase.Copying => "正在复制",
            StorageMigrationPhase.Rebasing or StorageMigrationPhase.Verifying => "正在校验",
            StorageMigrationPhase.Promoting or StorageMigrationPhase.Switching or StorageMigrationPhase.Completed => "正在切换",
            StorageMigrationPhase.Failed => "迁移失败",
            _ => "正在处理",
        };

        // 只有 Copying 且存在正数总量（bytes 或 files）时才是 determinate
        bool isDeterminate = progress.Phase == StorageMigrationPhase.Copying
            && (progress.TotalBytes > 0 || progress.TotalFiles > 0);
        double fraction = 0;

        if (isDeterminate)
        {
            // 优先按 bytes；TotalBytes<=0 时回退 files
            if (progress.TotalBytes > 0)
            {
                fraction = (double)progress.CompletedBytes / progress.TotalBytes;
            }
            else
            {
                fraction = (double)progress.CompletedFiles / progress.TotalFiles;
            }

            // Clamp [0,1]；NaN/Infinity → 0
            if (double.IsNaN(fraction) || double.IsInfinity(fraction) || fraction < 0)
                fraction = 0;
            else if (fraction > 1)
                fraction = 1;
        }

        return new StorageMigrationProgressPresentation(stageText, isDeterminate, fraction);
    }
}
