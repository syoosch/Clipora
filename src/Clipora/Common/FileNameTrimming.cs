using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace Clipora.Common;

/// <summary>
/// 给单行 <see cref="TextBlock"/> 做"保留扩展名"的省略：宽度不够时省略号打在文件名主体，
/// 末尾的扩展名（如 <c>.md</c>）始终可见。用法：<c>common:FileNameTrimming.FileName="{Binding Preview}"</c>。
/// 按当前字体与实际宽度精确测量，随尺寸变化自动重算。
/// </summary>
public static class FileNameTrimming
{
    private const string Ellipsis = "…";

    public static readonly DependencyProperty FileNameProperty =
        DependencyProperty.RegisterAttached(
            "FileName", typeof(string), typeof(FileNameTrimming),
            new PropertyMetadata(null, OnFileNameChanged));

    public static void SetFileName(DependencyObject element, string value) =>
        element.SetValue(FileNameProperty, value);

    public static string? GetFileName(DependencyObject element) =>
        (string?)element.GetValue(FileNameProperty);

    private static void OnFileNameChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock textBlock)
            return;

        textBlock.SizeChanged -= OnSizeChanged;
        textBlock.SizeChanged += OnSizeChanged;

        // 不能在绑定回调中直接 Apply——此时 TextBlock 的 ActualWidth 可能为 0
        //（新创建 / 刚从 Collapsed 变 Visible / 布局尚未完成），直接 Apply 会设全文
        // 并依赖 SizeChanged 补救，但 SizeChanged 在特定 WPF 布局边缘情况下不触发。
        // 延迟到 Loaded 优先级（在布局完成后执行），保证 ActualWidth 有效。
        textBlock.Dispatcher.BeginInvoke(
            () => Apply(textBlock),
            System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private static void OnSizeChanged(object sender, SizeChangedEventArgs e) => Apply((TextBlock)sender);

    private static void Apply(TextBlock textBlock)
    {
        string full = GetFileName(textBlock) ?? string.Empty;
        double available = textBlock.ActualWidth;

        if (string.IsNullOrEmpty(full) || available <= 0 || Measure(textBlock, full) <= available)
        {
            SetTextIfChanged(textBlock, full);
            return;
        }

        string extension = Path.GetExtension(full);
        string baseName = extension.Length > 0 ? full[..^extension.Length] : full;
        string tail = Ellipsis + extension;

        // 二分找出主体能保留的最大字符数，使 base[..k] + "…" + ext 仍放得下。
        int low = 0, high = baseName.Length, best = 0;
        while (low <= high)
        {
            int mid = (low + high) / 2;
            if (Measure(textBlock, baseName[..mid] + tail) <= available)
            {
                best = mid;
                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }

        string result = best > 0 ? baseName[..best] + tail : tail;

        // 安全网：用独立测量 TextBlock 再次验证结果确实放得下。
        // 旧实现使用 FormattedText（GDI+ 遗留引擎），与 TextBlock（DirectWrite 引擎）
        // 在特定 DPI / 字体 / 字符组合下宽度计算存在偏差，极端边界条件下导致
        // 结果溢出 → 末端扩展名被遮挡。
        while (best > 0 && Measure(textBlock, result) > available)
        {
            best--;
            result = baseName[..best] + tail;
        }

        SetTextIfChanged(textBlock, result);
    }

    private static void SetTextIfChanged(TextBlock textBlock, string text)
    {
        if (!string.Equals(textBlock.Text, text, StringComparison.Ordinal))
            textBlock.Text = text;
    }

    /// <summary>
    /// 使用独立 <see cref="TextBlock"/> 做 WPF 原生布局测量（DirectWrite 引擎），
    /// 与最终渲染使用完全相同的文本引擎，消除 FormattedText（GDI+ 遗留引擎）的测量偏差。
    /// 独立 TextBlock 不在视觉树中，不会触发布局副作用或递归 Apply。
    /// </summary>
    [ThreadStatic]
    private static TextBlock? _measureBlock;

    private static double Measure(TextBlock source, string text)
    {
        if (_measureBlock == null)
        {
            _measureBlock = new TextBlock { TextWrapping = TextWrapping.NoWrap };
        }

        // 从源 TextBlock 克隆字体属性，保证测量与渲染使用相同字体配置。
        _measureBlock.FontFamily = source.FontFamily;
        _measureBlock.FontSize = source.FontSize;
        _measureBlock.FontStyle = source.FontStyle;
        _measureBlock.FontWeight = source.FontWeight;
        _measureBlock.FontStretch = source.FontStretch;
        _measureBlock.FlowDirection = source.FlowDirection;
        _measureBlock.LineHeight = source.LineHeight;
        _measureBlock.LineStackingStrategy = source.LineStackingStrategy;
        _measureBlock.Text = text;
        _measureBlock.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

        return _measureBlock.DesiredSize.Width;
    }
}
