#if DEBUG
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Clipora.Models;
using Clipora.Interop;
using Clipora.ViewModels;
using Clipora.Views;

namespace Clipora.Services;

/// <summary>图片悬浮窗的定向检查：使用合成图片，不启动数据库/监听器，也不读写系统剪贴板。</summary>
internal static class DevFloatingCardCheck
{
    public static async Task<int> RunAsync()
    {
        string? root = Environment.GetEnvironmentVariable("CLIPORA_DATA_DIR");
        if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root)) return 2;
        string output = Path.Combine(root, "floating-card-check");
        Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(output, "result.txt"), "FLOATING CARD CHECK RUNNING\n");
        using var manager = new FloatingCardManager();
        var anchor = new Window { Style = null, AllowsTransparency = false, Width = 380, Height = 300, Left = 24, Top = 24, ShowInTaskbar = false, ShowActivated = false };
        try
        {
            CheckAspectResize();
            var theme = new ThemeService();
            theme.ApplyColorMode("Light");
            anchor.Show();
            string imagePath = Path.Combine(output, "sample.png");
            CreateImage(imagePath);
            var item = new ClipItemViewModel(new ClipItem { Id = 1, Type = ClipType.Image, RefPath = imagePath });
            Check(manager.Open(new ClipItemViewModel(new ClipItem { Id = 99, Type = ClipType.Text, TextContent = "text" }), anchor) is null,
                "Text cards must not open floating windows.");
            FloatingCardWindow window = manager.Open(item, anchor)!;
            var model = (FloatingCardViewModel)window.DataContext;
            await model.LoadAsync();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Check(model.Image is { IsFrozen: true } && !model.HasMessage, "Image must be loaded and frozen.");
            Check(window.Topmost && window.Owner is null && !window.ShowInTaskbar && !window.ShowActivated,
                "Floating window must be independent and topmost without opening activation.");
            Check(ReferenceEquals(window, manager.Open(item, anchor)), "Repeated open must reuse the window.");
            anchor.Hide();
            Check(window.IsVisible, "Hiding the panel must not hide the image.");
            File.Delete(imagePath);
            Check(model.Image?.PixelWidth == 1600, "Loaded snapshot must survive source removal and release its file handle.");
            SaveView(window, Path.Combine(output, "image-light.png"));

            // 通过真实 HWND 消息验证比例约束已接入原生缩放，然后应用结果渲染。
            IntPtr hwnd = new WindowInteropHelper(window).Handle;
            NativeMethods.GetWindowRect(hwnd, out NativeMethods.RECT minimumBounds);
            minimumBounds.Right = minimumBounds.Left + 40;
            minimumBounds.Bottom = minimumBounds.Top + 40;
            IntPtr sizingRect = Marshal.AllocHGlobal(Marshal.SizeOf<NativeMethods.RECT>());
            try
            {
                Marshal.StructureToPtr(minimumBounds, sizingRect, false);
                Check(NativeMethods.SendMessage(hwnd, NativeMethods.WM_SIZING,
                    new IntPtr(NativeMethods.WMSZ_BOTTOMRIGHT), sizingRect) == new IntPtr(1), "Native resize hook must handle sizing.");
                minimumBounds = Marshal.PtrToStructure<NativeMethods.RECT>(sizingRect);
                double dpi = VisualTreeHelper.GetDpi(window).DpiScaleX;
                Check(Math.Abs((minimumBounds.Right - minimumBounds.Left - 8 * dpi)
                    / (minimumBounds.Bottom - minimumBounds.Top - 8 * dpi) - 1600.0 / 900) < 0.03,
                    "Native sizing must retain the image aspect ratio.");
                NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, minimumBounds.Left, minimumBounds.Top,
                    minimumBounds.Right - minimumBounds.Left, minimumBounds.Bottom - minimumBounds.Top,
                    NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
            }
            finally { Marshal.FreeHGlobal(sizingRect); }
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            SaveView(window, Path.Combine(output, "image-minimum.png"));
            window.Width = 480;
            window.Height = 278;
            theme.ApplyVisualTheme("LiquidGlass");
            theme.ApplyColorMode("Dark");
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            SaveView(window, Path.Combine(output, "image-dark.png"));

            var missingItem = new ClipItemViewModel(new ClipItem { Id = 2, Type = ClipType.Image, RefPath = imagePath });
            FloatingCardWindow missing = manager.Open(missingItem, anchor)!;
            var missingModel = (FloatingCardViewModel)missing.DataContext;
            await missingModel.LoadAsync();
            Check(missingModel.Image is null && missingModel.HasMessage, "Missing image must show an error instead of failing.");
            SaveView(missing, Path.Combine(output, "image-missing.png"));
            CreateImage(imagePath);
            var closingItem = new ClipItemViewModel(new ClipItem { Id = 3, Type = ClipType.Image, RefPath = imagePath });
            FloatingCardWindow closing = manager.Open(closingItem, anchor)!;
            var closingModel = (FloatingCardViewModel)closing.DataContext;
            Task loading = closingModel.LoadAsync();
            closing.Close();
            await loading;
            Check(closingModel.Image is null, "Closing while loading must not publish the result.");

            window.Close();
            Check(model.Image is null, "Closing must release the loaded image.");
            FloatingCardWindow reopened = manager.Open(item, anchor)!;
            Check(!ReferenceEquals(window, reopened), "Closed windows must be removed from the cache.");
            var reopenedModel = (FloatingCardViewModel)reopened.DataContext;
            await reopenedModel.LoadAsync();
            manager.Dispose();
            Check(!reopened.IsVisible && !missing.IsVisible && reopenedModel.Image is null,
                "Disposing the manager must close all windows and release images.");
            File.WriteAllText(Path.Combine(output, "result.txt"), "FLOATING CARD CHECK OK\nAspect ratio (all eight resize edges/corners, landscape/portrait/square, minimum size, 100/150/200% DPI, native HWND hook), image-only entry, duplicate open, independent lifetime, snapshot, missing image, loading cancellation, cleanup, light/dark/minimum-size renders.\n");
            return 0;
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(output, "result.txt"), "FLOATING CARD CHECK FAILED\n" + ex);
            return 1;
        }
        finally { anchor.Close(); }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void CheckAspectResize()
    {
        foreach (double ratio in new[] { 16.0 / 9, 9.0 / 16, 1, 0.125 })
        foreach (double dpi in new[] { 1, 1.5, 2 })
        foreach (int size in new[] { 40, 650 })
        for (int edge = NativeMethods.WMSZ_LEFT; edge <= NativeMethods.WMSZ_BOTTOMRIGHT; edge++)
        {
            var before = new NativeMethods.RECT { Left = -800, Top = -200, Right = -800 + size, Bottom = -200 + size };
            var after = before;
            FloatingCardWindow.ConstrainImageResize(ref after, edge, ratio, 8 * dpi, 120 * dpi, 80 * dpi);
            int width = after.Right - after.Left, height = after.Bottom - after.Top;
            Check(width >= 120 * dpi && height >= 80 * dpi, "Resize must respect both minimum dimensions.");
            Check(Math.Abs(width - 8 * dpi - (height - 8 * dpi) * ratio) <= Math.Max(1, ratio),
                "Image ratio must be retained within physical pixel rounding.");
            bool left = edge is NativeMethods.WMSZ_LEFT or NativeMethods.WMSZ_TOPLEFT or NativeMethods.WMSZ_BOTTOMLEFT;
            bool top = edge is NativeMethods.WMSZ_TOP or NativeMethods.WMSZ_TOPLEFT or NativeMethods.WMSZ_TOPRIGHT;
            if (edge is NativeMethods.WMSZ_TOP or NativeMethods.WMSZ_BOTTOM)
                Check(Math.Abs(after.Left + after.Right - before.Left - before.Right) <= 1, "Vertical edge must preserve horizontal center.");
            else
                Check(left ? after.Right == before.Right : after.Left == before.Left, "Opposite horizontal anchor must stay fixed.");
            if (edge is NativeMethods.WMSZ_LEFT or NativeMethods.WMSZ_RIGHT)
                Check(Math.Abs(after.Top + after.Bottom - before.Top - before.Bottom) <= 1, "Horizontal edge must preserve vertical center.");
            else
                Check(top ? after.Bottom == before.Bottom : after.Top == before.Top, "Opposite vertical anchor must stay fixed.");
        }
    }

    private static void CreateImage(string path)
    {
        var drawing = new DrawingVisual();
        using (DrawingContext dc = drawing.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(242, 246, 250)), null, new Rect(0, 0, 1600, 900));
            var text = new FormattedText("攻略参考\n\n第一阶段：保持距离，观察动作\n第二阶段：躲避范围攻击\n第三阶段：抓住空档输出",
                CultureInfo.GetCultureInfo("zh-CN"), FlowDirection.LeftToRight,
                new Typeface("Microsoft YaHei UI"), 62, new SolidColorBrush(Color.FromRgb(32, 48, 64)), 1);
            dc.DrawText(text, new Point(100, 90));
        }
        var bitmap = new RenderTargetBitmap(1600, 900, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(drawing);
        SaveBitmap(bitmap, path);
    }
    private static void SaveView(Window window, string path)
    {
        var surface = (FrameworkElement)window.Content;
        surface.UpdateLayout();
        var bitmap = new RenderTargetBitmap(Math.Max(1, (int)surface.ActualWidth), Math.Max(1, (int)surface.ActualHeight),
            96, 96, PixelFormats.Pbgra32);
        bitmap.Render(surface);
        SaveBitmap(bitmap, path);
    }
    private static void SaveBitmap(BitmapSource bitmap, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream stream = File.Create(path);
        encoder.Save(stream);
    }
}
#endif
