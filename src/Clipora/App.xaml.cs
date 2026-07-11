using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Clipora.Abstractions;
using Clipora.Interop;
using Clipora.Models;
using Clipora.Services;
using Clipora.ViewModels;
using Clipora.Views;
using H.NotifyIcon;

namespace Clipora;

/// <summary>组合根：装配服务、剪贴板监听、托盘、全局快捷键、自动粘贴、单实例、关闭到托盘。</summary>
public partial class App : Application
{
    private const string MutexName = "Clipora.SingleInstance";
    private const string ShowEventName = "Clipora.ShowPanel";

    private Mutex? _mutex;
    private EventWaitHandle? _showEvent;
    private RegisteredWaitHandle? _showEventRegistration;
    private SettingsService? _settings;
    private ClipboardMonitorService? _monitor;
    private HotkeyService? _hotkey;
    private ForegroundTracker? _foreground;
    private TaskbarIcon? _tray;
    private System.Drawing.Icon? _trayIconResource;
    private HistoryWindow? _window;
    private FloatingBarWindow? _floatingBar;
    private RetentionCleanupService? _retentionCleanup;
    private StorageMigrationRestartCoordinator? _storageMigrationRestart;
    private SqliteClipStore? _clipStore;
    private ClipWriter? _clipWriter;
    private SequentialPasteSession? _sequentialPasteSession;
    private WindowsOcrService? _ocrService;
    private OcrProcessingService? _ocrProcessing;
    private readonly Queue<PendingHotkeyPaste> _pendingHotkeyPastes = new();
    private DispatcherTimer? _hotkeyPasteTimer;
    private DispatcherTimer? _trayRetryTimer;
    private int _trayRetryCount;
    private const int MaxTrayRetries = 3;
    private static readonly TimeSpan TrayRetryInterval = TimeSpan.FromSeconds(3);
    private bool _isPanelPreview;

    private sealed record PendingHotkeyPaste(uint MainVirtualKey, Action Execute);

    internal bool IsStorageMigrationCompletionLaunch { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        // 开发期：捕获未处理异常写入临时文件，便于诊断启动崩溃。
        DispatcherUnhandledException += (_, args) =>
        {
            try { System.IO.File.WriteAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "clipora-crash.txt"), args.Exception.ToString()); } catch { }
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            try { System.IO.File.WriteAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "clipora-crash.txt"), args.ExceptionObject?.ToString() ?? "unknown"); } catch { }
        };

        // M6.1a: 清理超过 7 天的旧崩溃日志
        CleanLegacyCrashLog();

        if (e.Args.Contains("--selftest")) { Shutdown(DevSelfTest.Run()); return; }
        if (e.Args.Contains("--thumbnailselftest")) { Shutdown(DevSelfTest.RunImageThumbnailContainer()); return; }
        if (e.Args.Contains("--dragselftest")) { Shutdown(DevSelfTest.RunDragData()); return; }
        if (e.Args.Contains("--ocr-status")) { Shutdown(DevSelfTest.RunOcrStatus()); return; }

        IsStorageMigrationCompletionLaunch = e.Args.Contains(StorageMigrationRestartCoordinator.CompletionArgument);

#if DEBUG
        // —— Debug fail-closed：开发版必须设置 CLIPORA_DATA_DIR，禁止访问正式数据库 ——
        string? devRoot = Environment.GetEnvironmentVariable("CLIPORA_DATA_DIR");
        bool validDevRoot = false;
        if (!string.IsNullOrWhiteSpace(devRoot))
        {
            try { validDevRoot = Path.IsPathRooted(Path.GetFullPath(devRoot)); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                validDevRoot = false;
            }
        }

        if (!validDevRoot)
        {
            string msg = "Clipora 开发版启动失败：必须通过 scripts/start-dev.ps1 启动（该脚本会设置 CLIPORA_DATA_DIR 环境变量），禁止直接运行 Debug exe 以免损坏正式数据。";
            try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "clipora-crash.txt"), msg); } catch { }
            MessageBox.Show(msg, "Clipora — 开发环境检查", MessageBoxButton.OK, MessageBoxImage.Stop);
            Shutdown(-1);
            return;
        }
#endif

        // Debug dumps must pass the same data-root guard as the interactive app.
        if (e.Args.Contains("--dump")) { Shutdown(DevSelfTest.Dump()); return; }

        // —— 单实例：已在运行则通知其显示面板后退出 ——
        _mutex = new Mutex(true, MutexName, out bool createdNew);
        if (!createdNew)
        {
            try { EventWaitHandle.OpenExisting(ShowEventName).Set(); } catch { /* ignore */ }
            Shutdown();
            return;
        }
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        _showEventRegistration = ThreadPool.RegisterWaitForSingleObject(
            _showEvent,
            (_, _) => Dispatcher.Invoke(ShowPanel),
            null,
            -1,
            false);

        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown; // 关窗不退出（常驻托盘后台记录）

        // —— 便携版检测：若 exe 同目录存在 Clipora.exe.portable 标记文件，
        //    且未设置 CLIPORA_DATA_DIR，则自动启用便携模式（数据存储在 .\portable-data\）。
        string? portableEnv = Environment.GetEnvironmentVariable("CLIPORA_DATA_DIR");
        if (string.IsNullOrWhiteSpace(portableEnv))
        {
            string? exeDir = Path.GetDirectoryName(Environment.ProcessPath);
            if (!string.IsNullOrWhiteSpace(exeDir))
            {
                string markerPath = Path.Combine(exeDir, "Clipora.exe.portable");
                if (File.Exists(markerPath))
                {
                    string portableDataDir = Path.Combine(exeDir, "portable-data");
                    Directory.CreateDirectory(portableDataDir);
                    Environment.SetEnvironmentVariable("CLIPORA_DATA_DIR", portableDataDir);
                }
            }
        }

        // —— 数据目录启动闸门（3a-3c-2）：在构造 AppPaths/Database 前完成恢复或显式退出 ——
        RegistryStorageMigrationStateStore? migrationStateStore = null;
        StorageMigrationStartupResult? completedMigration = null;
        AppPaths? paths = null;
        var locationService = StorageLocationService.CreateProduction();
        var defaultRecovery = new StorageDefaultRecoveryService();

        while (paths is null)
        {
            StorageRootResolution resolution;
            try
            {
                resolution = locationService.Resolve();
            }
            catch (StorageLocationException ex)
            {
                StorageStartupDecision decision = StorageStartupWindow.ShowLocationFailure(ex);
                if (decision == StorageStartupDecision.Exit)
                {
                    Shutdown();
                    return;
                }

                if (decision == StorageStartupDecision.UseDefault)
                {
                    StorageDefaultRecoveryResult recovery = defaultRecovery.RecoverMissingRoot(ex.PathValue ?? string.Empty);
                    if (!recovery.Succeeded)
                    {
                        var recoveryFailure = new StorageLocationException(
                            StorageLocationError.InvalidPath,
                            recovery.Detail ?? "无法恢复默认数据位置。",
                            ex.PathValue);
                        StorageStartupDecision recoveryDecision = StorageStartupWindow.ShowLocationFailure(
                            recoveryFailure,
                            recovery.Detail);
                        if (recoveryDecision == StorageStartupDecision.Exit)
                        {
                            Shutdown();
                            return;
                        }
                    }
                }

                continue;
            }

            migrationStateStore = RegistryStorageMigrationStateStore.CreateProduction();
            if (resolution.CanMigrate)
            {
                StorageMigrationState state;
                try
                {
                    state = migrationStateStore.Read();
                }
                catch (StorageLocationException ex)
                {
                    StorageStartupDecision decision = StorageStartupWindow.ShowLocationFailure(ex);
                    if (decision == StorageStartupDecision.Exit)
                    {
                        Shutdown();
                        return;
                    }
                    continue;
                }

                if (state.PendingRoot is not null && state.MigrationId is not null)
                {
                    var migrationEngine = new StorageMigrationEngine(migrationStateStore);
                    var migrationCoordinator = new StorageMigrationStartupCoordinator(migrationStateStore, migrationEngine);
                    StorageMigrationStartupResult? migrationResult = StorageStartupWindow.RunMigration(
                        progress => migrationCoordinator.ProcessPending(true, progress));
                    if (migrationResult is null)
                    {
                        Shutdown();
                        return;
                    }

                    if (migrationResult.Action == StorageMigrationStartupAction.Completed)
                        completedMigration = migrationResult;

                    // coordinator 可能已切换 DataRoot；回到循环重新解析唯一真相。
                    continue;
                }
            }

            paths = new AppPaths(resolution);
        }

        _storageMigrationRestart = StorageMigrationRestartCoordinator.CreateProduction(
            migrationStateStore ?? RegistryStorageMigrationStateStore.CreateProduction(),
            paths.CanMigrate,
            IsReleaseBuild);

        // —— 数据库损坏自动恢复：在构造 Database / 执行任何查询前检测；
        //    损坏则备份坏库并腾出位置，由下面的 new Database 重建空库，避免“窗口未显示即崩溃”。 ——
        DatabaseRecoveryResult dbRecovery;
        try { dbRecovery = new DatabaseRecoveryService(paths).EnsureHealthy(); }
        catch (Exception ex) { dbRecovery = DatabaseRecoveryResult.Healthy(ex.Message); }

        var database = new Database(paths);
        _clipStore = new SqliteClipStore(database);
        var tagStore = new SqliteTagStore(database);
        _settings = new SettingsService(paths);
        var thumbnails = new ThumbnailService(paths);
        var source = new SourceResolver();
        var classifier = new ContentClassifier(thumbnails, source, paths, _settings);

        _monitor = new ClipboardMonitorService(_clipStore, classifier, _settings, source);
        _clipWriter = new ClipWriter();
        var viewModel = new MainViewModel(_clipStore, tagStore, _monitor, _clipWriter);
        viewModel.Used += OnItemUsed;

        _foreground = new ForegroundTracker();
        _foreground.Start();
        _monitor.Start();

        // —— SettingsViewModel + 存储迁移 planner 接线（3a-3b） ——
        var autoStart = new AutoStartService();
        IStorageMigrationPlanner? settingsPlanner = null;
        bool canChangeDataDirectory = false;
        if (paths.CanMigrate && IsReleaseBuild && migrationStateStore is not null)
        {
            settingsPlanner = new StorageMigrationPlanService(
                paths.Root,
                paths.CanMigrate,
                migrationStateStore);
            canChangeDataDirectory = true;
        }

        var runningApps = new RunningAppsProvider();

        // —— 主题服务 ——
        var themeService = new ThemeService();
        themeService.ApplyLiquidGlassTransparency(_settings.Current.LiquidGlassTransparency);
        themeService.ApplyColorMode(_settings.Current.ColorMode);
        themeService.ApplyVisualTheme(_settings.Current.VisualTheme);

        // —— OCR 服务 ——
        _ocrService = new WindowsOcrService();

        _hotkey = new HotkeyService();
        _hotkey.Pressed += OnHotkey;

        // —— 快捷键注册/重注册 helper（4.5.2a） ——
        var hotkeySvc = _hotkey;
        IReadOnlyList<HotkeyRegistration> ReRegisterHotkeys()
        {
            var gestures = new Dictionary<HotkeyAction, HotkeyGesture>();
            if (HotkeyGesture.TryParse(_settings.Current.HotkeyOpenPanel, out var openG))
                gestures[HotkeyAction.OpenPanel] = openG;
            if (HotkeyGesture.TryParse(_settings.Current.HotkeyPastePlain, out var pasteG))
                gestures[HotkeyAction.PastePlain] = pasteG;
            if (HotkeyGesture.TryParse(_settings.Current.HotkeySequentialPaste, out var seqG))
                gestures[HotkeyAction.SequentialPaste] = seqG;

            return hotkeySvc.RegisterAll(gestures);
        }

        var settingsVm = new SettingsViewModel(
            _settings,
            autoStart,
            paths.Root,
            settingsPlanner,
            canChangeDataDirectory,
            id =>
            {
                bool succeeded = RequestStorageMigrationRestart(id, out string? error);
                return (succeeded, error);
            },
            runningApps,
            ReRegisterHotkeys,
            _ocrService,
            themeService);

        var hotkeyResults = ReRegisterHotkeys();

        // —— 顺序粘贴会话（4.5-seq） ——
        _sequentialPasteSession = new SequentialPasteSession(idleSeconds: 60);
        _monitor.ClipCaptured += (_, _) => _sequentialPasteSession.Reset();

        // —— 备份服务 ——
        var backupService = new BackupService(paths, database);
        var backupRecovery = new BackupImportRecoveryService(database, paths.Root);
        backupRecovery.RecoverAll(); // 启动时恢复未完成的导入

        settingsVm.RefreshHistory = () => Dispatcher.Invoke(() => viewModel.Reload());

        _window = new HistoryWindow(_settings) { DataContext = viewModel, SettingsViewModel = settingsVm, BackupService = backupService };
        MainWindow = _window; // 临时启动窗可能曾成为首个 Window；正常组合根必须显式接管 MainWindow。
        // 把主窗口交给主题服务：仅「跟随系统」模式监听系统主题变化，固定浅/深色不被系统覆盖。
        themeService.AttachWindow(_window);
        // 按当前视觉皮肤设置窗口背景质感：液态玻璃 = Acrylic，Fluent = Mica（视觉基线不变）。
        _window.WindowBackdropType = themeService.CurrentBackdrop;
        _window.MinimizeRequested += (_, _) => MinimizePanel();
        _window.ExitRequested += (_, _) => ExitApp();
        _monitor.ItemOverSized += (_, size) => Dispatcher.Invoke(() => _window?.ShowOverSizedHint(size));
        viewModel.FileReferenceUnavailable += () => Dispatcher.Invoke(() => _window?.ShowFileReferenceUnavailableHint());
        viewModel.OpenFailed += (msg) => Dispatcher.Invoke(() => _window?.ShowOpenFailedHint(msg));

        // —— 保留期自动清理（M4.2.2a） ——
        var eraser = new ClipItemFileEraser(paths);
        _retentionCleanup = new RetentionCleanupService(_clipStore, eraser, _settings);
        _retentionCleanup.ItemsPurged += (_, _) => Dispatcher.Invoke(() => viewModel.Reload());
        Task.Run(() => _retentionCleanup.RunCleanup()); // 启动清理（后台，不阻塞 UI）

        // —— OCR 后台处理 ——
        _ocrProcessing = new OcrProcessingService(_ocrService, _clipStore, _settings);
        _ocrProcessing.Start();
        _monitor.ClipCaptured += (_, item) =>
        {
            if (item.Type == ClipType.Image)
                _ocrProcessing.TryEnqueue(item.Id);
        };

        // 6 小时周期清理（常驻托盘多日实例）
        var cleanupTimer = new DispatcherTimer { Interval = TimeSpan.FromHours(6) };
        cleanupTimer.Tick += (_, _) => Task.Run(() => _retentionCleanup?.RunCleanup());
        cleanupTimer.Start();

        _floatingBar = new FloatingBarWindow();
        _floatingBar.RestoreRequested += (_, _) => ShowPanel();
        _floatingBar.PreviewRequested += (_, _) => ShowPanelPreview();
        _floatingBar.PreviewDismissed += (_, _) => HidePanelPreview();
        // 悬浮球拖入即存：落点复用面板同一条入库链路，存进去的条目/去重/超限/来源一致。
        _floatingBar.ExternalDropRequested += (_, args) => args.Imported = viewModel.ImportExternalDrop(args.Data);

        var trayResult = CreateTray();
        if (trayResult == TrayCreateResult.Failure)
            StartTrayRetry();

        bool forceShowForMigration = StorageStartupWindowPolicy.ShouldForceShowMain(
            IsStorageMigrationCompletionLaunch,
            completedMigration);
        bool forceShowForTrayFailure = TrayStartupPolicy.Decide(trayResult) == TrayStartupDecision.ForceShowPanel;
        // 数据库损坏恢复后必须强制显示面板，确保用户看到恢复提示（含损坏/备份/新库三处位置）。
        if (!_settings.Current.SilentStart || forceShowForMigration || forceShowForTrayFailure || dbRecovery.Recovered)
            ShowPanel();

        if (dbRecovery is { Recovered: true, CorruptPath: not null, BackupPath: not null, NewPath: not null })
        {
            _ = Dispatcher.BeginInvoke(new Action(async () =>
                await _window.ShowDatabaseRecoveredAsync(
                    dbRecovery.CorruptPath,
                    dbRecovery.BackupPath,
                    dbRecovery.NewPath)));
        }

        if (completedMigration is
            {
                Action: StorageMigrationStartupAction.Completed,
                SourceRoot: not null,
                TargetRoot: not null,
            })
        {
            _ = Dispatcher.BeginInvoke(new Action(async () =>
                await _window.ShowStorageMigrationCompletedAsync(
                    completedMigration.SourceRoot,
                    completedMigration.TargetRoot)));
        }
    }

    private static void TryWriteDiag(string message)
    {
        try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "clipora-crash.txt"), message); } catch { }
    }

    private void OnItemUsed(ClipItem item)
    {
        // 点击后面板保留（不自动关闭）。如开启自动粘贴，则把上一个目标窗口前置并模拟 Ctrl+V，
        // 面板随之退到后台（不隐藏），可继续连续取用。
        if (_settings?.Current.AutoPasteOnUse == true)
            PasteToForeground();
    }

    private void PasteToForeground()
    {
        IntPtr target = _foreground?.LastForeignWindow ?? IntPtr.Zero;
        if (target == IntPtr.Zero)
            return;

        NativeMethods.SetForegroundWindow(target);

        // 等焦点切换稳定后再发送 Ctrl+V
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(110) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            NativeMethods.keybd_event(NativeMethods.VK_CONTROL, 0, 0, UIntPtr.Zero);
            NativeMethods.keybd_event(NativeMethods.VK_V, 0, 0, UIntPtr.Zero);
            NativeMethods.keybd_event(NativeMethods.VK_V, 0, NativeMethods.KEYEVENTF_KEYUP, UIntPtr.Zero);
            NativeMethods.keybd_event(NativeMethods.VK_CONTROL, 0, NativeMethods.KEYEVENTF_KEYUP, UIntPtr.Zero);
        };
        timer.Start();
    }

    // —— 快捷键路由（4.5.2a） ——

    private void OnHotkey(object? sender, HotkeyAction action)
    {
        switch (action)
        {
            case HotkeyAction.OpenPanel:
                TogglePanel();
                break;
            case HotkeyAction.PastePlain:
                QueueHotkeyPaste(HotkeyAction.PastePlain, PastePlainToForeground);
                break;
            case HotkeyAction.SequentialPaste:
                DoSequentialPaste();
                break;
        }
    }

    private const int MaxBurstScan = 50;
    private const double BurstGapSeconds = 30;

    /// <summary>顺序粘贴：取最近历史→计算 burst→状态机推进→Write+Ctrl+V。</summary>
    private void DoSequentialPaste()
    {
        if (_clipStore is null || _clipWriter is null || _sequentialPasteSession is null)
            return;

        // 直接让存储层按 CreatedAt 降序取最近 50 条；不能先按置顶截断，否则大量旧置顶项会挤掉最新项。
        var recent = _clipStore.Query(new ClipQuery
        {
            Take = MaxBurstScan,
            PrioritizePinned = false,
        });
        if (recent.Count == 0)
            return;

        // 提取 CreatedAt 列表（现已严格降序）
        var times = new DateTime[recent.Count];
        for (int i = 0; i < recent.Count; i++)
            times[i] = recent[i].CreatedAt;

        // 计算 burst 下标（相对 recent 列表，oldest→newest）
        var burst = SequentialPasteBurstPlanner.ComputeMostRecentBurst(times, BurstGapSeconds);
        if (burst.Count == 0)
            return;

        // 用稳定 ClipItem Id 作为会话批次身份，避免列表变化但下标形状相同时误续旧会话。
        var burstItemIds = new long[burst.Count];
        for (int i = 0; i < burst.Count; i++)
            burstItemIds[i] = recent[burst[i]].Id;

        // 状态机推进
        var step = _sequentialPasteSession.Press(burstItemIds, DateTime.UtcNow);
        if (step is not { } s)
            return;

        ClipItem? item = null;
        for (int i = 0; i < recent.Count; i++)
        {
            if (recent[i].Id == s.ItemId)
            {
                item = recent[i];
                break;
            }
        }
        if (item is null)
            return;

        QueueHotkeyPaste(HotkeyAction.SequentialPaste, () => PasteItemToForeground(item));
    }

    /// <summary>
    /// 全局热键在 WM_HOTKEY 到达时通常仍处于按下状态。动作必须串行排队，等主键与修饰键全部释放后再执行，
    /// 否则模拟的 Ctrl+V 会和原热键组合，并且快速连按会先覆盖前一次尚未粘贴的剪贴板内容。
    /// </summary>
    private void QueueHotkeyPaste(HotkeyAction action, Action execute)
    {
        uint mainVirtualKey = 0;
        string? gestureText = action switch
        {
            HotkeyAction.PastePlain => _settings?.Current.HotkeyPastePlain,
            HotkeyAction.SequentialPaste => _settings?.Current.HotkeySequentialPaste,
            _ => null,
        };
        if (HotkeyGesture.TryParse(gestureText, out HotkeyGesture gesture))
            mainVirtualKey = gesture.VirtualKey;

        _pendingHotkeyPastes.Enqueue(new PendingHotkeyPaste(mainVirtualKey, execute));

        if (_hotkeyPasteTimer is null)
        {
            _hotkeyPasteTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(12) };
            _hotkeyPasteTimer.Tick += (_, _) => DrainHotkeyPasteQueue();
        }
        _hotkeyPasteTimer.Start();
    }

    private void DrainHotkeyPasteQueue()
    {
        if (_pendingHotkeyPastes.Count == 0)
        {
            _hotkeyPasteTimer?.Stop();
            return;
        }

        PendingHotkeyPaste pending = _pendingHotkeyPastes.Peek();
        bool released = HotkeyPasteReleaseGate.AreReleased(
            pending.MainVirtualKey,
            key => (NativeMethods.GetAsyncKeyState(key) & 0x8000) != 0);
        if (!released)
            return;

        _pendingHotkeyPastes.Dequeue();
        try { pending.Execute(); }
        catch { /* 单次粘贴失败不得阻塞后续队列。 */ }

        if (_pendingHotkeyPastes.Count == 0)
            _hotkeyPasteTimer?.Stop();
    }

    /// <summary>全保真写回 + 对当前前台 Ctrl+V。仅引用失效时跳过粘贴但仍推进。</summary>
    private void PasteItemToForeground(ClipItem item)
    {
        if (_clipWriter is null)
            return;

        try
        {
            ClipWriteResult writeResult = _clipWriter.Write(item);
            if (writeResult == ClipWriteResult.ReferenceUnavailable)
                return; // 仅引用失效：跳过粘贴但 session 已推进（best-effort）

            // 对当前前台发送 Ctrl+V（全局热键不夺焦，前台即目标窗口）
            NativeMethods.keybd_event(NativeMethods.VK_CONTROL, 0, 0, UIntPtr.Zero);
            NativeMethods.keybd_event(NativeMethods.VK_V, 0, 0, UIntPtr.Zero);
            NativeMethods.keybd_event(NativeMethods.VK_V, 0, NativeMethods.KEYEVENTF_KEYUP, UIntPtr.Zero);
            NativeMethods.keybd_event(NativeMethods.VK_CONTROL, 0, NativeMethods.KEYEVENTF_KEYUP, UIntPtr.Zero);
        }
        catch
        {
            // 剪贴板被占用等异常静默跳过
        }
    }

    /// <summary>纯文本粘贴：读当前剪贴板文本→剥离格式→写回→对当前前台发 Ctrl+V。</summary>
    private void PastePlainToForeground()
    {
        try
        {
            // 读当前剪贴板纯文本
            string? plain = System.Windows.Clipboard.GetText(TextDataFormat.UnicodeText);
            if (string.IsNullOrEmpty(plain))
                return;

            // 附加内部标记后写回纯文本，避免刷新历史项时间。
            var data = new DataObject();
            data.SetText(plain, TextDataFormat.UnicodeText);
            ClipboardInternalWriteMarker.SetClipboard(data);

            // 对当前前台发送 Ctrl+V（面板未夺焦，无需 SetForegroundWindow）
            NativeMethods.keybd_event(NativeMethods.VK_CONTROL, 0, 0, UIntPtr.Zero);
            NativeMethods.keybd_event(NativeMethods.VK_V, 0, 0, UIntPtr.Zero);
            NativeMethods.keybd_event(NativeMethods.VK_V, 0, NativeMethods.KEYEVENTF_KEYUP, UIntPtr.Zero);
            NativeMethods.keybd_event(NativeMethods.VK_CONTROL, 0, NativeMethods.KEYEVENTF_KEYUP, UIntPtr.Zero);
        }
        catch
        {
            // 剪贴板被占用等异常静默跳过
        }
    }

    private void TogglePanel()
    {
        if (_window is null)
            return;
        if (_isPanelPreview)
        {
            ShowPanel();
            return;
        }
        if (_window.IsVisible)
            _window.Hide();
        else
            ShowPanel();
    }

    private void ShowPanel()
    {
        if (_window is null)
            return;

        _isPanelPreview = false;
        _floatingBar?.Hide();
        _window.ShowActivated = true;
        _window.Show();
        _window.WindowState = WindowState.Normal;
        _window.Activate();

        // 强制前置
        _window.Topmost = true;
        _window.Topmost = _settings?.Current.AlwaysOnTop ?? false;
    }

    private void ShowPanelPreview()
    {
        if (_window is null || _floatingBar?.IsVisible != true || _window.IsVisible)
            return;

        _isPanelPreview = true;
        _window.ShowActivated = false;
        _window.Topmost = true;
        _window.WindowState = WindowState.Normal;
        _window.Show();

        // 主窗口若紧贴屏幕边缘，预览也不能盖住悬浮条的命中区域。
        _floatingBar.Topmost = false;
        _floatingBar.Topmost = true;
    }

    private void HidePanelPreview()
    {
        if (!_isPanelPreview || _window is null)
            return;

        _isPanelPreview = false;
        _window.Hide();
        _window.ShowActivated = true;
        _window.Topmost = _settings?.Current.AlwaysOnTop ?? false;
    }

    private void MinimizePanel()
    {
        if (_window is null)
            return;

        _isPanelPreview = false;
        if (_settings?.Current.MinimizeTo != MinimizeBehavior.FloatingBall || _floatingBar is null)
        {
            _window.Hide();
            return;
        }

        _floatingBar.PositionNear(_window);
        _window.Hide();
        _floatingBar.Show();
    }

    private TrayCreateResult CreateTray()
    {
        var menu = new ContextMenu();
        menu.SetResourceReference(FrameworkElement.StyleProperty, "TrayContextMenu");

        var open = CreateTrayMenuItem("打开 Clipora", "\uE8A7");
        open.Click += (_, _) => ShowPanel();

        var pause = CreateTrayMenuItem("暂停记录", "\uE769", isCheckable: true);
        pause.Click += (_, _) =>
        {
            // 经设置 VM 设值：保存到磁盘并通知设置页同步（VM 不可用时回退直写设置）。
            var settingsVm = _window?.SettingsViewModel;
            if (settingsVm is not null)
                settingsVm.Paused = pause.IsChecked;
            else if (_settings is not null)
            {
                _settings.Current.Paused = pause.IsChecked;
                _settings.Save();
            }
        };

        var exit = CreateTrayMenuItem("退出 Clipora", "\uE7E8");
        exit.Click += (_, _) => ExitApp();

        menu.Items.Add(open);
        menu.Items.Add(pause);
        var separator = new Separator();
        separator.SetResourceReference(FrameworkElement.StyleProperty, "TrayMenuSeparator");
        menu.Items.Add(separator);
        menu.Items.Add(exit);

        _tray = new TaskbarIcon
        {
            ToolTipText = "Clipora 剪贴板",
            NoLeftClickDelay = true,
            ContextMenu = menu,
        };

        // 优先加载正式 ICO 文件 → HICON 直设（绕过 IconSource→ToIconAsync 缺陷管线）
        System.Drawing.Icon? trayIcon = null;
        try
        {
            var icoUri = new Uri("pack://application:,,,/Assets/clipora.ico");
            var streamInfo = Application.GetResourceStream(icoUri);
            if (streamInfo?.Stream is not null)
            {
                using (streamInfo.Stream)
                    trayIcon = new System.Drawing.Icon(streamInfo.Stream);
            }
        }
        catch (Exception ex)
        {
            TryWriteDiag($"托盘图标加载 ICO 失败（回退手绘字母 C）: {ex.GetType().Name}: {ex.Message}");
        }

        _trayIconResource = trayIcon;

        if (trayIcon is not null)
            _tray.Icon = trayIcon;
        else
            _tray.IconSource = new GeneratedIconSource
            {
                Text = "C",
                Foreground = Brushes.White,
                Background = new SolidColorBrush(Color.FromRgb(0x2F, 0x6F, 0xED)),
            };
        _tray.TrayMouseDoubleClick += (_, _) => ShowPanel();

        // 暂停态指示：托盘菜单勾选 + 托盘提示文字。无论从托盘还是设置页切换 Paused 都即时同步
        // （设置页经 SettingsService.Changed；菜单打开时再兜底刷新一次）。
        void SyncPausedIndicator()
        {
            bool paused = _settings?.Current.Paused ?? false;
            pause.IsChecked = paused;
            if (_tray is not null)
                _tray.ToolTipText = paused ? "Clipora 剪贴板（已暂停）" : "Clipora 剪贴板";
        }

        menu.Opened += (_, _) => SyncPausedIndicator();
        if (_settings is not null)
            _settings.Changed += (_, _) => Dispatcher.Invoke(SyncPausedIndicator);

        try
        {
            _tray.ForceCreate();
            SyncPausedIndicator(); // 初始状态（含启动即暂停）
            return TrayCreateResult.Success;
        }
        catch (Exception ex)
        {
            TryWriteDiag($"托盘创建失败: {ex.GetType().Name}: {ex.Message}");
            return TrayCreateResult.Failure;
        }
    }

    private static MenuItem CreateTrayMenuItem(string header, string glyph, bool isCheckable = false)
    {
        var icon = new TextBlock
        {
            Text = glyph,
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 12,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        icon.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");

        var item = new MenuItem
        {
            Header = header,
            Icon = icon,
            IsCheckable = isCheckable,
        };
        item.SetResourceReference(FrameworkElement.StyleProperty, "TrayMenuItem");
        return item;
    }

    /// <summary>
    /// 托盘创建失败后的有限次数重试（DispatcherTimer，3 次 × 3 秒）。
    /// 每次失败写诊断；全部失败后保持主窗口可用，托盘功能缺失但进程正常。
    /// </summary>
    private void StartTrayRetry()
    {
        if (_trayRetryTimer is not null)
            return; // 已在重试中

        _trayRetryTimer = new DispatcherTimer { Interval = TrayRetryInterval };
        _trayRetryTimer.Tick += (_, _) =>
        {
            _trayRetryCount++;
            try
            {
                _tray!.ForceCreate();
                // 重试成功：同步暂停指示并停止定时器
                Dispatcher.Invoke(() =>
                {
                    bool paused = _settings?.Current.Paused ?? false;
                    if (_tray is not null)
                        _tray.ToolTipText = paused ? "Clipora 剪贴板（已暂停）" : "Clipora 剪贴板";
                });
                _trayRetryTimer.Stop();
                TryWriteDiag($"托盘重试成功（第 {_trayRetryCount} 次）。");
            }
            catch (Exception ex)
            {
                TryWriteDiag($"托盘重试失败（第 {_trayRetryCount}/{MaxTrayRetries} 次）: {ex.GetType().Name}: {ex.Message}");
                if (_trayRetryCount >= MaxTrayRetries)
                {
                    _trayRetryTimer.Stop();
                    TryWriteDiag("托盘重试已达上限，主窗口保持可用，托盘功能缺失。");
                }
            }
        };
        _trayRetryTimer.Start();
    }

    private void ExitApp()
    {
        if (_window is not null)
            _window.AllowClose = true;
        Shutdown();
    }

    internal bool RequestStorageMigrationRestart(Guid migrationId, out string? error)
    {
        if (_storageMigrationRestart is null)
        {
            error = "存储迁移重启服务尚未初始化。";
            return false;
        }

        if (!_storageMigrationRestart.TryRequest(migrationId, out error))
            return false;

        if (_window is not null)
            _window.AllowClose = true;
        Shutdown();
        return true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _settings?.Save(); // 确保 DeferredSave 中的数据在退出前刷盘
        _trayRetryTimer?.Stop();
        _ocrProcessing?.Dispose();
        _floatingBar?.Close();
        _hotkey?.Dispose();
        _monitor?.Dispose();
        _foreground?.Dispose();
        _trayIconResource?.Dispose();
        _trayIconResource = null;
        _tray?.Dispose();
        _showEventRegistration?.Unregister(null);
        _showEventRegistration = null;
        _showEvent?.Dispose();
        _showEvent = null;
        _mutex?.Dispose();
        _mutex = null;
        base.OnExit(e);

        if (_storageMigrationRestart?.TryLaunchAfterExit(out string? error) == false
            && !string.IsNullOrWhiteSpace(error))
        {
            TryWriteDiag(error);
        }
    }

    private static bool IsReleaseBuild
    {
        get
        {
#if DEBUG
            return false;
#else
            return true;
#endif
        }
    }

    /// <summary>M6.1a: 清理超过 7 天的旧崩溃日志，避免 %TEMP% 残留堆积。</summary>
    private static void CleanLegacyCrashLog()
    {
        try
        {
            string crashPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "clipora-crash.txt");
            if (System.IO.File.Exists(crashPath))
            {
                DateTime lastWrite = System.IO.File.GetLastWriteTimeUtc(crashPath);
                if (DateTime.UtcNow - lastWrite > TimeSpan.FromDays(7))
                    System.IO.File.Delete(crashPath);
            }
        }
        catch { /* best-effort */ }
    }
}
