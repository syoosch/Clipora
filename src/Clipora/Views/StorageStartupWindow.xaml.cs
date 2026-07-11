using System;
using System.ComponentModel;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Clipora.Models;
using Clipora.Services;
using Wpf.Ui.Controls;

namespace Clipora.Views;

internal enum StorageStartupDecision
{
    Continue,
    Retry,
    UseDefault,
    Exit,
}

internal static class StorageStartupWindowPolicy
{
    public static bool CanUseDefault(StorageLocationException exception) =>
        exception.ErrorCode == StorageLocationError.MissingDirectory
        && !string.IsNullOrWhiteSpace(exception.PathValue);

    public static bool ShouldContinue(StorageMigrationStartupResult result) =>
        result.Action is StorageMigrationStartupAction.None or StorageMigrationStartupAction.Completed;

    public static bool ShouldForceShowMain(
        bool isCompletionLaunch,
        StorageMigrationStartupResult? result) =>
        isCompletionLaunch || result?.Action == StorageMigrationStartupAction.Completed;
}

/// <summary>数据库打开前使用的临时 Fluent 启动窗口。</summary>
public partial class StorageStartupWindow : FluentWindow
{
    private Func<IProgress<StorageMigrationProgress>, StorageMigrationStartupResult>? _migrationWork;
    private bool _migrationRunning;
    private bool _allowClose;
    private StorageMigrationStartupResult? _migrationResult;
    private StorageStartupDecision _decision = StorageStartupDecision.Exit;

    private StorageStartupWindow()
    {
        InitializeComponent();
    }

    internal static StorageMigrationStartupResult? RunMigration(
        Func<IProgress<StorageMigrationProgress>, StorageMigrationStartupResult> work)
    {
        var window = new StorageStartupWindow
        {
            _migrationWork = work ?? throw new ArgumentNullException(nameof(work)),
        };
        window.ConfigureProgress();
        window.Loaded += (_, _) => window.StartMigration();
        window.ShowDialog();
        return window._decision == StorageStartupDecision.Exit ? null : window._migrationResult;
    }

    internal static StorageStartupDecision ShowLocationFailure(
        StorageLocationException exception,
        string? additionalDetail = null)
    {
        var window = new StorageStartupWindow();
        window.ConfigureLocationFailure(exception, additionalDetail);
        window.ShowDialog();
        return window._decision;
    }

    private void ConfigureProgress()
    {
        _decision = StorageStartupDecision.Exit;
        _allowClose = false;
        HeadingText.Text = "正在迁移数据";
        SubheadingText.Text = "Clipora 会在完整校验后切换目录，请勿关闭电脑。";
        ProgressPanel.Visibility = Visibility.Visible;
        FailurePanel.Visibility = Visibility.Collapsed;
        ActionPanel.Visibility = Visibility.Collapsed;
        StageText.Text = "正在准备";
        ProgressRing.Visibility = Visibility.Visible;
        CopyProgressBar.Visibility = Visibility.Collapsed;
    }

    private void ConfigureMigrationFailure(StorageMigrationStartupResult result)
    {
        _migrationRunning = false;
        _allowClose = true;
        HeadingText.Text = "数据迁移未完成";
        SubheadingText.Text = "Clipora 将继续使用当前活动数据目录。";
        ProgressPanel.Visibility = Visibility.Collapsed;
        FailurePanel.Visibility = Visibility.Visible;
        ActionPanel.Visibility = Visibility.Visible;
        UseDefaultButton.Visibility = Visibility.Collapsed;
        RetryButton.Visibility = Visibility.Visible;
        FailureText.Text = string.IsNullOrWhiteSpace(result.Detail)
            ? $"迁移失败（{result.Error}）。"
            : result.Detail;
        FailurePathText.Text = string.IsNullOrWhiteSpace(result.ActiveRoot)
            ? string.Empty
            : $"当前目录：{result.ActiveRoot}";
        FailurePathText.ToolTip = result.ActiveRoot;
        FailureHintText.Text = "可以重试；若暂时不处理，请退出 Clipora。不会删除原目录。";
    }

    private void ConfigureLocationFailure(StorageLocationException exception, string? additionalDetail)
    {
        _allowClose = true;
        HeadingText.Text = "数据目录不可用";
        SubheadingText.Text = "Clipora 已阻止启动，以避免打开错误的数据目录。";
        ProgressPanel.Visibility = Visibility.Collapsed;
        FailurePanel.Visibility = Visibility.Visible;
        ActionPanel.Visibility = Visibility.Visible;
        FailureText.Text = string.IsNullOrWhiteSpace(additionalDetail)
            ? exception.Message
            : additionalDetail;
        FailurePathText.Text = string.IsNullOrWhiteSpace(exception.PathValue)
            ? string.Empty
            : $"配置目录：{exception.PathValue}";
        FailurePathText.ToolTip = exception.PathValue;
        FailureHintText.Text = exception.ErrorCode == StorageLocationError.MissingDirectory
            ? "使用默认位置不会删除旧目录或其中的数据。也可以恢复原目录后重试。"
            : "请修复目录或注册表访问问题后重试。";
        UseDefaultButton.Visibility = StorageStartupWindowPolicy.CanUseDefault(exception)
                ? Visibility.Visible
                : Visibility.Collapsed;
        RetryButton.Visibility = Visibility.Visible;
    }

    private void StartMigration()
    {
        if (_migrationRunning || _migrationWork is null)
            return;

        ConfigureProgress();
        _migrationRunning = true;
        var progress = new CallbackProgress<StorageMigrationProgress>(value =>
        {
            _ = Dispatcher.BeginInvoke(
                DispatcherPriority.Background,
                new Action(() => ApplyProgress(value)));
        });

        _ = Task.Run(() => _migrationWork(progress)).ContinueWith(task =>
        {
            _ = Dispatcher.BeginInvoke(new Action(() => CompleteMigration(task)));
        }, TaskScheduler.Default);
    }

    private void ApplyProgress(StorageMigrationProgress progress)
    {
        if (!_migrationRunning)
            return;

        StorageMigrationProgressPresentation presentation = StorageMigrationProgressPresenter.Map(progress);
        StageText.Text = presentation.StageText;
        ProgressRing.Visibility = presentation.IsDeterminate ? Visibility.Collapsed : Visibility.Visible;
        CopyProgressBar.Visibility = presentation.IsDeterminate ? Visibility.Visible : Visibility.Collapsed;
        CopyProgressBar.Value = presentation.Fraction;
    }

    private void CompleteMigration(Task<StorageMigrationStartupResult> task)
    {
        _migrationRunning = false;
        StorageMigrationStartupResult result;
        if (task.Status == TaskStatus.RanToCompletion)
        {
            result = task.Result;
        }
        else
        {
            string detail = task.Exception?.GetBaseException().Message ?? "未知启动迁移错误。";
            result = new StorageMigrationStartupResult(
                StorageMigrationStartupAction.Failed,
                string.Empty,
                StorageMigrationError.Unknown,
                detail);
        }

        _migrationResult = result;
        if (!StorageStartupWindowPolicy.ShouldContinue(result))
        {
            ConfigureMigrationFailure(result);
            return;
        }

        _decision = StorageStartupDecision.Continue;
        _allowClose = true;
        Close();
    }

    private void Retry_Click(object sender, RoutedEventArgs e)
    {
        if (_migrationWork is not null)
        {
            StartMigration();
            return;
        }

        _decision = StorageStartupDecision.Retry;
        _allowClose = true;
        Close();
    }

    private void UseDefault_Click(object sender, RoutedEventArgs e)
    {
        _decision = StorageStartupDecision.UseDefault;
        _allowClose = true;
        Close();
    }

    private void Exit_Click(object sender, RoutedEventArgs e)
    {
        _decision = StorageStartupDecision.Exit;
        _allowClose = true;
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_allowClose || _migrationRunning)
        {
            e.Cancel = true;
            return;
        }

        base.OnClosing(e);
    }

    private sealed class CallbackProgress<T> : IProgress<T>
    {
        private readonly Action<T> _callback;
        public CallbackProgress(Action<T> callback) => _callback = callback;
        public void Report(T value) => _callback(value);
    }
}
