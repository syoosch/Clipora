namespace Clipora.Abstractions;

/// <summary>
/// 主题与外观服务。
/// </summary>
public interface IThemeService
{
    /// <summary>可用界面主题风格。</summary>
    static readonly string[] VisualThemes = { "Fluent", "LiquidGlass" };

    /// <summary>应用界面颜色：System / Light / Dark。</summary>
    void ApplyColorMode(string mode);

    /// <summary>应用界面主题风格：Fluent / LiquidGlass。</summary>
    void ApplyVisualTheme(string theme);

    /// <summary>液态玻璃通透度，用户范围 0-100；仅 LiquidGlass 主题使用。</summary>
    void ApplyLiquidGlassTransparency(int value);
}
