using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Clipora.Abstractions;
using Clipora.Models;
using Clipora.Services;

namespace Clipora.ViewModels;

/// <summary>存储迁移工作流错误分类（3a-3b）。</summary>
internal enum StorageMigrationWorkflowError
{
    None,
    EnqueueFailed,
    RestartFailed,
}

/// <summary>存储迁移工作流结果（3a-3b）。</summary>
internal sealed record StorageMigrationWorkflowResult(
    bool Succeeded,
    StorageMigrationWorkflowError Error,
    string? Detail);

internal enum CustomBackgroundError
{
    None,
    FileMissing,
    UnsupportedFormat,
    TooLarge,
    DecodeFailed,
    IoFailed,
}

internal sealed record CustomBackgroundApplyResult(
    bool Succeeded,
    CustomBackgroundError Error,
    string? Detail,
    long? SizeBytes = null);

/// <summary>常规设置页绑定层：每个属性 setter 即时保存到磁盘并通知 UI。</summary>
public sealed class SettingsViewModel : INotifyPropertyChanged
{
    internal const long CustomBackgroundMaxBytes = 67L * 1024 * 1024;
    private const int DefaultCustomBackgroundOpacity = 50;
    private const string BackgroundsDirectoryName = "backgrounds";

    private readonly ISettingsService _settings;
    private readonly IAutoStartService _autoStart;
    private readonly IStorageMigrationPlanner? _planner;
    private readonly Func<Guid, (bool Succeeded, string? Error)>? _restartDelegate;
    private readonly IRunningAppsProvider? _runningApps;
    private readonly Func<IReadOnlyList<HotkeyRegistration>>? _reRegisterHotkeys;
    private readonly IOcrService? _ocrService;
    private readonly IThemeService? _themeService;
    private readonly string _dataDirectory;
    private bool _canChangeDataDirectory;
    private bool _hasPendingEnqueue;
    private bool _restartFailed;

    private DispatcherTimer? _deferredSaveTimer;

    /// <summary>公开三参数构造：供既有调用/自检使用，默认迁移不可用。</summary>
    public SettingsViewModel(ISettingsService settings, IAutoStartService autoStart, string dataDirectory)
        : this(settings, autoStart, dataDirectory, null, false, null, null, null, null, null)
    {
    }

    internal SettingsViewModel(
        ISettingsService settings,
        IAutoStartService autoStart,
        string dataDirectory,
        IStorageMigrationPlanner? planner,
        bool canChangeDataDirectory,
        Func<Guid, (bool Succeeded, string? Error)>? restartDelegate)
        : this(settings, autoStart, dataDirectory, planner, canChangeDataDirectory, restartDelegate, null, null, null, null)
    {
    }

    internal SettingsViewModel(
        ISettingsService settings,
        IAutoStartService autoStart,
        string dataDirectory,
        IStorageMigrationPlanner? planner,
        bool canChangeDataDirectory,
        Func<Guid, (bool Succeeded, string? Error)>? restartDelegate,
        IRunningAppsProvider? runningApps)
        : this(settings, autoStart, dataDirectory, planner, canChangeDataDirectory, restartDelegate, runningApps, null, null, null)
    {
    }

    internal SettingsViewModel(
        ISettingsService settings,
        IAutoStartService autoStart,
        string dataDirectory,
        IStorageMigrationPlanner? planner,
        bool canChangeDataDirectory,
        Func<Guid, (bool Succeeded, string? Error)>? restartDelegate,
        IRunningAppsProvider? runningApps,
        Func<IReadOnlyList<HotkeyRegistration>>? reRegisterHotkeys,
        IOcrService? ocrService,
        IThemeService? themeService)
    {
        _settings = settings;
        _autoStart = autoStart;
        DataDirectory = Path.GetFullPath(dataDirectory);
        _dataDirectory = DataDirectory;
        _planner = planner;
        _canChangeDataDirectory = canChangeDataDirectory;
        _restartDelegate = restartDelegate;
        _runningApps = runningApps;
        _reRegisterHotkeys = reRegisterHotkeys;
        _ocrService = ocrService;
        _themeService = themeService;

        foreach (string excludedProcessName in _settings.Current.ExcludedApps)
        {
            string displayName = ResolveDisplayName(excludedProcessName);
            ExcludedApps.Add(new ExcludedAppViewModel(excludedProcessName, displayName));
        }

        RefreshColorPill();
        RefreshVisualThemePill();
    }

    /// <summary>M5.2：导入后刷新主列表的回调。</summary>
    internal Action? RefreshHistory { get; set; }

    public bool AutoPasteOnUse
    {
        get => _settings.Current.AutoPasteOnUse;
        set { _settings.Current.AutoPasteOnUse = value; SaveAndNotify(); }
    }

    public bool MergeDuplicates
    {
        get => _settings.Current.MergeDuplicates;
        set { _settings.Current.MergeDuplicates = value; SaveAndNotify(); }
    }

    public bool SilentStart
    {
        get => _settings.Current.SilentStart;
        set { _settings.Current.SilentStart = value; SaveAndNotify(); }
    }

    public MinimizeBehavior MinimizeTo
    {
        get => _settings.Current.MinimizeTo;
        set { _settings.Current.MinimizeTo = value; SaveAndNotify(); }
    }

    public CloseBehavior CloseTo
    {
        get => _settings.Current.CloseTo;
        set { _settings.Current.CloseTo = value; SaveAndNotify(); }
    }

    public bool ShowInTaskbar
    {
        get => _settings.Current.ShowInTaskbar;
        set { _settings.Current.ShowInTaskbar = value; SaveAndNotify(); }
    }

    public bool ShowBackToTop
    {
        get => _settings.Current.ShowBackToTop;
        set { _settings.Current.ShowBackToTop = value; SaveAndNotify(); }
    }

    /// <summary>当前数据目录（只读），由组合根在构造时传入。</summary>
    public string DataDirectory { get; }

    // —— 存储迁移设置（3a-3b） ——

    /// <summary>是否允许更改数据目录（绑定"更改…"按钮 IsEnabled）。</summary>
    public bool CanChangeDataDirectory => _canChangeDataDirectory && !_hasPendingEnqueue;

    /// <summary>数据目录迁移说明文案（绑定按钮下方描述）。</summary>
    public string DataDirectoryMigrationDescription
    {
        get
        {
            if (_restartFailed)
                return "迁移已排队，但无法自动重启。请手动退出并重新打开 Clipora。";
            if (_hasPendingEnqueue)
                return "迁移已排队，正在重启…";
            if (_canChangeDataDirectory)
                return "选择新的本地父目录，Clipora 将在重启后安全迁移";
            return "开发版数据目录固定为 .dev-data";
        }
    }

    /// <summary>预检：调用 planner.Plan 并透传结果。不可用时返回 Unavailable 且不调用 planner。</summary>
    internal StorageMigrationPlanResult TryPlan(string? selectedParent)
    {
        if (_planner is null || !CanChangeDataDirectory)
            return new StorageMigrationPlanResult(false, null, StorageMigrationPlanError.Unavailable, "迁移功能不可用。");
        return _planner.Plan(selectedParent);
    }

    /// <summary>入队 + 安全重启：Enqueue 成功后立即禁用按钮并请求重启。
    /// 重启成功则进程退出；重启失败则保留 pending、按钮保持禁用。</summary>
    internal StorageMigrationWorkflowResult TryEnqueueAndRestart(StorageMigrationPlan plan)
    {
        if (_planner is null || !CanChangeDataDirectory)
            return new StorageMigrationWorkflowResult(false, StorageMigrationWorkflowError.EnqueueFailed, "迁移功能不可用。");

        StorageMigrationEnqueueResult enqueueResult = _planner.Enqueue(plan);
        if (!enqueueResult.Succeeded)
            return new StorageMigrationWorkflowResult(false, StorageMigrationWorkflowError.EnqueueFailed, enqueueResult.Detail);

        // 成功入队后立即禁用按钮，防止重复入队
        _hasPendingEnqueue = true;
        _canChangeDataDirectory = false;
        OnPropertyChanged(nameof(CanChangeDataDirectory));
        OnPropertyChanged(nameof(DataDirectoryMigrationDescription));

        // 请求安全重启。入队已经成功时，任何后续故障都必须保留 pending 并明确要求手动重启。
        string? restartError = null;
        bool restartSucceeded = false;
        if (_restartDelegate is null)
        {
            restartError = "存储迁移重启服务不可用。";
        }
        else if (enqueueResult.MigrationId is not Guid migrationId || migrationId == Guid.Empty)
        {
            restartError = "迁移已入队，但返回的 MigrationId 无效。";
        }
        else
        {
            try
            {
                (restartSucceeded, restartError) = _restartDelegate(migrationId);
            }
            catch (Exception ex)
            {
                restartError = $"请求安全重启失败: {ex.Message}";
            }
        }

        if (!restartSucceeded)
        {
            _restartFailed = true;
            OnPropertyChanged(nameof(DataDirectoryMigrationDescription));
            string detail = "迁移已排队，但无法自动重启。请手动退出并重新打开 Clipora。";
            if (!string.IsNullOrWhiteSpace(restartError))
                detail += $"\n\n原因：{restartError}";
            return new StorageMigrationWorkflowResult(false, StorageMigrationWorkflowError.RestartFailed, detail);
        }

        return new StorageMigrationWorkflowResult(true, StorageMigrationWorkflowError.None, null);
    }

    /// <summary>
    /// 保存天数：1 / 3 / 7 / 30；0 = 永久。只接受合法离散值，非法值不改模型并通知 UI 回弹。
    /// 修改值不会立即触发清理；新值只由下次启动 / 6 小时周期读取。
    /// </summary>
    public int RetentionDays
    {
        get => _settings.Current.RetentionDays;
        set
        {
            if (value is not (0 or 1 or 3 or 7 or 30))
            {
                // 非法值：不改模型、不保存，仅通知 UI 回到 getter 值
                OnPropertyChanged();
                return;
            }

            if (value == _settings.Current.RetentionDays)
                return; // 同值不重复保存

            _settings.Current.RetentionDays = value;
            SaveAndNotify();
        }
    }

    /// <summary>
    /// 单条大小上限（MiB），范围 1–1024。setter 自动归一化（取整 + clamp）后写入字节。
    /// NaN / Infinity 拒绝并回弹；被取整/clamp 时仍通知 UI 显示归一化结果。
    /// </summary>
    public double MaxItemMegabytes
    {
        get
        {
            long b = _settings.Current.MaxItemBytes;
            if (b <= 0) return 1;
            return b / (1024d * 1024d);
        }
        set
        {
            // 拒绝 NaN 和无穷大
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                OnPropertyChanged();
                return;
            }

            // 归一化：AwayFromZero 取整 → clamp [1, 1024]
            long normalizedMiB = (long)Math.Round(value, MidpointRounding.AwayFromZero);
            normalizedMiB = Math.Clamp(normalizedMiB, 1, 1024);

            long newBytes;
            try
            {
                newBytes = checked(normalizedMiB * 1024L * 1024L);
            }
            catch (OverflowException)
            {
                newBytes = 1024L * 1024L * 1024L; // 极端防御：上限 1 GiB
            }

            if (newBytes == _settings.Current.MaxItemBytes)
            {
                // 归一化后字节值不变（或被取整/clamp）：仅通知 UI 显示归一化结果
                OnPropertyChanged();
                return;
            }

            _settings.Current.MaxItemBytes = newBytes;
            SaveAndNotify();
        }
    }

    /// <summary>
    /// 开机自启：真相源 = 注册表（<see cref="IAutoStartService"/>）。
    /// setter 调 TrySetEnabled；成功则同步 Current + 写盘；失败则不改开关并置错误提示。
    /// </summary>
    public bool AutoStart
    {
        get => _autoStart.IsEnabled();
        set
        {
            if (_autoStart.TrySetEnabled(value, out string? error))
            {
                _settings.Current.AutoStart = value;
                _settings.Save();
                AutoStartError = null;
            }
            else
            {
                AutoStartError = error;
                // 注册表写入失败：通知 UI 回弹（getter 读注册表，值未变）
            }

            OnPropertyChanged(nameof(AutoStart));
            OnPropertyChanged(nameof(AutoStartError));
            OnPropertyChanged(nameof(HasAutoStartError));
        }
    }

    public string? AutoStartError { get; private set; }

    public bool HasAutoStartError => !string.IsNullOrEmpty(AutoStartError);

    // —— OCR ——

    /// <summary>图片文字识别（OCR）。引擎不可用时整行禁用。</summary>
    public bool OcrEnabled
    {
        get => _settings.Current.OcrEnabled;
        set { _settings.Current.OcrEnabled = value; SaveAndNotify(); }
    }

    /// <summary>OCR 引擎是否可用（只读）。</summary>
    public bool OcrIsAvailable => _ocrService?.IsAvailable == true;

    /// <summary>引擎不可用时的内联提示文案。</summary>
    public string OcrUnavailableHint => !OcrIsAvailable
        ? "当前系统未安装可用的 OCR 语言包"
        : string.Empty;

    /// <summary>OCR 行是否可操作（引擎可用）。</summary>
    public bool OcrRowEnabled => OcrIsAvailable;

    // —— 外观 ——

    public string VisualTheme
    {
        get => _settings.Current.VisualTheme;
        set { _settings.Current.VisualTheme = value; SaveAndNotify(); RefreshVisualThemePill(); }
    }

    public void SetVisualTheme(string theme)
    {
        VisualTheme = theme;
        _themeService?.ApplyVisualTheme(theme);
    }

    public bool IsFluentSelected => VisualTheme == "Fluent";
    public bool IsLiquidGlassSelected => VisualTheme == "LiquidGlass";
    public bool IsCustomBackgroundSectionVisible => IsFluentSelected;
    public bool IsLiquidGlassTransparencySectionVisible => IsLiquidGlassSelected;

    private void RefreshVisualThemePill()
    {
        OnPropertyChanged(nameof(IsFluentSelected));
        OnPropertyChanged(nameof(IsLiquidGlassSelected));
        OnPropertyChanged(nameof(IsCustomBackgroundSectionVisible));
        OnPropertyChanged(nameof(IsLiquidGlassTransparencySectionVisible));
    }

    public string? CustomBackgroundPath => _settings.Current.CustomBackgroundPath;

    public bool HasCustomBackground => !string.IsNullOrWhiteSpace(CustomBackgroundPath);

    public string CustomBackgroundFileName =>
        HasCustomBackground ? Path.GetFileName(CustomBackgroundPath) ?? string.Empty : string.Empty;

    public string CustomBackgroundChooseText => HasCustomBackground ? "更换背景" : "选择背景";

    public string CustomBackgroundOpacityText => CustomBackgroundOpacity.ToString();

    public int CustomBackgroundOpacity
    {
        get => Math.Clamp(_settings.Current.CustomBackgroundOpacity, 0, 100);
        set
        {
            int normalized = Math.Clamp(value, 0, 100);
            if (_settings.Current.CustomBackgroundOpacity == normalized)
            {
                OnPropertyChanged();
                OnPropertyChanged(nameof(CustomBackgroundOpacityText));
                return;
            }

            _settings.Current.CustomBackgroundOpacity = normalized;
            DeferredSave();
            OnPropertyChanged();
            OnPropertyChanged(nameof(CustomBackgroundOpacitySlider));
            OnPropertyChanged(nameof(CustomBackgroundOpacityText));
        }
    }

    public double CustomBackgroundOpacitySlider
    {
        get => CustomBackgroundOpacity;
        set
        {
            int normalized = Math.Clamp((int)Math.Round(value, MidpointRounding.AwayFromZero), 0, 100);
            if (_settings.Current.CustomBackgroundOpacity == normalized)
                return;

            _settings.Current.CustomBackgroundOpacity = normalized;
            DeferredSave();
            OnPropertyChanged(nameof(CustomBackgroundOpacity));
            OnPropertyChanged(nameof(CustomBackgroundOpacityText));
        }
    }

    public string LiquidGlassTransparencyText => LiquidGlassTransparency.ToString();

    public int LiquidGlassTransparency
    {
        get => Math.Clamp(_settings.Current.LiquidGlassTransparency, 0, 100);
        set
        {
            int normalized = Math.Clamp(value, 0, 100);
            if (_settings.Current.LiquidGlassTransparency == normalized)
            {
                OnPropertyChanged();
                OnPropertyChanged(nameof(LiquidGlassTransparencyText));
                return;
            }

            _settings.Current.LiquidGlassTransparency = normalized;
            SaveAndNotify();
            _themeService?.ApplyLiquidGlassTransparency(normalized);
            OnPropertyChanged(nameof(LiquidGlassTransparencySlider));
            OnPropertyChanged(nameof(LiquidGlassTransparencyText));
        }
    }

    public double LiquidGlassTransparencySlider
    {
        get => LiquidGlassTransparency;
        set
        {
            int normalized = Math.Clamp((int)Math.Round(value, MidpointRounding.AwayFromZero), 0, 100);
            if (_settings.Current.LiquidGlassTransparency == normalized)
                return;

            _settings.Current.LiquidGlassTransparency = normalized;
            DeferredSave();
            _themeService?.ApplyLiquidGlassTransparency(normalized);
            OnPropertyChanged(nameof(LiquidGlassTransparency));
            OnPropertyChanged(nameof(LiquidGlassTransparencyText));
        }
    }

    /// <summary>
    /// 延迟存盘：拖动滑条时不阻塞 UI 线程写磁盘，松手 500ms 后存一次。
    /// </summary>
    private void DeferredSave()
    {
        if (_deferredSaveTimer is null)
        {
            _deferredSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _deferredSaveTimer.Tick += (_, _) =>
            {
                _deferredSaveTimer.Stop();
                _settings.Save();
            };
        }
        _deferredSaveTimer.Stop();
        _deferredSaveTimer.Start();
    }

    internal string? CustomBackgroundFullPath => ResolveCustomBackgroundFullPath();

    internal CustomBackgroundApplyResult SetCustomBackground(string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            return new CustomBackgroundApplyResult(false, CustomBackgroundError.FileMissing, "文件不存在。");

        string extension = Path.GetExtension(sourcePath).ToLowerInvariant();
        if (extension is not ".jpg" and not ".jpeg" and not ".png")
            return new CustomBackgroundApplyResult(false, CustomBackgroundError.UnsupportedFormat, "仅支持 JPG 和 PNG 图片。");

        FileInfo sourceInfo;
        try
        {
            sourceInfo = new FileInfo(sourcePath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return new CustomBackgroundApplyResult(false, CustomBackgroundError.IoFailed, ex.Message);
        }

        if (sourceInfo.Length > CustomBackgroundMaxBytes)
            return new CustomBackgroundApplyResult(false, CustomBackgroundError.TooLarge, "背景图片超过上限。", sourceInfo.Length);

        if (!CanDecodeImage(sourcePath))
            return new CustomBackgroundApplyResult(false, CustomBackgroundError.DecodeFailed, "图片无法解码。");

        string managedDirectory = Path.Combine(_dataDirectory, BackgroundsDirectoryName);
        string managedFileName = extension == ".jpeg" ? "custom-bg.jpg" : $"custom-bg{extension}";
        string destinationPath = Path.Combine(managedDirectory, managedFileName);
        string relativePath = Path.Combine(BackgroundsDirectoryName, managedFileName);
        bool hadBackground = HasCustomBackground;

        try
        {
            Directory.CreateDirectory(managedDirectory);
            if (!string.Equals(Path.GetFullPath(sourcePath), Path.GetFullPath(destinationPath), StringComparison.OrdinalIgnoreCase))
                File.Copy(sourcePath, destinationPath, overwrite: true);

            DeleteOtherManagedBackgrounds(managedDirectory, destinationPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or PathTooLongException)
        {
            return new CustomBackgroundApplyResult(false, CustomBackgroundError.IoFailed, ex.Message);
        }

        _settings.Current.CustomBackgroundPath = relativePath;
        if (!hadBackground || _settings.Current.CustomBackgroundOpacity <= 0)
            _settings.Current.CustomBackgroundOpacity = DefaultCustomBackgroundOpacity;
        _settings.Save();
        RefreshCustomBackgroundState();
        return new CustomBackgroundApplyResult(true, CustomBackgroundError.None, null, sourceInfo.Length);
    }

    internal void ClearCustomBackground()
    {
        string? path = ResolveCustomBackgroundFullPath();
        _settings.Current.CustomBackgroundPath = null;
        _settings.Current.CustomBackgroundOpacity = 0;
        _settings.Save();
        if (!string.IsNullOrWhiteSpace(path))
        {
            try { File.Delete(path); } catch { }
        }
        RefreshCustomBackgroundState();
    }

    private string? ResolveCustomBackgroundFullPath()
    {
        string? relative = _settings.Current.CustomBackgroundPath;
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative))
            return null;

        try
        {
            string fullPath = Path.GetFullPath(Path.Combine(_dataDirectory, relative));
            string root = Path.GetFullPath(_dataDirectory);
            if (!fullPath.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
            return fullPath;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static bool CanDecodeImage(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            BitmapDecoder.Create(
                stream,
                BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void DeleteOtherManagedBackgrounds(string managedDirectory, string keepPath)
    {
        foreach (string file in Directory.EnumerateFiles(managedDirectory, "custom-bg.*"))
        {
            if (string.Equals(Path.GetFullPath(file), Path.GetFullPath(keepPath), StringComparison.OrdinalIgnoreCase))
                continue;
            try { File.Delete(file); } catch { }
        }
    }

    private void RefreshCustomBackgroundState()
    {
        OnPropertyChanged(nameof(CustomBackgroundPath));
        OnPropertyChanged(nameof(CustomBackgroundFullPath));
        OnPropertyChanged(nameof(HasCustomBackground));
        OnPropertyChanged(nameof(CustomBackgroundFileName));
        OnPropertyChanged(nameof(CustomBackgroundChooseText));
        OnPropertyChanged(nameof(CustomBackgroundOpacity));
        OnPropertyChanged(nameof(CustomBackgroundOpacitySlider));
        OnPropertyChanged(nameof(CustomBackgroundOpacityText));
    }

    public string ColorMode
    {
        get => _settings.Current.ColorMode;
        private set { _settings.Current.ColorMode = value; SaveAndNotify(); RefreshColorPill(); }
    }

    public void SetColorMode(string mode)
    {
        ColorMode = mode;
        _themeService?.ApplyColorMode(mode);
    }

    public bool IsColorSystemSelected => ColorMode == "System";
    public bool IsColorLightSelected => ColorMode == "Light";
    public bool IsColorDarkSelected => ColorMode == "Dark";

    private void RefreshColorPill()
    {
        OnPropertyChanged(nameof(IsColorSystemSelected));
        OnPropertyChanged(nameof(IsColorLightSelected));
        OnPropertyChanged(nameof(IsColorDarkSelected));
    }

    // —— 隐私 ——

    /// <summary>暂停记录：暂停后不再自动捕获剪贴板（手动拖入仍可保存）。</summary>
    public bool Paused
    {
        get => _settings.Current.Paused;
        set { _settings.Current.Paused = value; SaveAndNotify(); }
    }

    /// <summary>应用排除名单的显示集合（ProcessName + DisplayName）。</summary>
    public ObservableCollection<ExcludedAppViewModel> ExcludedApps { get; } = new();

    /// <summary>供"添加应用…"选择器枚举当前运行中的用户应用。</summary>
    internal IRunningAppsProvider? RunningAppsProvider => _runningApps;

    /// <summary>添加应用排除：归一化 processName→lowercase；已存在（OrdinalIgnoreCase）则忽略。</summary>
    internal void AddExclusion(string processName, string displayName)
    {
        string normalized = (processName ?? throw new ArgumentNullException(nameof(processName))).ToLowerInvariant();

        // 已存在则忽略
        if (_settings.Current.ExcludedApps.Contains(normalized, StringComparer.OrdinalIgnoreCase))
            return;

        _settings.Current.ExcludedApps.Add(normalized);
        ExcludedApps.Add(new ExcludedAppViewModel(normalized, displayName));
        _settings.Save();
    }

    /// <summary>移除应用排除：从 settings 和 ObservableCollection 中同步移除。</summary>
    internal void RemoveExclusion(ExcludedAppViewModel item)
    {
        ArgumentNullException.ThrowIfNull(item);

        _settings.Current.ExcludedApps.RemoveAll(
            x => string.Equals(x, item.ProcessName, StringComparison.OrdinalIgnoreCase));
        ExcludedApps.Remove(item);
        _settings.Save();
    }

    /// <summary>按归一化进程名解析当前显示名（若进程仍在运行则取友好名，否则直接返回进程名）。</summary>
    private string ResolveDisplayName(string normalizedProcessName)
    {
        if (_runningApps is not null)
        {
            foreach (var app in _runningApps.GetUserApps())
            {
                if (string.Equals(app.ProcessName, normalizedProcessName, StringComparison.OrdinalIgnoreCase))
                    return app.DisplayName;
            }
        }

        return normalizedProcessName;
    }

    // —— 快捷键（4.5.2b） ——

    /// <summary>打开面板快捷键显示串（空=未绑定）。</summary>
    public string HotkeyOpenPanelDisplay =>
        HotkeyGesture.TryParse(_settings.Current.HotkeyOpenPanel, out var g) ? g.Format() : string.Empty;

    /// <summary>纯文本粘贴快捷键显示串。</summary>
    public string HotkeyPastePlainDisplay =>
        HotkeyGesture.TryParse(_settings.Current.HotkeyPastePlain, out var g) ? g.Format() : string.Empty;

    /// <summary>顺序粘贴快捷键显示串。</summary>
    public string HotkeySequentialPasteDisplay =>
        HotkeyGesture.TryParse(_settings.Current.HotkeySequentialPaste, out var g) ? g.Format() : string.Empty;

    private string? _hotkeyOpenPanelError;
    public string? HotkeyOpenPanelError
    {
        get => _hotkeyOpenPanelError;
        set { _hotkeyOpenPanelError = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasHotkeyOpenPanelError)); }
    }

    private string? _hotkeyPastePlainError;
    public string? HotkeyPastePlainError
    {
        get => _hotkeyPastePlainError;
        set { _hotkeyPastePlainError = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasHotkeyPastePlainError)); }
    }

    private string? _hotkeySequentialPasteError;
    public string? HotkeySequentialPasteError
    {
        get => _hotkeySequentialPasteError;
        set { _hotkeySequentialPasteError = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasHotkeySequentialPasteError)); }
    }

    public bool HasHotkeyOpenPanelError => !string.IsNullOrEmpty(HotkeyOpenPanelError);
    public bool HasHotkeyPastePlainError => !string.IsNullOrEmpty(HotkeyPastePlainError);
    public bool HasHotkeySequentialPasteError => !string.IsNullOrEmpty(HotkeySequentialPasteError);

    /// <summary>提交新快捷键绑定：校验→写入设置→重注册→冲突检测→回滚或更新显示。</summary>
    internal void SubmitHotkeyBinding(HotkeyAction action, HotkeyGesture gesture)
    {
        string propertyName = action switch
        {
            HotkeyAction.PastePlain => nameof(HotkeyPastePlainDisplay),
            HotkeyAction.SequentialPaste => nameof(HotkeySequentialPasteDisplay),
            _ => nameof(HotkeyOpenPanelDisplay),
        };

        string formatted = gesture.Format();
        string? previousValue = GetSettingValue(action);

        // 写入设置
        SetSettingValue(action, formatted);
        _settings.Save();

        // 重注册所有热键
        IReadOnlyList<HotkeyRegistration>? results = _reRegisterHotkeys?.Invoke();

        // 检查该动作注册是否失败
        bool systemConflict = false;
        if (results is not null)
        {
            foreach (var reg in results)
            {
                if (reg.Action == action && !reg.Succeeded)
                {
                    systemConflict = true;
                    break;
                }
            }
        }

        // 检查应用内重复
        bool appDuplicate = false;
        var currentGestures = ReadCurrentGestures();
        var duplicates = HotkeyConflictChecker.FindDuplicates(currentGestures);
        if (duplicates.Contains(action))
            appDuplicate = true;

        // 冲突处理
        if (systemConflict || appDuplicate)
        {
            string msg = systemConflict
                ? "该快捷键被系统占用，请改用其它组合"
                : "与其它动作冲突，请改用其它组合";

            // 通过 property setter 设错（自动触发 HasXxxError 通知）
            switch (action)
            {
                case HotkeyAction.OpenPanel: HotkeyOpenPanelError = msg; break;
                case HotkeyAction.PastePlain: HotkeyPastePlainError = msg; break;
                case HotkeyAction.SequentialPaste: HotkeySequentialPasteError = msg; break;
            }

            // 回滚
            SetSettingValue(action, previousValue ?? string.Empty);
            _settings.Save();
            _reRegisterHotkeys?.Invoke();
        }
        else
        {
            // 清除该动作错误
            switch (action)
            {
                case HotkeyAction.OpenPanel: HotkeyOpenPanelError = null; break;
                case HotkeyAction.PastePlain: HotkeyPastePlainError = null; break;
                case HotkeyAction.SequentialPaste: HotkeySequentialPasteError = null; break;
            }
        }

        OnPropertyChanged(propertyName);
    }

    /// <summary>恢复某个动作为默认快捷键。</summary>
    internal void ResetHotkeyToDefault(HotkeyAction action)
    {
        string defaultValue = action switch
        {
            HotkeyAction.OpenPanel => "Alt+V",
            HotkeyAction.PastePlain => "Ctrl+Shift+V",
            HotkeyAction.SequentialPaste => string.Empty,
            _ => string.Empty,
        };

        SetSettingValue(action, defaultValue);
        _settings.Save();
        _reRegisterHotkeys?.Invoke();

        // 重注册后重新计算冲突，清除所有已解除冲突的动作错误
        var currentGestures = ReadCurrentGestures();
        var currentDuplicates = new HashSet<HotkeyAction>(HotkeyConflictChecker.FindDuplicates(currentGestures));

        foreach (var checkAction in (HotkeyAction[])Enum.GetValues(typeof(HotkeyAction)))
        {
            string? currentError = checkAction switch
            {
                HotkeyAction.OpenPanel => HotkeyOpenPanelError,
                HotkeyAction.PastePlain => HotkeyPastePlainError,
                HotkeyAction.SequentialPaste => HotkeySequentialPasteError,
                _ => null,
            };

            // 清除已不在冲突列表中的动作错误
            if (currentError is not null && !currentDuplicates.Contains(checkAction))
            {
                switch (checkAction)
                {
                    case HotkeyAction.OpenPanel: HotkeyOpenPanelError = null; break;
                    case HotkeyAction.PastePlain: HotkeyPastePlainError = null; break;
                    case HotkeyAction.SequentialPaste: HotkeySequentialPasteError = null; break;
                }
            }
        }

        OnPropertyChanged(nameof(HotkeyOpenPanelDisplay));
        OnPropertyChanged(nameof(HotkeyPastePlainDisplay));
        OnPropertyChanged(nameof(HotkeySequentialPasteDisplay));
    }

    private string? GetSettingValue(HotkeyAction action) => action switch
    {
        HotkeyAction.OpenPanel => _settings.Current.HotkeyOpenPanel,
        HotkeyAction.PastePlain => _settings.Current.HotkeyPastePlain,
        HotkeyAction.SequentialPaste => _settings.Current.HotkeySequentialPaste,
        _ => null,
    };

    private void SetSettingValue(HotkeyAction action, string value)
    {
        switch (action)
        {
            case HotkeyAction.OpenPanel: _settings.Current.HotkeyOpenPanel = value; break;
            case HotkeyAction.PastePlain: _settings.Current.HotkeyPastePlain = value; break;
            case HotkeyAction.SequentialPaste: _settings.Current.HotkeySequentialPaste = value; break;
        }
    }

    private Dictionary<HotkeyAction, HotkeyGesture> ReadCurrentGestures()
    {
        var dict = new Dictionary<HotkeyAction, HotkeyGesture>();
        if (HotkeyGesture.TryParse(_settings.Current.HotkeyOpenPanel, out var openG))
            dict[HotkeyAction.OpenPanel] = openG;
        if (HotkeyGesture.TryParse(_settings.Current.HotkeyPastePlain, out var pasteG))
            dict[HotkeyAction.PastePlain] = pasteG;
        if (HotkeyGesture.TryParse(_settings.Current.HotkeySequentialPaste, out var seqG))
            dict[HotkeyAction.SequentialPaste] = seqG;
        return dict;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void SaveAndNotify([CallerMemberName] string? propertyName = null)
    {
        _settings.Save();
        OnPropertyChanged(propertyName);
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
