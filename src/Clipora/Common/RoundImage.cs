using System.Windows;
using System.Windows.Media;

namespace Clipora.Common;

/// <summary>
/// 给任意 FrameworkElement（如 Image）加圆角裁剪。用法：<c>common:RoundImage.Radius="8"</c>。
/// 随尺寸变化自动更新裁剪几何。
/// </summary>
public static class RoundImage
{
    public static readonly DependencyProperty RadiusProperty =
        DependencyProperty.RegisterAttached(
            "Radius", typeof(double), typeof(RoundImage),
            new PropertyMetadata(0.0, OnRadiusChanged));

    public static void SetRadius(DependencyObject element, double value) =>
        element.SetValue(RadiusProperty, value);

    public static double GetRadius(DependencyObject element) =>
        (double)element.GetValue(RadiusProperty);

    private static void OnRadiusChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element)
            return;

        element.SizeChanged -= OnSizeChanged;
        element.SizeChanged += OnSizeChanged;
        ApplyClip(element);
    }

    private static void OnSizeChanged(object sender, SizeChangedEventArgs e) =>
        ApplyClip((FrameworkElement)sender);

    private static void ApplyClip(FrameworkElement element)
    {
        double radius = GetRadius(element);
        if (element.ActualWidth <= 0 || element.ActualHeight <= 0)
            return;

        element.Clip = new RectangleGeometry(
            new Rect(0, 0, element.ActualWidth, element.ActualHeight),
            radius, radius);
    }
}
