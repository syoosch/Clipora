using System.Windows;
using System.Windows.Media;

namespace Clipora.Views;

internal static class ImagePreviewScreenGeometry
{
    public static bool TryGetElementBounds(
        FrameworkElement target,
        double marginDip,
        out Rect bounds)
    {
        bounds = Rect.Empty;
        PresentationSource? source = PresentationSource.FromVisual(target);
        if (source?.CompositionTarget is null)
            return false;

        double scaleX = PositiveOrOne(source.CompositionTarget.TransformToDevice.M11);
        double scaleY = PositiveOrOne(source.CompositionTarget.TransformToDevice.M22);

        try
        {
            Point screenPosition = target.PointToScreen(new Point(0, 0));
            bounds = InflateForDpi(
                new Rect(
                    screenPosition.X,
                    screenPosition.Y,
                    target.ActualWidth * scaleX,
                    target.ActualHeight * scaleY),
                marginDip,
                scaleX,
                scaleY);
            return bounds.Width > 0 && bounds.Height > 0;
        }
        catch (InvalidOperationException)
        {
            // PresentationSource 可在检查后被虚拟化回收；所有竞态都 fail closed。
            return false;
        }
    }

    public static Rect InflateForDpi(
        Rect physicalBounds,
        double marginDip,
        double scaleX,
        double scaleY)
    {
        double horizontal = Math.Max(0, marginDip) * PositiveOrOne(scaleX);
        double vertical = Math.Max(0, marginDip) * PositiveOrOne(scaleY);
        physicalBounds.Inflate(horizontal, vertical);
        return physicalBounds;
    }

    /// <summary>
    /// 把已规划的物理屏幕位置转换成 CustomPopupPlacement 相对目标左上角的偏移。
    /// PointToScreen 与规划矩形已经同属设备坐标；WPF 会在 Popup HWND 内部处理 DPI，
    /// 此处再次除以 TransformToDevice 会在 125%+ DPI 下把 Popup 推回主窗口内部。
    /// </summary>
    public static Point ToTargetRelativePopupOffset(
        Rect plannedPhysicalBounds,
        Point targetPhysicalOrigin) =>
        new(
            plannedPhysicalBounds.Left - targetPhysicalOrigin.X,
            plannedPhysicalBounds.Top - targetPhysicalOrigin.Y);

    private static double PositiveOrOne(double value) =>
        value > 0 && double.IsFinite(value) ? value : 1.0;
}
