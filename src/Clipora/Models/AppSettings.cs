using System.Collections.Generic;

namespace Clipora.Models;

/// <summary>最小化按钮的行为。</summary>
public enum MinimizeBehavior { Tray, FloatingBall }

/// <summary>关闭（✕）按钮的行为。</summary>
public enum CloseBehavior { Tray, FloatingBall, Exit }

/// <summary>主面板排版模式。</summary>
public enum LayoutMode { Normal, Compact }

/// <summary>
/// 应用设置（持久化为数据目录下的 settings.json）。默认值对应已确认决策。
/// </summary>
public sealed class AppSettings
{
    // —— 保留与容量 ——
    /// <summary>保存天数：1 / 3 / 7 / 30；0 = 永久。置顶项不受影响。</summary>
    public int RetentionDays { get; set; } = 3;

    /// <summary>单条最大字节数，超过则不存入并提示。默认 25MB。</summary>
    public long MaxItemBytes { get; set; } = 25L * 1024 * 1024;

    /// <summary>重复内容合并（同内容刷新到最前，而非新增）。</summary>
    public bool MergeDuplicates { get; set; } = true;

    // —— 启动与窗口形态 ——
    public bool AutoStart { get; set; }
    public bool SilentStart { get; set; }
    public MinimizeBehavior MinimizeTo { get; set; } = MinimizeBehavior.FloatingBall;
    public CloseBehavior CloseTo { get; set; } = CloseBehavior.Tray;
    public bool AlwaysOnTop { get; set; }

    /// <summary>主面板打开时是否在任务栏显示程序按钮。默认显示。</summary>
    public bool ShowInTaskbar { get; set; } = true;

    /// <summary>主面板列表向上滚动时是否浮出「回到顶部」按钮。默认开启。</summary>
    public bool ShowBackToTop { get; set; } = true;

    /// <summary>主面板上次正常状态下的尺寸与位置；null 表示使用默认居中布局。</summary>
    public double? WindowWidth { get; set; }
    public double? WindowHeight { get; set; }
    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }

    // —— 粘贴 ——
    /// <summary>点击卡片后是否自动粘贴回原窗口（失败回退仅复制）。</summary>
    public bool AutoPasteOnUse { get; set; } = true;

    // —— 快捷键（均可改；注册失败做冲突检测）——
    public string HotkeyOpenPanel { get; set; } = "Alt+V";
    public string HotkeyPastePlain { get; set; } = "Ctrl+Shift+V";
    public string? HotkeySequentialPaste { get; set; }

    // —— 外观 ——
    /// <summary>界面主题风格：Fluent / LiquidGlass。</summary>
    public string VisualTheme { get; set; } = "Fluent";

    /// <summary>界面颜色：System / Light / Dark。</summary>
    public string ColorMode { get; set; } = "System";

    /// <summary>强调色（#RRGGBB）；null = 跟随系统。</summary>
    public string? AccentColor { get; set; }

    /// <summary>Fluent 主题自定义背景图片，相对数据目录路径；null = 不使用。</summary>
    public string? CustomBackgroundPath { get; set; }

    /// <summary>Fluent 自定义背景透明度百分比，范围 0-100；首次选择默认 50。</summary>
    public int CustomBackgroundOpacity { get; set; } = 50;

    /// <summary>液态玻璃通透度，用户可见范围 0-100；内部映射到安全通透区间。</summary>
    public int LiquidGlassTransparency { get; set; } = 55;

    /// <summary>界面语言，默认中文。</summary>
    public string Language { get; set; } = "zh-CN";

    public LayoutMode Layout { get; set; } = LayoutMode.Normal;

    /// <summary>[已废弃] 旧的 ThemeMode 字段，迁移到 ColorMode。</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string ThemeMode { get; set; } = "System";

    // —— OCR ——
    /// <summary>图片文字识别（OCR），本地识别截图文字使其可被搜索（不联网）。默认开启。</summary>
    public bool OcrEnabled { get; set; } = true;

    /// <summary>历史图片回填是否已完成（内部标记，非用户可见）。</summary>
    public bool OcrBackfillCompleted { get; set; }

    // —— 隐私 ——
    /// <summary>暂停记录。</summary>
    public bool Paused { get; set; }

    /// <summary>不记录的应用名单。</summary>
    public List<string> ExcludedApps { get; set; } = new();

    /// <summary>本地数据库加密（可选）。</summary>
    public bool EncryptDatabase { get; set; }

    // —— 存储位置 ——
    // DataDirectory 已移除（M4.2.2d）：Root 真相唯一来源为 AppPaths，由 StorageLocationService 按
    // override > CLIPORA_DATA_DIR > HKCU locator > %LOCALAPPDATA%\Clipora 解析。
    // 旧 settings.json 中的 DataDirectory 字段由 System.Text.Json 在反序列化时自动忽略。
    [System.Text.Json.Serialization.JsonIgnore]
    public string? DataDirectory { get; set; }
}
