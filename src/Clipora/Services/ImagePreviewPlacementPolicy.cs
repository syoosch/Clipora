using System.Windows;

namespace Clipora.Services;

internal enum ImagePreviewPlacementKind
{
    ExternalLeft,
    ExternalRight,
    InternalLeft,
    InternalRight,
}

internal readonly record struct ImagePreviewPlacement(
    Rect Bounds,
    ImagePreviewPlacementKind Kind,
    double Scale);

/// <summary>在物理屏幕坐标中计算稳定预览位置，不读取实时光标位置。</summary>
internal static class ImagePreviewPlacementPolicy
{
    internal const double MinimumExternalScale = 0.60;

    public static ImagePreviewPlacement Calculate(
        Rect workArea,
        Rect windowBounds,
        Rect targetBounds,
        Size desiredSize,
        double gap)
    {
        if (workArea.IsEmpty || workArea.Width <= 0 || workArea.Height <= 0)
            throw new ArgumentOutOfRangeException(nameof(workArea));
        if (desiredSize.Width <= 0 || desiredSize.Height <= 0)
            throw new ArgumentOutOfRangeException(nameof(desiredSize));

        gap = Math.Max(0, gap);
        Rect safeWorkArea = Inset(workArea, gap);
        if (safeWorkArea.Width <= 0 || safeWorkArea.Height <= 0)
            safeWorkArea = workArea;

        Rect leftSlot = CreateSlot(
            safeWorkArea.Left,
            safeWorkArea.Top,
            Math.Min(windowBounds.Left - gap, safeWorkArea.Right),
            safeWorkArea.Bottom);
        Rect rightSlot = CreateSlot(
            Math.Max(windowBounds.Right + gap, safeWorkArea.Left),
            safeWorkArea.Top,
            safeWorkArea.Right,
            safeWorkArea.Bottom);

        double leftScale = ScaleToFit(desiredSize, leftSlot.Size);
        double rightScale = ScaleToFit(desiredSize, rightSlot.Size);
        bool preferLeft = leftScale > rightScale
            || (Math.Abs(leftScale - rightScale) < 0.0001 && leftSlot.Width > rightSlot.Width);
        Rect preferredSlot = preferLeft ? leftSlot : rightSlot;
        double preferredScale = preferLeft ? leftScale : rightScale;

        if (preferredScale >= MinimumExternalScale)
        {
            double scale = Math.Min(1.0, preferredScale);
            Size size = Scale(desiredSize, scale);
            double x = preferLeft ? preferredSlot.Right - size.Width : preferredSlot.Left;
            double y = Clamp(
                targetBounds.Top + (targetBounds.Height - size.Height) / 2,
                preferredSlot.Top,
                preferredSlot.Bottom - size.Height);
            return new ImagePreviewPlacement(
                new Rect(x, y, size.Width, size.Height),
                preferLeft ? ImagePreviewPlacementKind.ExternalLeft : ImagePreviewPlacementKind.ExternalRight,
                scale);
        }

        Rect internalSlot = Rect.Intersect(windowBounds, safeWorkArea);
        if (internalSlot.IsEmpty || internalSlot.Width <= 0 || internalSlot.Height <= 0)
            internalSlot = safeWorkArea;

        double internalScale = Math.Min(1.0, ScaleToFit(desiredSize, internalSlot.Size));
        Size internalSize = Scale(desiredSize, internalScale);
        bool targetOnLeft = targetBounds.Left + targetBounds.Width / 2
            <= windowBounds.Left + windowBounds.Width / 2;
        double internalX = targetOnLeft
            ? internalSlot.Right - internalSize.Width
            : internalSlot.Left;
        double internalY = Clamp(
            targetBounds.Top + (targetBounds.Height - internalSize.Height) / 2,
            internalSlot.Top,
            internalSlot.Bottom - internalSize.Height);
        return new ImagePreviewPlacement(
            new Rect(internalX, internalY, internalSize.Width, internalSize.Height),
            targetOnLeft ? ImagePreviewPlacementKind.InternalRight : ImagePreviewPlacementKind.InternalLeft,
            internalScale);
    }

    public static Size GetDesiredOuterSize(
        int pixelWidth,
        int pixelHeight,
        double displayDpiX,
        double displayDpiY,
        double maximumOuterSize,
        double chromeSize)
    {
        if (pixelWidth <= 0 || pixelHeight <= 0)
            return Size.Empty;

        displayDpiX = displayDpiX > 0 && double.IsFinite(displayDpiX) ? displayDpiX : 96;
        displayDpiY = displayDpiY > 0 && double.IsFinite(displayDpiY) ? displayDpiY : 96;
        double maximumContent = Math.Max(1, maximumOuterSize - chromeSize);
        double naturalWidth = pixelWidth * 96.0 / displayDpiX;
        double naturalHeight = pixelHeight * 96.0 / displayDpiY;
        double scale = Math.Min(
            1.0,
            Math.Min(maximumContent / naturalWidth, maximumContent / naturalHeight));
        return new Size(
            naturalWidth * scale + chromeSize,
            naturalHeight * scale + chromeSize);
    }

    private static Rect Inset(Rect rect, double amount)
    {
        double width = Math.Max(0, rect.Width - amount * 2);
        double height = Math.Max(0, rect.Height - amount * 2);
        return new Rect(rect.Left + amount, rect.Top + amount, width, height);
    }

    private static Rect CreateSlot(double left, double top, double right, double bottom) =>
        new(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));

    private static double ScaleToFit(Size desired, Size available)
    {
        if (available.Width <= 0 || available.Height <= 0)
            return 0;

        return Math.Min(available.Width / desired.Width, available.Height / desired.Height);
    }

    private static Size Scale(Size size, double scale) =>
        new(Math.Max(1, size.Width * scale), Math.Max(1, size.Height * scale));

    private static double Clamp(double value, double minimum, double maximum) =>
        maximum < minimum ? minimum : Math.Clamp(value, minimum, maximum);
}
