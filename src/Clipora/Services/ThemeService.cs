using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using Clipora.Abstractions;

namespace Clipora.Services;

/// <summary>
/// WPF-UI implementation for color mode and visual theme switching.
/// </summary>
public sealed class ThemeService : IThemeService
{
    internal const int DefaultLiquidGlassTransparency = 55;
    internal const int MinEffectiveTransparency = 30;
    internal const int MaxEffectiveTransparency = 85;
    internal const string DynamicGlassMarkerKey = "Clipora.LiquidGlass.DynamicOverlay";
    internal const string WindowOverlayKey = "LiquidGlassWindowOverlayBrush";

    /// <summary>
    /// Alpha ceiling for the application‑background overlay that sits between the DWM Acrylic backdrop
    /// and WPF content.  At slider=0 this value is used (most opaque ⇢ window looks solid);
    /// at slider=100 the overlay is fully transparent (Acrylic fully visible).
    /// Tune this single constant to adjust the "solid‑to‑see‑through" range.
    /// Set to 0 to revert to the previous always‑transparent background behaviour.
    /// </summary>
    internal const byte MaxBackgroundOverlayAlpha = 0x66; // 40 % — 回退到 0xC0 即可恢复原行为

    private Window? _window;
    private string _colorMode = "System";
    private string _visualTheme = "Fluent";
    private int _liquidGlassTransparency = DefaultLiquidGlassTransparency;
    private ApplicationTheme _cachedEffectiveTheme;
    private bool _hasCachedEffectiveTheme;

    private ResourceDictionary? _installedGlass;
    private bool _subscribedThemeChanged;

    public WindowBackdropType CurrentBackdrop =>
        _visualTheme == "LiquidGlass" ? WindowBackdropType.Acrylic : WindowBackdropType.Mica;

    public void AttachWindow(Window window)
    {
        _window = window;
        ApplyAttachedWindowBackdrop();
        if (window.IsLoaded)
            ApplyWatcher();
        else
            window.Loaded += OnWindowLoaded;
    }

    private void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is Window window)
            window.Loaded -= OnWindowLoaded;
        ApplyWatcher();
    }

    public void ApplyColorMode(string mode)
    {
        _colorMode = mode;
        ApplyCurrent();
    }

    public void ApplyVisualTheme(string theme)
    {
        _visualTheme = theme == "LiquidGlass" ? "LiquidGlass" : "Fluent";
        ApplyCurrent();
    }

    public void ApplyLiquidGlassTransparency(int value)
    {
        _liquidGlassTransparency = Math.Clamp(value, 0, 100);

        if (_visualTheme != "LiquidGlass")
            return;

        // 轻路径：仅重新生成玻璃覆盖字典。跳过 ApplyCurrent() 中的
        // ApplicationThemeManager.Apply / ApplyWatcher / 注册表读取——通透度变化时这些都不需要。
        ApplicationTheme theme = _hasCachedEffectiveTheme
            ? _cachedEffectiveTheme
            : GetEffectiveTheme();
        SyncGlassOverride(theme);
    }

    private ApplicationTheme GetEffectiveTheme() =>
        _colorMode switch
        {
            "Light" => ApplicationTheme.Light,
            "Dark" => ApplicationTheme.Dark,
            _ => IsSystemDarkTheme() ? ApplicationTheme.Dark : ApplicationTheme.Light,
        };

    private void ApplyCurrent()
    {
        ApplicationTheme theme = GetEffectiveTheme();
        _cachedEffectiveTheme = theme;
        _hasCachedEffectiveTheme = true;

        ApplicationThemeManager.Apply(theme, CurrentBackdrop, true);
        ApplyAttachedWindowBackdrop();
        SyncGlassOverride(theme);
        EnsureThemeChangedSubscription();
        ApplyWatcher();
    }

    private void ApplyAttachedWindowBackdrop()
    {
        if (_window is FluentWindow fluentWindow)
            fluentWindow.WindowBackdropType = CurrentBackdrop;
    }

    private void ApplyWatcher()
    {
        if (_window is null || !_window.IsLoaded)
            return;

        if (_colorMode is not "Light" and not "Dark")
            SystemThemeWatcher.Watch(_window, CurrentBackdrop, true);
        else
            SystemThemeWatcher.UnWatch(_window);
    }

    private void SyncGlassOverride(ApplicationTheme theme)
    {
        Application? app = Application.Current;
        if (app is null)
            return;

        var dicts = app.Resources.MergedDictionaries;

        // 先 Add 新字典（若需要），再 Remove 旧字典。
        // WPF 从 MergedDictionaries 末尾向前查找，先 Add 意味着新值立刻生效，
        // 再 Remove 旧字典时不存在 DynamicResource 回退间隙，消除闪烁。
        ResourceDictionary? glass = null;
        if (_visualTheme == "LiquidGlass")
        {
            glass = CreateLiquidGlassDictionary(
                theme == ApplicationTheme.Dark,
                _liquidGlassTransparency);
            dicts.Add(glass);
        }

        if (_installedGlass is not null)
        {
            dicts.Remove(_installedGlass);
            _installedGlass = null;
        }

        if (glass is not null)
            _installedGlass = glass;
    }

    private void EnsureThemeChangedSubscription()
    {
        if (_subscribedThemeChanged)
            return;
        ApplicationThemeManager.Changed += OnApplicationThemeChanged;
        _subscribedThemeChanged = true;
    }

    private void OnApplicationThemeChanged(ApplicationTheme currentTheme, System.Windows.Media.Color systemAccent)
        => SyncGlassOverride(currentTheme);

    private static bool IsSystemDarkTheme()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
    }

    internal static double EffectiveLiquidGlassTransparency(int value)
        => MinEffectiveTransparency + Math.Clamp(value, 0, 100) * ((MaxEffectiveTransparency - MinEffectiveTransparency) / 100d);

    internal static ResourceDictionary CreateLiquidGlassDictionary(bool dark, int value)
    {
        double t = Math.Clamp(value, 0, 100) / 100d;

        // ApplicationBackgroundBrush 作为 WPF‑层遮罩坐落在 DWM Acrylic 之上。
        // 更高滑块 → 更低 alpha → 更多 Acrylic 透出。
        byte bgAlpha = (byte)Math.Round(
            MaxBackgroundOverlayAlpha * (100.0 - Math.Clamp(value, 0, 100)) / 100.0,
            MidpointRounding.AwayFromZero);

        var dict = new ResourceDictionary
        {
            [DynamicGlassMarkerKey] = true,
            ["ApplicationBackgroundBrush"] = Brush(bgAlpha.ToString("X2"), dark ? "000000" : "FFFFFF"),
            [WindowOverlayKey] = Brush(bgAlpha.ToString("X2"), dark ? "000000" : "FFFFFF"),
        };

        if (dark)
            PopulateDarkGlassBrushes(dict, t);
        else
            PopulateLightGlassBrushes(dict, t);

        return dict;
    }

    private static void PopulateLightGlassBrushes(ResourceDictionary dict, double t)
    {
        dict["CardBackgroundFillColorDefaultBrush"] = Brush(Alpha(0xCC, 0x6F, t), "FFFFFF");
        dict["CardBackgroundFillColorSecondaryBrush"] = Brush(Alpha(0xB3, 0x56, t), "FFFFFF");
        dict["CardStrokeColorDefaultBrush"] = Gradient("#F2FFFFFF", "#4DFFFFFF");
        dict["CardStrokeColorDefaultSolidBrush"] = Brush("#CC", "FFFFFF");

        dict["ControlFillColorDefaultBrush"] = Brush(Alpha(0x99, 0x54, t), "FFFFFF");
        dict["ControlFillColorSecondaryBrush"] = Brush(Alpha(0xB2, 0x6D, t), "FFFFFF");
        dict["ControlFillColorTertiaryBrush"] = Brush(Alpha(0x80, 0x39, t), "FFFFFF");
        dict["ControlFillColorDisabledBrush"] = Brush("#2E", "FFFFFF");

        dict["SubtleFillColorSecondaryBrush"] = Brush("#38", "FFFFFF");
        dict["SubtleFillColorTertiaryBrush"] = Brush("#24", "FFFFFF");

        dict["SolidBackgroundFillColorBaseBrush"] = Brush("FF", "FFFFFF");
        dict["SolidBackgroundFillColorSecondaryBrush"] = Brush(Alpha(0xE0, 0xBE, t), "FFFFFF");
        dict["SolidBackgroundFillColorTertiaryBrush"] = Brush(Alpha(0xD6, 0xAA, t), "FFFFFF");

        dict["LayerFillColorDefaultBrush"] = Brush(Alpha(0xB8, 0x5F, t), "FFFFFF");
        dict["LayerFillColorAltBrush"] = Brush(Alpha(0x80, 0x39, t), "FFFFFF");

        dict["ControlStrokeColorDefaultBrush"] = Brush("#59", "FFFFFF");
        dict["ControlStrokeColorSecondaryBrush"] = Brush("#2E", "FFFFFF");
    }

    private static void PopulateDarkGlassBrushes(ResourceDictionary dict, double t)
    {
        const string surface = "32323A";
        dict["CardBackgroundFillColorDefaultBrush"] = Brush(Alpha(0x85, 0x3B, t), surface);
        dict["CardBackgroundFillColorSecondaryBrush"] = Brush(Alpha(0x66, 0x25, t), surface);
        dict["CardStrokeColorDefaultBrush"] = Gradient("#5CFFFFFF", "#14FFFFFF");
        dict["CardStrokeColorDefaultSolidBrush"] = Brush("#3D", "FFFFFF");

        dict["ControlFillColorDefaultBrush"] = Brush(Alpha(0x4C, 0x16, t), "FFFFFF");
        dict["ControlFillColorSecondaryBrush"] = Brush(Alpha(0x60, 0x25, t), "FFFFFF");
        dict["ControlFillColorTertiaryBrush"] = Brush(Alpha(0x3D, 0x0F, t), "FFFFFF");
        dict["ControlFillColorDisabledBrush"] = Brush("#14", "FFFFFF");

        dict["SubtleFillColorSecondaryBrush"] = Brush("#29", "FFFFFF");
        dict["SubtleFillColorTertiaryBrush"] = Brush("#1A", "FFFFFF");

        dict["SolidBackgroundFillColorBaseBrush"] = Brush("FF", "2A2A30");
        dict["SolidBackgroundFillColorSecondaryBrush"] = Brush(Alpha(0xBF, 0x8F, t), "222227");
        dict["SolidBackgroundFillColorTertiaryBrush"] = Brush(Alpha(0xB5, 0x80, t), "1C1C20");

        dict["LayerFillColorDefaultBrush"] = Brush(Alpha(0x66, 0x25, t), surface);
        dict["LayerFillColorAltBrush"] = Brush(Alpha(0x45, 0x0F, t), surface);

        dict["ControlStrokeColorDefaultBrush"] = Brush("#38", "FFFFFF");
        dict["ControlStrokeColorSecondaryBrush"] = Brush("#1F", "FFFFFF");
    }

    private static string Alpha(byte from, byte to, double t)
        => ((byte)Math.Round(from + (to - from) * t, MidpointRounding.AwayFromZero)).ToString("X2");

    private static SolidColorBrush Brush(string alpha, string rgb)
    {
        alpha = alpha.TrimStart('#');
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString($"#{alpha}{rgb}")!);
        brush.Freeze();
        return brush;
    }

    private static LinearGradientBrush Gradient(string top, string bottom)
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0.5, 0),
            EndPoint = new Point(0.5, 1),
        };
        brush.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString(top)!, 0));
        brush.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString(bottom)!, 1));
        brush.Freeze();
        return brush;
    }
}
