using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Interop;
using System.Windows.Threading;
using Clipora.Abstractions;
using Clipora.Common;
using Clipora.Controls;
using Clipora.Interop;
using Clipora.Models;
using Clipora.Scrolling;
using Clipora.Services;
using Clipora.ViewModels;
using Microsoft.Win32;
using Wpf.Ui.Controls;
using CardButton = System.Windows.Controls.Button;

namespace Clipora.Views;

/// <summary>主面板窗口：Win11 云母外壳 + 跟随系统深浅色 + 历史卡片列表。</summary>
public partial class HistoryWindow : FluentWindow
{
    private static readonly DependencyProperty FilterAnimatedHorizontalOffsetProperty =
        DependencyProperty.Register(
            nameof(FilterAnimatedHorizontalOffset),
            typeof(double),
            typeof(HistoryWindow),
            new PropertyMetadata(0d, OnFilterAnimatedHorizontalOffsetChanged));

    private ScrollViewer? _filterAnimatedScrollViewer;
    private double _filterScrollTarget;
    // 纵向滚轮 + 回到顶部的平滑滚动收口到 SmoothScroller（横向筛选条仍走下方 Storyboard，属 Phase 2）。
    private readonly SmoothScroller _scroller = new(new RenderingFrameClock());
    private readonly ISettingsService _settings;
    private readonly DispatcherTimer _windowPlacementSaveTimer;
    private CardButton? _cardDragSource;
    private Point _cardDragStart;
    private bool _cardDragStarted;
    private bool _awaitingFreshPress;
    private bool _isCardDragInProgress;
    private int _cardMoveHandlerDepth;
    private bool _isExternalDropOverlayShown;
    private int _externalDropOverlayAnimationVersion;
    // 拖入浮层看门狗：拖动越过窗口后若在别处（如桌面回收站）落下，WPF 不保证回投 DragLeave/Drop，
    // 浮层会卡住。DragOver 在拖动悬停期间持续触发，每次都重置该计时器；一旦事件停止供给即判定
    // 拖动已离开/结束，强制收起浮层。
    private DispatcherTimer? _externalDropWatchdog;
    private bool _isSettingsDetailOpen;
    private SettingsViewModel? _settingsViewModel;
    private string? _cachedBackgroundPath; // 上次解码的背景图路径，滑条拖动时避免重复解码
    private IBackupService? _backupService;

    // —— 回到顶部浮动按钮 ——
    // 显隐阈值与滚轮/缓动常量已迁入 BackToTopVisibilityPolicy / ScrollGlide（行为保持）。
    private ScrollViewer? _historyScrollViewer;
    private double _upScrollAccum;
    private double _downScrollAccum;
    private bool _backToTopVisible;
    /// <summary>按钮静止态不透明度（半透明）。</summary>
    private const double BackToTopRestOpacity = 0.92;
    private DispatcherTimer? _overSizedHintTimer;
    private int _overSizedHintAnimationVersion;
    private bool _isChangeDataDirectoryInProgress;

    // 快捷键录入态
    private HotkeyAction? _recordingAction;
    private DispatcherTimer? _recordingTimer;

    // 图片悬停放大预览
    private DispatcherTimer? _imagePreviewTimer;
    private CancellationTokenSource? _imagePreviewCts;
    private FrameworkElement? _previewTarget; // 代替 Popup.PlacementTarget（Placement="Mouse" 不读它）
    private DispatcherTimer? _previewCloseMonitor; // Popup 打开后轮询光标位置，绕过 WS_EX_LAYERED 窗口的鼠标事件丢失

    /// <summary>为 true 时允许真正关闭（退出应用）；否则关闭=隐藏到托盘。</summary>
    public bool AllowClose { get; set; }

    /// <summary>M5.2 备份服务，由 App.xaml.cs 组合根注入。</summary>
    public IBackupService? BackupService
    {
        get => _backupService;
        set => _backupService = value;
    }

    /// <summary>常规设置页 ViewModel，由 App.xaml.cs 组合根注入。</summary>
    public SettingsViewModel? SettingsViewModel
    {
        get => _settingsViewModel;
        set
        {
            if (_settingsViewModel is not null)
                _settingsViewModel.PropertyChanged -= SettingsViewModel_PropertyChanged;

            _settingsViewModel = value;
            if (value is not null)
            {
                SettingsGeneralBody.DataContext = value;
                SettingsStorageBody.DataContext = value;
                SettingsPrivacyBody.DataContext = value;
                SettingsHotkeysBody.DataContext = value;
                SettingsBackupBody.DataContext = value;
                SettingsAppearanceBody.DataContext = value;
                value.PropertyChanged += SettingsViewModel_PropertyChanged;
                ApplyBackgroundImage();
            }
        }
    }

    public event EventHandler? MinimizeRequested;
    public event EventHandler? ExitRequested;

    private double FilterAnimatedHorizontalOffset
    {
        get => (double)GetValue(FilterAnimatedHorizontalOffsetProperty);
        set => SetValue(FilterAnimatedHorizontalOffsetProperty, value);
    }

    public HistoryWindow(ISettingsService settings)
    {
        _settings = settings;
        InitializeComponent();

        System.Version? asmVersion = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = asmVersion is null
            ? "Clipora"
            : $"Clipora v{asmVersion.Major}.{asmVersion.Minor}.{asmVersion.Build}";
        _windowPlacementSaveTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(300),
        };
        _windowPlacementSaveTimer.Tick += (_, _) =>
        {
            _windowPlacementSaveTimer.Stop();
            SaveWindowPlacement();
        };
        _imagePreviewTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(220),
        };
        _imagePreviewTimer.Tick += OnImagePreviewTimerTick;

        _previewCloseMonitor = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(150),
        };
        _previewCloseMonitor.Tick += OnPreviewCloseMonitorTick;

        RestoreWindowPlacement();
        Topmost = settings.Current.AlwaysOnTop;
        ShowInTaskbar = settings.Current.ShowInTaskbar;
        settings.Changed += (_, _) => Dispatcher.Invoke(() =>
        {
            ShowInTaskbar = _settings.Current.ShowInTaskbar;
            if (!_settings.Current.ShowBackToTop)
                HideBackToTop();
        });
        AlwaysOnTopButton.IsChecked = Topmost;
        UpdateAlwaysOnTopToolTip();
        LocationChanged += (_, _) => QueueWindowPlacementSave();
        SizeChanged += (_, _) => QueueWindowPlacementSave();
        PreviewMouseDown += (_, _) => _scroller.CancelAll();
    }

    /// <summary>
    /// 拦截 WM_NCACTIVATE，让窗口失焦时仍以「激活」态渲染，
    /// 避免 DWM 把 Mica / Acrylic 背景材质切到发灰的非激活回退色（用户反馈：失焦后面板发脏）。
    /// 用户 2026-06-23 明确要求对<b>所有外观主题（含 Fluent 及未来主题）</b>统一启用，故无条件挂钩。
    /// </summary>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        IntPtr handle = new WindowInteropHelper(this).Handle;
        HwndSource.FromHwnd(handle)?.AddHook(KeepBackdropActiveHook);
    }

    private static IntPtr KeepBackdropActiveHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != NativeMethods.WM_NCACTIVATE)
            return IntPtr.Zero;

        // 始终以激活态处理（wParam=TRUE）；lParam=-1 抑制非客户区重绘，避免闪烁。
        handled = true;
        return NativeMethods.DefWindowProc(hwnd, msg, new IntPtr(1), new IntPtr(-1));
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        _scroller.CancelAll();
        _windowPlacementSaveTimer.Stop();

        SaveWindowPlacement();
        if (_settingsViewModel is not null)
            _settingsViewModel.PropertyChanged -= SettingsViewModel_PropertyChanged;

        if (AllowClose)
        {
            ExitRequested?.Invoke(this, EventArgs.Empty);
            base.OnClosing(e);
            return;
        }

        e.Cancel = true;
        HandleCloseRequest();
    }

    private void Minimize_Click(object sender, RoutedEventArgs e)
    {
        SaveWindowPlacement();
        MinimizeRequested?.Invoke(this, EventArgs.Empty);
    }

    private void Settings_Click(object sender, RoutedEventArgs e) =>
        ShowSettingsHome();

    private void SettingsBack_Click(object sender, RoutedEventArgs e)
    {
        if (_isSettingsDetailOpen)
            ShowSettingsHome();
        else
            ShowHistoryView();
    }

    private void SettingsCategory_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string category })
            return;

        (string title, string description) = category switch
        {
            "Storage" => ("存储", "保存周期、容量上限与数据位置。"),
            "Hotkeys" => ("快捷键", "全局快捷键与冲突检测。"),
            "Privacy" => ("隐私", "暂停记录、应用排除与本地保护。"),
            "Appearance" => ("外观", "主题、强调色与语言将在后续小步接入。"),
            "Backup" => ("备份与恢复", "导出备份、合并恢复与数据安全。"),
            _ => ("常规", "启动、窗口与基础捕获行为。"),
        };

        _isSettingsDetailOpen = true;
        SettingsHomePanel.Visibility = Visibility.Collapsed;
        SettingsDetailPanel.Visibility = Visibility.Visible;
        SettingsDetailTitle.Text = title;
        SettingsDetailDescription.Text = description;

        // 按分类切换详情主体
        bool isGeneral = category == "General";
        bool isStorage = category == "Storage";
        bool isPrivacy = category == "Privacy";
        bool isHotkeys = category == "Hotkeys";
        bool isBackup = category == "Backup";
        bool isAppearance = category == "Appearance";
        SettingsGeneralBody.Visibility = isGeneral ? Visibility.Visible : Visibility.Collapsed;
        SettingsStorageBody.Visibility = isStorage ? Visibility.Visible : Visibility.Collapsed;
        SettingsPrivacyBody.Visibility = isPrivacy ? Visibility.Visible : Visibility.Collapsed;
        SettingsHotkeysBody.Visibility = isHotkeys ? Visibility.Visible : Visibility.Collapsed;
        SettingsBackupBody.Visibility = isBackup ? Visibility.Visible : Visibility.Collapsed;
        SettingsAppearanceBody.Visibility = isAppearance ? Visibility.Visible : Visibility.Collapsed;
        SettingsGenericBody.Visibility = (isGeneral || isStorage || isPrivacy || isHotkeys || isBackup || isAppearance) ? Visibility.Collapsed : Visibility.Visible;

        SettingsScrollViewer.ScrollToTop();
    }

    private async void ChangeDataDirectory_Click(object sender, RoutedEventArgs e)
    {
        // 单次运行 guard：防止双击/重复点击
        if (_isChangeDataDirectoryInProgress || _settingsViewModel is null)
            return;
        if (!_settingsViewModel.CanChangeDataDirectory)
            return;

        _isChangeDataDirectoryInProgress = true;
        try
        {
            // 1. 目录选择
            var folderDialog = new OpenFolderDialog
            {
                Title = "选择新的数据存储位置",
                Multiselect = false,
            };

            bool? dialogResult = folderDialog.ShowDialog(this);
            if (dialogResult != true)
                return; // 取消选择，零副作用

            string selectedParent = folderDialog.FolderName;

            // 2. 预检
            StorageMigrationPlanResult planResult = _settingsViewModel.TryPlan(selectedParent);
            if (!planResult.Succeeded)
            {
                await ShowMigrationErrorDialogAsync(planResult.Detail ?? "目录预检失败。");
                return;
            }

            StorageMigrationPlan plan = planResult.Plan!;

            // 3. 确认对话框
            var confirmDialog = new ContentDialog(DialogHost)
            {
                Title = "迁移数据目录",
                Content = BuildMigrationConfirmationContent(plan),
                PrimaryButtonText = "迁移并重启",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close,
                PrimaryButtonAppearance = ControlAppearance.Primary,
                DialogMaxWidth = 300,
            };

            ContentDialogResult confirmResult = await confirmDialog.ShowAsync();
            if (confirmResult != ContentDialogResult.Primary)
                return; // 用户取消

            // 4. 入队 + 重启
            StorageMigrationWorkflowResult workflowResult = _settingsViewModel.TryEnqueueAndRestart(plan);
            if (!workflowResult.Succeeded)
            {
                await ShowMigrationErrorDialogAsync(workflowResult.Detail ?? "迁移入队失败。");
            }
            // 成功时 Shutdown 已由 restart delegate 调用，不会到达这里
        }
        catch (Exception ex)
        {
            await ShowMigrationErrorDialogAsync($"操作失败: {ex.Message}");
        }
        finally
        {
            _isChangeDataDirectoryInProgress = false;
        }
    }

    private async Task ShowMigrationErrorDialogAsync(string detail)
    {
        var errorDialog = new ContentDialog(DialogHost)
        {
            Title = "无法迁移数据目录",
            Content = new System.Windows.Controls.TextBlock
            {
                Text = detail,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 360,
            },
            CloseButtonText = "知道了",
            DialogMaxWidth = 300,
        };
        try
        {
            await errorDialog.ShowAsync();
        }
        catch
        {
            // 错误反馈本身失败时不能让 async void 事件异常逃逸并终止应用。
        }
    }

    internal async Task ShowStorageMigrationCompletedAsync(string sourceRoot, string targetRoot)
    {
        var content = new StackPanel { MaxWidth = 268 };
        content.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "新数据目录",
            FontSize = 12,
            Foreground = Application.Current.TryFindResource("TextFillColorTertiaryBrush") as Brush,
        });
        content.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = targetRoot,
            ToolTip = targetRoot,
            Margin = new Thickness(0, 3, 0, 14),
            FontSize = 13,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        content.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "旧备份目录",
            FontSize = 12,
            Foreground = Application.Current.TryFindResource("TextFillColorTertiaryBrush") as Brush,
        });
        content.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = sourceRoot,
            ToolTip = sourceRoot,
            Margin = new Thickness(0, 3, 0, 12),
            FontSize = 13,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        content.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "旧目录不会自动删除，可在确认数据完整后自行处理。",
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Application.Current.TryFindResource("TextFillColorTertiaryBrush") as Brush,
        });

        var dialog = new ContentDialog(DialogHost)
        {
            Title = "数据迁移完成",
            Content = content,
            CloseButtonText = "知道了",
            DialogMaxWidth = 300,
        };

        try
        {
            await dialog.ShowAsync();
        }
        catch
        {
            // 成功提示失败不能影响已经完成并提交的数据迁移。
        }
    }

    /// <summary>
    /// 数据库损坏自动恢复后的提示，明确注明损坏库原位置、备份位置、新建空库位置。
    /// </summary>
    internal async Task ShowDatabaseRecoveredAsync(string corruptPath, string backupPath, string newPath)
    {
        var content = new StackPanel { MaxWidth = 288 };
        content.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "上次的数据库文件已损坏，无法读取历史记录。Clipora 已自动备份损坏文件并在原位置重建空数据库，可正常继续使用。",
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 14),
        });

        AppendRecoveryPathRow(content, "损坏的数据库（原位置）", corruptPath);
        AppendRecoveryPathRow(content, "已备份到", backupPath);
        AppendRecoveryPathRow(content, "新建空数据库", newPath);

        content.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "损坏文件不会自动删除；如需尝试找回旧历史，请保留该备份。",
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Application.Current.TryFindResource("TextFillColorTertiaryBrush") as Brush,
        });

        var dialog = new ContentDialog(DialogHost)
        {
            Title = "数据库已恢复",
            Content = content,
            CloseButtonText = "知道了",
            DialogMaxWidth = 340,
        };

        try
        {
            await dialog.ShowAsync();
        }
        catch
        {
            // 提示本身失败不能让 async void 启动回调的异常逃逸并终止应用。
        }
    }

    private static void AppendRecoveryPathRow(StackPanel host, string label, string path)
    {
        host.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = label,
            FontSize = 12,
            Foreground = Application.Current.TryFindResource("TextFillColorTertiaryBrush") as Brush,
        });
        host.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = path,
            ToolTip = path,
            Margin = new Thickness(0, 3, 0, 12),
            FontSize = 13,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
    }

    private static StackPanel BuildMigrationConfirmationContent(StorageMigrationPlan plan)
    {
        var panel = new StackPanel { MaxWidth = 268 };

        // 当前目录
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "当前目录",
            FontSize = 12,
            Foreground = Application.Current.TryFindResource("TextFillColorTertiaryBrush") as Brush,
            Margin = new Thickness(0, 0, 0, 2),
        });
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = plan.SourceRoot,
            FontSize = 13,
            TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = plan.SourceRoot,
            Margin = new Thickness(0, 0, 0, 14),
        });

        // 迁移到
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "迁移到",
            FontSize = 12,
            Foreground = Application.Current.TryFindResource("TextFillColorTertiaryBrush") as Brush,
            Margin = new Thickness(0, 0, 0, 2),
        });
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = plan.TargetRoot,
            FontSize = 13,
            TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = plan.TargetRoot,
            Margin = new Thickness(0, 0, 0, 14),
        });

        // 旧目录将保留
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "旧目录将保留",
            FontSize = 12,
            Foreground = Application.Current.TryFindResource("TextFillColorTertiaryBrush") as Brush,
            Margin = new Thickness(0, 0, 0, 2),
        });
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "迁移成功后不会自动删除，可人工核对",
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 14),
        });

        // 中性提示
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "迁移将在重启后开始；任一步失败仍使用原目录。请勿选择云同步目录。",
            FontSize = 11,
            Foreground = Application.Current.TryFindResource("TextFillColorTertiaryBrush") as Brush,
            TextWrapping = TextWrapping.Wrap,
        });

        return panel;
    }

    private void ShowSettingsHome()
    {
        if (DataContext is MainViewModel viewModel && viewModel.IsTagManagerOpen)
            viewModel.CloseTagManagerCommand.Execute(null);

        _isSettingsDetailOpen = false;
        HistoryContent.Visibility = Visibility.Collapsed;
        SettingsContent.Visibility = Visibility.Visible;
        SettingsHomePanel.Visibility = Visibility.Visible;
        SettingsDetailPanel.Visibility = Visibility.Collapsed;
        SettingsBackButton.Visibility = Visibility.Visible;
        SearchButton.Visibility = Visibility.Collapsed;
        TagManagerButton.Visibility = Visibility.Collapsed;
        AlwaysOnTopButton.Visibility = Visibility.Collapsed;
        SettingsButton.Visibility = Visibility.Collapsed;
        WindowTitleText.Text = "设置";
        WindowTitleText.Margin = new Thickness(14, 0, 0, 0);
        WindowTitleText.RenderTransform = null;
    }

    private void ShowHistoryView()
    {
        _isSettingsDetailOpen = false;
        SettingsRowControl.ResetAllExpanded();
        HistoryContent.Visibility = Visibility.Visible;
        SettingsContent.Visibility = Visibility.Collapsed;
        SettingsBackButton.Visibility = Visibility.Collapsed;
        SearchButton.Visibility = Visibility.Visible;
        TagManagerButton.Visibility = Visibility.Visible;
        AlwaysOnTopButton.Visibility = Visibility.Visible;
        SettingsButton.Visibility = Visibility.Visible;
        WindowTitleText.Text = "Clipora";
        WindowTitleText.Margin = new Thickness(14, 0, 0, 0);
        WindowTitleText.RenderTransform = new TranslateTransform(0, -2);
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        HandleCloseRequest();
    }

    private void HandleCloseRequest()
    {
        SaveWindowPlacement();
        switch (_settings.Current.CloseTo)
        {
            case CloseBehavior.Exit:
                AllowClose = true;
                Close();
                break;
            case CloseBehavior.FloatingBall:
                MinimizeRequested?.Invoke(this, EventArgs.Empty);
                break;
            default:
                Hide();
                break;
        }
    }

    private void AlwaysOnTop_Click(object sender, RoutedEventArgs e)
    {
        bool enabled = AlwaysOnTopButton.IsChecked == true;
        Topmost = enabled;
        _settings.Current.AlwaysOnTop = enabled;
        _settings.Save();
        UpdateAlwaysOnTopToolTip();
    }

    private void UpdateAlwaysOnTopToolTip() =>
        AlwaysOnTopButton.ToolTip = AlwaysOnTopButton.IsChecked == true ? "取消置顶" : "置顶窗口";

    private void QueueWindowPlacementSave()
    {
        if (!IsLoaded || WindowState != WindowState.Normal)
            return;

        _windowPlacementSaveTimer.Stop();
        _windowPlacementSaveTimer.Start();
    }

    private void RestoreWindowPlacement()
    {
        var current = _settings.Current;
        double virtualLeft = SystemParameters.VirtualScreenLeft;
        double virtualTop = SystemParameters.VirtualScreenTop;
        double virtualWidth = SystemParameters.VirtualScreenWidth;
        double virtualHeight = SystemParameters.VirtualScreenHeight;

        if (IsValidDimension(current.WindowWidth) && IsValidDimension(current.WindowHeight))
        {
            Width = Math.Clamp(current.WindowWidth!.Value, MinWidth, Math.Max(MinWidth, virtualWidth));
            Height = Math.Clamp(current.WindowHeight!.Value, MinHeight, Math.Max(MinHeight, virtualHeight));
        }

        if (!IsFinite(current.WindowLeft) || !IsFinite(current.WindowTop))
            return;

        WindowStartupLocation = WindowStartupLocation.Manual;
        double maxLeft = Math.Max(virtualLeft, virtualLeft + virtualWidth - Width);
        double maxTop = Math.Max(virtualTop, virtualTop + virtualHeight - Height);
        Left = Math.Clamp(current.WindowLeft!.Value, virtualLeft, maxLeft);
        Top = Math.Clamp(current.WindowTop!.Value, virtualTop, maxTop);
    }

    private void SaveWindowPlacement()
    {
        if (WindowState != WindowState.Normal
            || !double.IsFinite(Left)
            || !double.IsFinite(Top)
            || !double.IsFinite(ActualWidth)
            || !double.IsFinite(ActualHeight)
            || ActualWidth <= 0
            || ActualHeight <= 0)
        {
            return;
        }

        var current = _settings.Current;
        if (NearlyEqual(current.WindowWidth, ActualWidth)
            && NearlyEqual(current.WindowHeight, ActualHeight)
            && NearlyEqual(current.WindowLeft, Left)
            && NearlyEqual(current.WindowTop, Top))
        {
            return;
        }

        current.WindowWidth = ActualWidth;
        current.WindowHeight = ActualHeight;
        current.WindowLeft = Left;
        current.WindowTop = Top;
        _settings.Save();
    }

    private static bool IsValidDimension(double? value) =>
        IsFinite(value) && value > 0;

    private static bool IsFinite(double? value) =>
        value.HasValue && double.IsFinite(value.Value);

    private static bool NearlyEqual(double? saved, double actual) =>
        saved.HasValue && Math.Abs(saved.Value - actual) < 0.5;

    private static void OnFilterAnimatedHorizontalOffsetChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is HistoryWindow window && window._filterAnimatedScrollViewer is not null)
            window._filterAnimatedScrollViewer.ScrollToHorizontalOffset((double)e.NewValue);
    }

    private void FilterScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not ScrollViewer scrollViewer)
            return;

        _filterAnimatedScrollViewer = scrollViewer;

        double currentOffset = scrollViewer.HorizontalOffset;
        if (Math.Abs(_filterScrollTarget - currentOffset) > 240)
            _filterScrollTarget = currentOffset;

        _filterScrollTarget = Math.Clamp(_filterScrollTarget - (e.Delta * 0.45), 0, scrollViewer.ScrollableWidth);

        var animation = new DoubleAnimation(currentOffset, _filterScrollTarget, TimeSpan.FromMilliseconds(130))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        BeginAnimation(FilterAnimatedHorizontalOffsetProperty, animation, HandoffBehavior.SnapshotAndReplace);
        e.Handled = true;
    }

    private void SettingsSelector_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not ComboBox selector)
            return;

        if (selector.IsDropDownOpen)
        {
            ScrollViewer? dropDownScrollViewer = FindVisualAncestor<ScrollViewer>(e.OriginalSource as DependencyObject);
            if (dropDownScrollViewer is not null)
                BeginSmoothVerticalScroll(dropDownScrollViewer, e.Delta);
            e.Handled = true;
            return;
        }

        // ComboBox 收起后仍保有键盘焦点；默认行为会用滚轮静默改值。
        // 明确拦截并把滚动量交给设置内容区，避免误调节且不破坏页面滚动。
        BeginSmoothVerticalScroll(SettingsScrollViewer, e.Delta);
        e.Handled = true;
    }

    private void VerticalScroll_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        ScrollViewer? scrollViewer = sender switch
        {
            ScrollViewer direct => direct,
            ListBox listBox => FindVisualAncestor<ScrollViewer>(e.OriginalSource as DependencyObject)
                ?? FindVisualDescendant<ScrollViewer>(listBox),
            _ => null,
        };
        if (scrollViewer is null)
            return;

        // 在同一面上发起滚轮滑动会自动替换该面正在进行的"回到顶部"滑动，无需单独停止。
        BeginSmoothVerticalScroll(scrollViewer, e.Delta);
        e.Handled = true;
    }

    private void BeginSmoothVerticalScroll(ScrollViewer scrollViewer, int wheelDelta) =>
        _scroller.GlideBy(new ScrollViewerSurface(scrollViewer), wheelDelta);

    private void Root_DragEnter(object sender, DragEventArgs e) => UpdateExternalDropState(e);

    private void Root_DragOver(object sender, DragEventArgs e) => UpdateExternalDropState(e);

    private void Root_DragLeave(object sender, DragEventArgs e)
    {
        e.Handled = true;
        // 拖动经过子元素时 WPF 会在 root 上冒泡出 DragLeave/DragEnter 对；此时 e.GetPosition(root)
        // 在路由过程中偶尔返回过时/越界坐标，会把"窗口内移动"误判成离开 → 浮层闪烁。改用实时物理
        // 光标位置比对窗口矩形：只有光标真正离开窗口才收起，窗口内移动恒为内不闪。
        if (DragWindowHelpers.IsCursorInsideWindow(this))
            return;

        HideExternalDropOverlay();
    }

    private void Root_Drop(object sender, DragEventArgs e)
    {
        bool imported = CanAcceptExternalDrop(e.Data)
            && DataContext is MainViewModel viewModel
            && viewModel.ImportExternalDrop(e.Data);

        e.Effects = imported ? DragDropEffects.Copy : DragDropEffects.None;
        HideExternalDropOverlay();
        e.Handled = true;
    }

    private void UpdateExternalDropState(DragEventArgs e)
    {
        bool canAccept = CanAcceptExternalDrop(e.Data);
        e.Effects = canAccept ? DragDropEffects.Copy : DragDropEffects.None;
        if (canAccept)
        {
            ShowExternalDropOverlay();
            PetExternalDropWatchdog();
        }
        else
        {
            HideExternalDropOverlay();
        }
        e.Handled = true;
    }

    // DragOver 在拖动悬停于窗口期间每秒触发多次；每次喂狗，保证浮层显示期间看门狗始终被刷新。
    // 一旦拖动离开或在别处落下，事件停止，看门狗超时即收起浮层，避免卡死。
    private void PetExternalDropWatchdog()
    {
        _externalDropWatchdog ??= CreateExternalDropWatchdog();
        _externalDropWatchdog.Stop();
        _externalDropWatchdog.Start();
    }

    private DispatcherTimer CreateExternalDropWatchdog()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        timer.Tick += (_, _) =>
        {
            // DragOver 在拖动悬停于窗口静止不动时会停止供给（OLE 仅在鼠标移动时回投）。
            // 若仅凭计时判定，会误把"悬停瞄准"当作拖动结束。改用物理左键真实状态闸门：
            // 左键仍按下＝拖动尚未结束，续期等待；左键已松开＝拖动确已落下（如丢进回收站），收起浮层。
            if (DragWindowHelpers.IsPhysicalLeftButtonDown())
            {
                timer.Stop();
                timer.Start();
                return;
            }

            timer.Stop();
            HideExternalDropOverlay();
        };
        return timer;
    }

    private bool CanAcceptExternalDrop(IDataObject dataObject)
    {
        if (_isCardDragInProgress)
            return false;

        return ExternalDropSupport.HasAcceptableFormat(dataObject);
    }

    private void ShowExternalDropOverlay()
    {
        if (_isExternalDropOverlayShown)
            return;

        _isExternalDropOverlayShown = true;
        _externalDropOverlayAnimationVersion++;
        ExternalDropOverlay.Visibility = Visibility.Visible;
        ExternalDropOverlay.BeginAnimation(OpacityProperty, null);
        ExternalDropOverlay.Opacity = 0;
        ExternalDropOverlay.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(120))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            },
            HandoffBehavior.SnapshotAndReplace);
    }

    private void HideExternalDropOverlay()
    {
        _externalDropWatchdog?.Stop();

        if (!_isExternalDropOverlayShown)
            return;

        _isExternalDropOverlayShown = false;
        int animationVersion = ++_externalDropOverlayAnimationVersion;
        var animation = new DoubleAnimation(0, TimeSpan.FromMilliseconds(160))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        animation.Completed += (_, _) =>
        {
            if (_isExternalDropOverlayShown || animationVersion != _externalDropOverlayAnimationVersion)
                return;

            ExternalDropOverlay.BeginAnimation(OpacityProperty, null);
            ExternalDropOverlay.Opacity = 0;
            ExternalDropOverlay.Visibility = Visibility.Collapsed;
        };
        ExternalDropOverlay.BeginAnimation(
            OpacityProperty,
            animation,
            HandoffBehavior.SnapshotAndReplace);
    }

    private void Card_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_isCardDragInProgress)
        {
            e.Handled = true;
            return;
        }

        ResetCardDrag();
        if (sender is not CardButton button
            || button.DataContext is not ClipItemViewModel
            || DataContext is MainViewModel { HasOpenClipTagEditor: true })
        {
            return;
        }

        if (e.OriginalSource is DependencyObject source
            && FindVisualAncestor<ButtonBase>(source) is ButtonBase clickedButton
            && !ReferenceEquals(clickedButton, button))
        {
            return;
        }

        _cardDragSource = button;
        _cardDragStart = e.GetPosition(this);
        _awaitingFreshPress = false; // 真实的卡片按下：解除“等待新按下”闸门，允许这次拖动。
    }

    private void Card_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        int callDepth = ++_cardMoveHandlerDepth;
        try
        {
            // ReleaseMouseCapture 会同步重入 PreviewMouseMove。外层调用尚未开始 DoDragDrop，
            // 单靠“正在拖动”卫兵还挡不住内层调用；嵌套 Move 必须在入口直接返回。
            if (callDepth > 1)
                return;

            if (_isCardDragInProgress)
                return;

            // DoDragDrop 返回后的残留 MouseMove 必须等到一次真实的卡片按下才允许再次起拖。
            if (_awaitingFreshPress)
                return;

            if (e.LeftButton != MouseButtonState.Pressed
                || sender is not CardButton button
                || !ReferenceEquals(button, _cardDragSource)
                || button.DataContext is not ClipItemViewModel item)
            {
                return;
            }

            Point current = e.GetPosition(this);
            if (Math.Abs(current.X - _cardDragStart.X) < SystemParameters.MinimumHorizontalDragDistance
                && Math.Abs(current.Y - _cardDragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
            {
                return;
            }

            if (!ClipDragDataBuilder.TryBuild(item.Model, out DataObject? data, out bool referenceUnavailable))
            {
                if (referenceUnavailable)
                {
                    // 仅引用失效：标记手势已开始以防随后触发点击命令，提示并退出
                    item.SetReferenceInvalid(true);
                    _cardDragStarted = true;
                    e.Handled = true;
                    ShowFileReferenceUnavailableHint();
                }
                ResetCardDrag();
                return;
            }

            _isCardDragInProgress = true;
            try
            {
                // 之前失效的仅引用项本次恢复 → 清除失效状态
                if (item.IsReferenceOnlyFile)
                    item.SetReferenceInvalid(false);

                _cardDragStarted = true;
                e.Handled = true;
                button.ReleaseMouseCapture();
                DragDrop.DoDragDrop(button, data!, DragDropEffects.Copy);
            }
            finally
            {
                _awaitingFreshPress = true;
                Mouse.Capture(null);
                ResetCardDrag();
                _isCardDragInProgress = false;
            }
        }
        finally
        {
            _cardMoveHandlerDepth--;
        }
    }

    private void Card_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_isCardDragInProgress)
        {
            e.Handled = true;
            return;
        }

        if (_cardDragStarted)
            e.Handled = true;
        ResetCardDrag();
    }

    private void ResetCardDrag()
    {
        _cardDragSource = null;
        _cardDragStarted = false;
    }

    private void Root_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel)
            return;

        if (!viewModel.HasOpenClipTagEditor)
            return;

        if (e.OriginalSource is not DependencyObject tagSource)
            return;

        ClipItemViewModel? clickedItem = FindDataContext<ClipItemViewModel>(tagSource);
        ClipItemViewModel? openItem = viewModel.OpenClipTagEditor;

        if (clickedItem is null)
        {
            viewModel.DismissOpenTagEditors();
            return;
        }

        if (!ReferenceEquals(clickedItem, openItem))
        {
            viewModel.DismissOpenTagEditors();
            e.Handled = true;
        }
    }

    private void TagNameTextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TagManagementItemViewModel tag })
            tag.CommitRenameCommand.Execute(null);
    }

    private static T? FindDataContext<T>(DependencyObject? source)
        where T : class
    {
        while (source is not null)
        {
            if (source is FrameworkElement element && element.DataContext is T context)
                return context;

            source = VisualTreeHelper.GetParent(source);
        }

        return null;
    }

    public void ShowOverSizedHint(long sizeBytes)
    {
        string cap = ByteSizeFormatter.Format(_settings.Current.MaxItemBytes);
        string actual = ByteSizeFormatter.Format(sizeBytes);
        ShowHint("  内容超过上限，未保存", $"约 {actual}，上限 {cap}");
    }

    /// <summary>仅引用文件原路径不可用时的瞬时提示。</summary>
    public void ShowFileReferenceUnavailableHint()
    {
        ShowHint("  原文件已移动或删除，无法使用", "该记录仅保存原路径，请恢复文件或文件夹后重试");
    }

    /// <summary>文件/链接打开失败时的瞬时提示。</summary>
    public void ShowOpenFailedHint(string message)
    {
        ShowHint("  打开失败", message);
    }

    /// <summary>泛化瞬时提示：设置标题/详情文本，显示并启动 4 秒计时器。</summary>
    private void ShowHint(string title, string detail)
    {
        OverSizedHintTitleText.Text = title;
        OverSizedHintDetail.Text = detail;

        _overSizedHintTimer?.Stop();
        _overSizedHintTimer ??= CreateOverSizedHintTimer();
        _overSizedHintTimer.Start();

        ShowOverSizedHintOverlay();
    }

    private DispatcherTimer CreateOverSizedHintTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            HideOverSizedHintOverlay();
        };
        return timer;
    }

    private void ShowOverSizedHintOverlay()
    {
        _overSizedHintAnimationVersion++;
        OverSizedHint.Visibility = Visibility.Visible;
        OverSizedHint.BeginAnimation(OpacityProperty, null);
        OverSizedHint.Opacity = 0;
        OverSizedHint.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            },
            HandoffBehavior.SnapshotAndReplace);
    }

    private void HideOverSizedHintOverlay()
    {
        int animationVersion = ++_overSizedHintAnimationVersion;
        var animation = new DoubleAnimation(0, TimeSpan.FromMilliseconds(200))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        animation.Completed += (_, _) =>
        {
            if (animationVersion != _overSizedHintAnimationVersion)
                return;

            OverSizedHint.BeginAnimation(OpacityProperty, null);
            OverSizedHint.Opacity = 0;
            OverSizedHint.Visibility = Visibility.Collapsed;
        };
        OverSizedHint.BeginAnimation(OpacityProperty, animation, HandoffBehavior.SnapshotAndReplace);
    }

    // —— 图片悬停放大预览 ——

    private void Thumbnail_MouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not ClipItemViewModel vm)
            return;
        if (!vm.HasThumbnail || string.IsNullOrEmpty(vm.PreviewImagePath))
            return;

        CancelImagePreview();
        _imagePreviewCts = new CancellationTokenSource();
        _previewTarget = fe;
        _imagePreviewTimer!.Start();
    }

    private void Thumbnail_MouseLeave(object sender, MouseEventArgs e)
    {

        // Popup 打开后：MouseLeave 因 WS_EX_LAYERED 窗口干扰完全不可靠
        // （刚打开时虚假触发，用户真移开时反而不触发）。
        // 此时由 _previewCloseMonitor + Deactivated 负责关闭，不处理 MouseLeave。
        if (ImagePreviewPopup.IsOpen)
        {
            return;
        }

        // Popup 未打开：检测 CliporaCard 悬停上浮动画（3px）导致的虚假 Leave。
        if (sender is FrameworkElement fe)
        {
            Point pos = e.GetPosition(fe);
            const double tolerance = 4.0;
            if (pos.X >= -tolerance && pos.X <= fe.ActualWidth + tolerance &&
                pos.Y >= -tolerance && pos.Y <= fe.ActualHeight + tolerance)
            {
                return;
            }
        }

        CancelImagePreview();
    }

    private void CancelImagePreview()
    {
        _imagePreviewTimer!.Stop();
        _imagePreviewCts?.Cancel();
        _imagePreviewCts = null;
        HideImagePreview();
    }

    /// <summary>分组标题点击 → 切换展开/折叠。</summary>
    private void GroupHeader_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: System.Windows.Data.CollectionViewGroup { Name: ClipGroupHeader header } }
            && DataContext is MainViewModel vm)
        {
            vm.ToggleGroup(header);
            e.Handled = true;
        }
    }

    /// <summary>卡片首次加载：处理一次性入场动画，并安排缩略图加载。</summary>
    private void Item_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ClipItemViewModel vm } element)
            return;

        // DataContextChanged / IsVisibleChanged 不是 RoutedEvent，不能放进 XAML EventSetter。
        // 在容器首次 Loaded 时订阅；Recycling 期间容器保持 Loaded，订阅也会持续生效。
        element.DataContextChanged -= Item_DataContextChanged;
        element.DataContextChanged += Item_DataContextChanged;
        element.IsVisibleChanged -= Item_IsVisibleChanged;
        element.IsVisibleChanged += Item_IsVisibleChanged;

        if (vm.TryTakeEntranceAnimation())
        {
            element.BeginAnimation(
                OpacityProperty,
                new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
        }

        QueueThumbnailLoad(element, vm);
    }

    /// <summary>
    /// Recycling 虚拟化会在容器保持 Loaded 时替换 DataContext；必须按新 VM 重新安排加载。
    /// </summary>
    private void Item_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is FrameworkElement element && e.NewValue is ClipItemViewModel vm)
            QueueThumbnailLoad(element, vm);
    }

    /// <summary>分组展开或窗口恢复可见时，补偿先前因不可见而跳过的缩略图加载。</summary>
    private void Item_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true
            && sender is FrameworkElement { DataContext: ClipItemViewModel vm } element)
        {
            QueueThumbnailLoad(element, vm);
        }
    }

    private static async void QueueThumbnailLoad(FrameworkElement element, ClipItemViewModel vm)
    {
        await Task.Delay(90);
        if (!element.IsLoaded || !ReferenceEquals(element.DataContext, vm) || !element.IsVisible)
            return;

        await vm.EnsureThumbnailLoadedAsync();
    }

    /// <summary>
    /// 主面板列表滚动：按"上滚意图"浮出回到顶部按钮，"下滚一点距离"或近顶时收起。
    /// 滚动单位为像素（虚拟化面板使用 ScrollUnit=Pixel），阈值见上方常量。
    /// </summary>
    private void ItemsList_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        _historyScrollViewer ??= e.OriginalSource as ScrollViewer;

        if (!_settings.Current.ShowBackToTop)
        {
            HideBackToTop();
            return;
        }

        BackToTopAction action = BackToTopVisibilityPolicy.Decide(
            e.VerticalChange, e.VerticalOffset, _backToTopVisible,
            ref _upScrollAccum, ref _downScrollAccum);

        if (action == BackToTopAction.Show)
            ShowBackToTop();
        else if (action == BackToTopAction.Hide)
            HideBackToTop();
    }

    private void ShowBackToTop()
    {
        if (_backToTopVisible)
            return;
        _backToTopVisible = true;
        BackToTopButton.Visibility = Visibility.Visible;
        var fade = new DoubleAnimation(BackToTopRestOpacity, TimeSpan.FromMilliseconds(180));
        BackToTopButton.BeginAnimation(OpacityProperty, fade);
    }

    private void HideBackToTop()
    {
        if (!_backToTopVisible && BackToTopButton.Visibility != Visibility.Visible)
            return;
        _backToTopVisible = false;
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(150));
        fade.Completed += (_, _) =>
        {
            if (!_backToTopVisible)
                BackToTopButton.Visibility = Visibility.Collapsed;
        };
        BackToTopButton.BeginAnimation(OpacityProperty, fade);
    }

    private void BackToTop_Click(object sender, RoutedEventArgs e)
    {
        _upScrollAccum = 0;
        _downScrollAccum = 0;
        HideBackToTop();

        ScrollViewer? sv = _historyScrollViewer;
        if (sv is null)
            return;

        double startOffset = sv.VerticalOffset;
        if (startOffset <= 0)
            return;

        // 跟随 WPF 实际渲染帧（GlideTo 内部用 RenderingFrameClock），ease-out cubic 起步快、近顶减速；
        // 时长随起始偏移对数增长并钳制在 [320,520]ms（行为保持，公式逐字沿用）。
        // 在同一面上发起 GlideTo 会替换该面正在进行的滚轮滑动，等价于原先的两次 Stop。
        double durationMs = Math.Clamp(260 + Math.Log10(1 + startOffset) * 55, 320, 520);
        _scroller.GlideTo(new ScrollViewerSurface(sv), 0, ScrollGlide.Wheel.WithDuration(durationMs));
    }

    private async void OnImagePreviewTimerTick(object? sender, EventArgs e)
    {
        _imagePreviewTimer!.Stop();

        CancellationTokenSource? cts = _imagePreviewCts;
        if (cts is null || cts.IsCancellationRequested)
            return;

        FrameworkElement? fe = _previewTarget;
        if (fe is null || fe.DataContext is not ClipItemViewModel vm)
            return;

        string refPath = vm.PreviewImagePath!;
        BitmapSource? fullImage = await LoadFullImageAsync(refPath, cts.Token);

        if (cts.IsCancellationRequested || fullImage is null)
            return;

        PreviewFullImage.Source = fullImage;
        ShowImagePreview();
    }

    private static async Task<BitmapSource?> LoadFullImageAsync(string path, CancellationToken ct)
    {
        // 用专用 STA 线程加载 BitmapImage（满足 WPF 成像 STA 要求），
        // 避免阻塞 UI 线程，同时支持取消。
        var tcs = new TaskCompletionSource<BitmapSource?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                if (ct.IsCancellationRequested)
                {
                    tcs.TrySetCanceled(ct);
                    return;
                }

                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.UriSource = new Uri(path);
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.EndInit();
                bmp.Freeze();
                tcs.TrySetResult(bmp);
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();

        try
        {
            using (ct.Register(() => tcs.TrySetCanceled(ct)))
            {
                return await tcs.Task.ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch
        {
            return null;
        }
    }

    private void ShowImagePreview()
    {
        ImagePreviewBorder.Opacity = 0;
        ImagePreviewPopup.IsOpen = true;

        // Popup 打开后 MouseLeave 不再可靠（WS_EX_LAYERED 窗口干扰）。
        // 启动光标位置轮询作为关闭手段。
        _previewCloseMonitor!.Start();

        var animation = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        ImagePreviewBorder.BeginAnimation(OpacityProperty, animation, HandoffBehavior.SnapshotAndReplace);
    }

    private void HideImagePreview()
    {
        _previewCloseMonitor!.Stop();
        ImagePreviewPopup.IsOpen = false;
        PreviewFullImage.Source = null;
    }

    /// <summary>Popup 打开期间定期检查光标是否已远离缩略图（用屏幕绝对坐标绕过 WS_EX_LAYERED 窗口干扰）。</summary>
    private void OnPreviewCloseMonitorTick(object? sender, EventArgs e)
    {
        if (!ImagePreviewPopup.IsOpen || _previewTarget is null)
            return;

        NativeMethods.GetCursorPos(out NativeMethods.POINT cursorPt);

        double scaleX = 1.0;
        double scaleY = 1.0;
        PresentationSource? source = PresentationSource.FromVisual(_previewTarget);
        if (source?.CompositionTarget is not null)
        {
            double transformScaleX = source.CompositionTarget.TransformToDevice.M11;
            double transformScaleY = source.CompositionTarget.TransformToDevice.M22;
            if (transformScaleX > 0)
                scaleX = transformScaleX;
            if (transformScaleY > 0)
                scaleY = transformScaleY;
        }

        // PointToScreen 与 GetCursorPos 同属屏幕坐标；ActualWidth/Height 与 margin 是 DIP，
        // 比较前必须把尺寸扩展到物理像素，避免高 DPI 下安全区小于真实缩略图。
        Point screenPos = _previewTarget.PointToScreen(new Point(0, 0));
        const double marginDip = 60.0;
        double left = screenPos.X - marginDip * scaleX;
        double top = screenPos.Y - marginDip * scaleY;
        double right = screenPos.X + _previewTarget.ActualWidth * scaleX + marginDip * scaleX;
        double bottom = screenPos.Y + _previewTarget.ActualHeight * scaleY + marginDip * scaleY;

        if (cursorPt.X < left || cursorPt.X > right || cursorPt.Y < top || cursorPt.Y > bottom)
        {
            CancelImagePreview();
        }
    }

    /// <summary>用户点击其他窗口/桌面时关闭预览。</summary>
    private void OnWindowDeactivatedForPreview(object? sender, EventArgs e)
    {
        CancelImagePreview();
    }

    private static T? FindVisualAncestor<T>(DependencyObject? source)
        where T : DependencyObject
    {
        while (source is not null)
        {
            if (source is T match)
                return match;
            source = VisualTreeHelper.GetParent(source);
        }

        return null;
    }

    // —— 快捷键页事件处理（4.5.2b） ——
    private readonly Dictionary<HotkeyAction, CardButton> _hotkeyChangeButtons = new();

    /// <summary>「更改」按钮 → 进入录入态。</summary>
    private void HotkeyChange_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CardButton { Tag: string tag } button || _settingsViewModel is null)
            return;

        HotkeyAction action = tag switch
        {
            "PastePlain" => HotkeyAction.PastePlain,
            "SequentialPaste" => HotkeyAction.SequentialPaste,
            _ => HotkeyAction.OpenPanel,
        };

        _hotkeyChangeButtons[action] = button;

        // 已在录入其他按钮 → 先取消旧录入，避免旧定时器恢复不到旧按钮的文案
        if (_recordingAction is { } prevAction)
            EndRecording(prevAction, GetChangeButton(prevAction), cancelled: true);

        // 进入录入态
        _recordingAction = action;
        button.Content = "按下快捷键…（Esc 取消）";

        // 临时接管键盘
        AddHandler(PreviewKeyDownEvent, (KeyEventHandler)OnHotkeyRecordingKeyDown, handledEventsToo: true);

        // 几秒后超时取消（以防用户失去焦点）
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (_recordingAction == action)
                EndRecording(action, button, cancelled: true);
        };
        _recordingTimer = timer;
        timer.Start();
    }

    private void OnHotkeyRecordingKeyDown(object sender, KeyEventArgs e)
    {
        if (_recordingAction is not { } action)
            return;

        e.Handled = true;

        // Esc → 取消
        if (e.Key == Key.Escape)
        {
            EndRecording(action, GetChangeButton(action), cancelled: true);
            return;
        }

        // 忽略纯修饰键
        if (e.Key == Key.LeftCtrl || e.Key == Key.RightCtrl
            || e.Key == Key.LeftAlt || e.Key == Key.RightAlt
            || e.Key == Key.LeftShift || e.Key == Key.RightShift
            || e.Key == Key.LWin || e.Key == Key.RWin
            || e.Key == Key.System)
        {
            return;
        }

        // 由 WPF 键盘事件构造 gesture
        int vk = KeyInterop.VirtualKeyFromKey(e.Key);
        KeyboardDevice kb = e.KeyboardDevice;
        var gesture = HotkeyGesture.FromKeyInput(vk,
            ctrl: (kb.Modifiers & ModifierKeys.Control) != 0,
            alt: (kb.Modifiers & ModifierKeys.Alt) != 0,
            shift: (kb.Modifiers & ModifierKeys.Shift) != 0,
            win: (kb.Modifiers & ModifierKeys.Windows) != 0);

        // 拒绝 VK 合法但无法序列化的键（不在 VkToName 映射表，Format 返回空串会静默解绑）
        if (!gesture.IsValid || gesture.Format().Length == 0)
        {
            EndRecording(action, GetChangeButton(action), cancelled: true);
            return;
        }

        // 提交到 VM
        _settingsViewModel?.SubmitHotkeyBinding(action, gesture);
        EndRecording(action, GetChangeButton(action), cancelled: false);
    }

    private void EndRecording(HotkeyAction action, CardButton? button, bool cancelled)
    {
        _recordingAction = null;
        // 停止本次录入的超时定时器，避免旧定时器在同一动作二次录入时误触发取消。
        _recordingTimer?.Stop();
        _recordingTimer = null;
        RemoveHandler(PreviewKeyDownEvent, (KeyEventHandler)OnHotkeyRecordingKeyDown);

        if (button is not null)
            button.Content = "更改";
    }

    private CardButton? GetChangeButton(HotkeyAction action) =>
        _hotkeyChangeButtons.TryGetValue(action, out var btn) ? btn : null;

    /// <summary>「恢复默认」按钮。</summary>
    private void HotkeyReset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CardButton { Tag: string tag } || _settingsViewModel is null)
            return;

        HotkeyAction action = tag switch
        {
            "PastePlain" => HotkeyAction.PastePlain,
            "SequentialPaste" => HotkeyAction.SequentialPaste,
            _ => HotkeyAction.OpenPanel,
        };

        _settingsViewModel.ResetHotkeyToDefault(action);
    }

    // —— 隐私页事件处理 ——

    /// <summary>「✕」移除排除应用按钮。</summary>
    private void RemoveExclusion_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: ExcludedAppViewModel item }
            || _settingsViewModel is null)
            return;

        _settingsViewModel.RemoveExclusion(item);
    }

    /// <summary>「添加应用…」按钮 → 弹出运行中应用选择器 ContentDialog。</summary>
    private async void AddExcludedApp_Click(object sender, RoutedEventArgs e)
    {
        if (_settingsViewModel?.RunningAppsProvider is not { } appsProvider)
            return;

        var availableApps = appsProvider.GetUserApps();
        if (availableApps.Count == 0)
        {
            var emptyDialog = new ContentDialog(DialogHost)
            {
                Title = "排除应用",
                Content = new System.Windows.Controls.TextBlock
                {
                    Text = "当前没有检测到正在运行的应用。",
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 260,
                },
                CloseButtonText = "知道了",
                DialogMaxWidth = 300,
            };
            try { await emptyDialog.ShowAsync(); } catch { }
            return;
        }

        // 构建可滚动单选列表
        var listBox = new ListBox
        {
            MaxHeight = 320,
            Margin = new Thickness(0),
            BorderThickness = new Thickness(0),
            Background = System.Windows.Media.Brushes.Transparent,
            // 注意：不要在此设置 ItemContainerStyle 为 Button 样式——ListBox 容器是 ListBoxItem，
            // TargetType 不匹配会在生成容器时抛出而被外层 try/catch 吞掉，导致选择器静默失效。
        };
        listBox.SetValue(VirtualizingPanel.ScrollUnitProperty, ScrollUnit.Pixel);
        listBox.SetValue(VirtualizingPanel.VirtualizationModeProperty, VirtualizationMode.Recycling);
        listBox.AddHandler(PreviewMouseWheelEvent, new MouseWheelEventHandler(VerticalScroll_PreviewMouseWheel));

        var excludedSet = new HashSet<string>(
            _settingsViewModel.ExcludedApps.Select(a => a.ProcessName),
            StringComparer.OrdinalIgnoreCase);

        RunningAppInfo? selectedApp = null;

        foreach (var app in availableApps)
        {
            bool isExcluded = excludedSet.Contains(app.ProcessName);

            var itemBorder = new Border
            {
                Style = (System.Windows.Style)FindResource("CliporaCard"),
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var stack = new StackPanel { Margin = new Thickness(0, 2, 12, 2), VerticalAlignment = VerticalAlignment.Center };
            var titleBlock = new System.Windows.Controls.TextBlock
            {
                Text = app.DisplayName,
                FontSize = 14,
                Foreground = Application.Current.TryFindResource("TextFillColorPrimaryBrush") as System.Windows.Media.Brush,
            };
            if (isExcluded) titleBlock.Opacity = 0.45;
            stack.Children.Add(titleBlock);

            var descBlock = new System.Windows.Controls.TextBlock
            {
                Text = isExcluded ? $"{app.ProcessName}  — 已排除" : app.ProcessName,
                Margin = new Thickness(0, 2, 0, 0),
                FontSize = 12,
                Foreground = Application.Current.TryFindResource("TextFillColorTertiaryBrush") as System.Windows.Media.Brush,
            };
            stack.Children.Add(descBlock);

            Grid.SetColumn(stack, 0);
            grid.Children.Add(stack);

            if (!isExcluded)
            {
                var checkBlock = new System.Windows.Controls.TextBlock
                {
                    Text = "", // 右箭头
                    VerticalAlignment = VerticalAlignment.Center,
                    FontFamily = new System.Windows.Media.FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                    FontSize = 10,
                    Foreground = Application.Current.TryFindResource("TextFillColorTertiaryBrush") as System.Windows.Media.Brush,
                };
                Grid.SetColumn(checkBlock, 1);
                grid.Children.Add(checkBlock);
            }

            itemBorder.Child = grid;
            itemBorder.Tag = app;

            if (isExcluded)
                itemBorder.Opacity = 0.45;

            listBox.Items.Add(itemBorder);
        }

        listBox.SelectionChanged += (_, _) =>
        {
            if (listBox.SelectedItem is Border { Tag: RunningAppInfo selected })
                selectedApp = selected;
            else
                selectedApp = null;
        };

        var content = new StackPanel { MaxWidth = 268 };
        content.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "选择一个应用，从它复制的内容将不再被记录。",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Foreground = Application.Current.TryFindResource("TextFillColorTertiaryBrush") as System.Windows.Media.Brush,
            Margin = new Thickness(0, 0, 0, 10),
        });
        content.Children.Add(listBox);

        var dialog = new ContentDialog(DialogHost)
        {
            Title = "排除应用",
            Content = content,
            PrimaryButtonText = "添加",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            PrimaryButtonAppearance = ControlAppearance.Primary,
            DialogMaxWidth = 300,
        };

        // 默认主按钮禁用（未选中任何项）
        dialog.IsPrimaryButtonEnabled = false;
        listBox.SelectionChanged += (_, _) =>
        {
            dialog.IsPrimaryButtonEnabled = selectedApp is not null;
        };

        try
        {
            ContentDialogResult result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary && selectedApp is { } app)
            {
                _settingsViewModel.AddExclusion(app.ProcessName, app.DisplayName);
            }
        }
        catch
        {
            // 选择器崩溃不能影响整体功能。
        }
    }

    private static T? FindVisualDescendant<T>(DependencyObject parent) where T : DependencyObject
    {
        int childCount = VisualTreeHelper.GetChildrenCount(parent);
        for (int index = 0; index < childCount; index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match)
                return match;

            T? descendant = FindVisualDescendant<T>(child);
            if (descendant is not null)
                return descendant;
        }

        return null;
    }

    // ══════════════════════════════════════════════
    //  M5.3 外观——界面颜色分段点击
    // ══════════════════════════════════════════════

    private void ColorModeSystem_Click(object s, RoutedEventArgs e) => _settingsViewModel?.SetColorMode("System");
    private void ColorModeLight_Click(object s, RoutedEventArgs e) => _settingsViewModel?.SetColorMode("Light");
    private void ColorModeDark_Click(object s, RoutedEventArgs e) => _settingsViewModel?.SetColorMode("Dark");

    private void VisualThemeFluent_Click(object s, RoutedEventArgs e) => _settingsViewModel?.SetVisualTheme("Fluent");
    private void VisualThemeLiquidGlass_Click(object s, RoutedEventArgs e) => _settingsViewModel?.SetVisualTheme("LiquidGlass");

    private async void ChooseCustomBackground_Click(object sender, RoutedEventArgs e)
    {
        if (_settingsViewModel is null)
            return;

        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择背景图片",
            Filter = "图片文件 (*.jpg;*.jpeg;*.png)|*.jpg;*.jpeg;*.png",
            Multiselect = false,
        };

        if (dialog.ShowDialog(this) != true)
            return;

        CustomBackgroundApplyResult result = _settingsViewModel.SetCustomBackground(dialog.FileName);
        if (!result.Succeeded)
            await ShowCustomBackgroundErrorAsync(result);
    }

    private void ClearCustomBackground_Click(object sender, RoutedEventArgs e)
    {
        _settingsViewModel?.ClearCustomBackground();
        ClearCustomBackgroundLayer();
    }

    private void SettingsViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SettingsViewModel.VisualTheme)
            or nameof(SettingsViewModel.IsFluentSelected)
            or nameof(SettingsViewModel.CustomBackgroundPath))
        {
            ApplyBackgroundImage();
        }
        else if (e.PropertyName is nameof(SettingsViewModel.CustomBackgroundOpacity))
        {
            UpdateBackgroundOpacity();
        }
    }

    /// <summary>只在背景图片路径或主题变化时解码，滑条拖动时不再重新解码。</summary>
    private void ApplyBackgroundImage()
    {
        _cachedBackgroundPath = null;
        string? backgroundPath = _settingsViewModel?.CustomBackgroundFullPath;
        if (_settingsViewModel is null
            || !_settingsViewModel.IsFluentSelected
            || !_settingsViewModel.HasCustomBackground
            || string.IsNullOrWhiteSpace(backgroundPath)
            || !File.Exists(backgroundPath))
        {
            ClearCustomBackgroundLayer();
            return;
        }

        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            image.UriSource = new Uri(backgroundPath, UriKind.Absolute);
            image.EndInit();
            image.Freeze();

            var brush = new ImageBrush(image)
            {
                Stretch = Stretch.UniformToFill,
                AlignmentX = AlignmentX.Center,
                AlignmentY = AlignmentY.Center,
            };
            brush.Freeze();

            CustomBackgroundLayer.Background = brush;
            _cachedBackgroundPath = backgroundPath;
            UpdateBackgroundOpacity();
        }
        catch
        {
            ClearCustomBackgroundLayer();
        }
    }

    /// <summary>滑条每 tick 只更新透明度，不解码图片。</summary>
    private void UpdateBackgroundOpacity()
    {
        if (_cachedBackgroundPath is not null)
        {
            CustomBackgroundLayer.BeginAnimation(OpacityProperty, null);
            CustomBackgroundLayer.Opacity = _settingsViewModel!.CustomBackgroundOpacity / 100d;
        }
        else
        {
            ClearCustomBackgroundLayer();
        }
    }

    private void ClearCustomBackgroundLayer()
    {
        CustomBackgroundLayer.BeginAnimation(OpacityProperty, null);
        CustomBackgroundLayer.Opacity = 0;
        CustomBackgroundLayer.Background = null;
        _cachedBackgroundPath = null;
    }

    private async Task ShowCustomBackgroundErrorAsync(CustomBackgroundApplyResult result)
    {
        string title = result.Error switch
        {
            CustomBackgroundError.TooLarge => "背景图片超过上限，未应用",
            CustomBackgroundError.UnsupportedFormat => "不支持的背景图片格式",
            CustomBackgroundError.DecodeFailed => "无法读取背景图片",
            CustomBackgroundError.FileMissing => "背景图片不存在",
            _ => "无法应用背景图片",
        };

        string detail = result.Error switch
        {
            CustomBackgroundError.TooLarge => $"约 {ByteSizeFormatter.Format(result.SizeBytes ?? 0)}，上限 {ByteSizeFormatter.Format(SettingsViewModel.CustomBackgroundMaxBytes)}",
            CustomBackgroundError.UnsupportedFormat => "仅支持 JPG 和 PNG 图片。",
            CustomBackgroundError.DecodeFailed => "请选择可正常打开的 JPG 或 PNG 图片。",
            CustomBackgroundError.FileMissing => "请选择仍然存在的本地图片文件。",
            _ => result.Detail ?? "请换一张图片后重试。",
        };

        var errorDialog = new ContentDialog(DialogHost)
        {
            Title = title,
            Content = new System.Windows.Controls.TextBlock
            {
                Text = detail,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 320,
            },
            CloseButtonText = "知道了",
            DialogMaxWidth = 320,
        };

        try { await errorDialog.ShowAsync(); } catch { }
    }

    // ══════════════════════════════════════════════
    //  M5.2 备份导出/导入
    // ══════════════════════════════════════════════

    private async void ExportBackup_Click(object sender, RoutedEventArgs e)
    {
        if (_backupService is null)
            return;

        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "Clipora 备份 (*.clpbak)|*.clpbak",
            DefaultExt = ".clpbak",
            FileName = $"Clipora-{DateTime.Now:yyyyMMdd-HHmmss}.clpbak",
        };

        if (dlg.ShowDialog() != true)
            return;

        var progressDialog = new ContentDialog(DialogHost)
        {
            Title = "正在导出备份…",
            Content = new StackPanel
            {
                Children =
                {
                    new Wpf.Ui.Controls.ProgressRing { Width = 32, Height = 32, Margin = new Thickness(0, 8, 0, 8) },
                    new System.Windows.Controls.TextBlock { Text = "正在准备并打包数据…", TextWrapping = TextWrapping.Wrap },
                },
            },
            DialogMaxWidth = 320,
        };

        _ = progressDialog.ShowAsync();
        var result = await _backupService.ExportAsync(dlg.FileName, null, CancellationToken.None);
        progressDialog.Hide();

        if (result.Ok)
        {
            var okDialog = new ContentDialog(DialogHost)
            {
                Title = "导出成功",
                Content = new System.Windows.Controls.TextBlock
                {
                    Text = $"已导出 {result.ItemCount} 条记录\n文件大小：{result.Bytes / 1024.0 / 1024.0:F1} MB",
                    TextWrapping = TextWrapping.Wrap,
                },
                CloseButtonText = "知道了",
                DialogMaxWidth = 300,
            };
            await okDialog.ShowAsync();
        }
        else
        {
            var errDialog = new ContentDialog(DialogHost)
            {
                Title = "导出失败",
                Content = new System.Windows.Controls.TextBlock
                {
                    Text = result.Error ?? "未知错误",
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 360,
                },
                CloseButtonText = "知道了",
                DialogMaxWidth = 300,
            };
            await errDialog.ShowAsync();
        }
    }

    private async void ImportBackup_Click(object sender, RoutedEventArgs e)
    {
        if (_backupService is null)
            return;

        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Clipora 备份 (*.clpbak)|*.clpbak",
        };

        if (dlg.ShowDialog() != true)
            return;

        var preview = await _backupService.InspectAsync(dlg.FileName);
        if (!preview.Compatible)
        {
            var errDialog = new ContentDialog(DialogHost)
            {
                Title = "无法恢复",
                Content = new System.Windows.Controls.TextBlock
                {
                    Text = preview.Incompatibility ?? "归档不兼容或已损坏",
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 360,
                },
                CloseButtonText = "知道了",
                DialogMaxWidth = 300,
            };
            await errDialog.ShowAsync();
            return;
        }

        var confirmDialog = new ContentDialog(DialogHost)
        {
            Title = "确认恢复备份",
            Content = new System.Windows.Controls.TextBlock
            {
                Text = $"备份创建时间：{preview.CreatedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm}\n"
                     + $"条目数：{preview.ItemCount}\n\n"
                     + "将按内容去重合并到当前历史，不会删除现有内容。",
                TextWrapping = TextWrapping.Wrap,
            },
            PrimaryButtonText = "合并导入",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            PrimaryButtonAppearance = ControlAppearance.Primary,
            DialogMaxWidth = 320,
        };

        if (await confirmDialog.ShowAsync() != ContentDialogResult.Primary)
            return;

        var progressDialog = new ContentDialog(DialogHost)
        {
            Title = "正在恢复备份…",
            Content = new StackPanel
            {
                Children =
                {
                    new Wpf.Ui.Controls.ProgressRing { Width = 32, Height = 32, Margin = new Thickness(0, 8, 0, 8) },
                    new System.Windows.Controls.TextBlock { Text = "正在校验并合并数据…", TextWrapping = TextWrapping.Wrap },
                },
            },
            DialogMaxWidth = 320,
        };

        _ = progressDialog.ShowAsync();
        var result = await _backupService.ImportAsync(dlg.FileName, null, CancellationToken.None);
        progressDialog.Hide();

        if (result.Ok)
        {
            var okDialog = new ContentDialog(DialogHost)
            {
                Title = "恢复成功",
                Content = new System.Windows.Controls.TextBlock
                {
                    Text = $"已导入 {result.Imported} 条，跳过 {result.Skipped} 条重复",
                    TextWrapping = TextWrapping.Wrap,
                },
                CloseButtonText = "知道了",
                DialogMaxWidth = 300,
            };
            await okDialog.ShowAsync();
            _settingsViewModel?.RefreshHistory?.Invoke();
        }
        else
        {
            var errDialog = new ContentDialog(DialogHost)
            {
                Title = "恢复失败",
                Content = new System.Windows.Controls.TextBlock
                {
                    Text = $"导入失败，未改动现有数据。\n\n{result.Error}",
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 360,
                },
                CloseButtonText = "知道了",
                DialogMaxWidth = 300,
            };
            await errDialog.ShowAsync();
        }
    }
}
