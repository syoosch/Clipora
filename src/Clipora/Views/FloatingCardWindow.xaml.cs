using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Clipora.Interop;
using Clipora.ViewModels;

namespace Clipora.Views;

public partial class FloatingCardWindow : Window
{
    private readonly FloatingCardViewModel _model;
    private const double ImageInset = 8; // 两侧各 1 DIP 描边 + 3 DIP 留白。
    private bool _userAdjusted;
    private bool _closed;

    public FloatingCardWindow(FloatingCardViewModel model)
    {
        _model = model;
        InitializeComponent();
        DataContext = model;
        model.PropertyChanged += Model_PropertyChanged;
        Loaded += async (_, _) => await model.LoadAsync();
    }

    internal void ShowNear(Window anchor, int ordinal)
    {
        // 先在主窗口所在区域创建 HWND，再用物理像素限制到该显示器工作区。
        Left = anchor.Left + 24;
        Top = anchor.Top + 24;
        IntPtr hwnd = new WindowInteropHelper(this).EnsureHandle();
        IntPtr anchorHandle = new WindowInteropHelper(anchor).Handle;
        if (TryGetWorkArea(anchorHandle, out NativeMethods.RECT workArea)
            && NativeMethods.GetWindowRect(anchorHandle, out NativeMethods.RECT anchorRect))
        {
            double dpi = VisualTreeHelper.GetDpi(anchor).DpiScaleX;
            int width = Math.Min((int)Math.Round(Width * dpi), workArea.Right - workArea.Left);
            int height = Math.Min((int)Math.Round(Height * dpi), workArea.Bottom - workArea.Top);
            int gap = (int)Math.Round(12 * dpi);
            int offset = (ordinal % 6) * (int)Math.Round(24 * dpi);
            int preferredLeft = anchorRect.Right + gap;
            if (preferredLeft + width > workArea.Right)
                preferredLeft = anchorRect.Left - width - gap;
            int left = Math.Clamp(preferredLeft + offset, workArea.Left, Math.Max(workArea.Left, workArea.Right - width));
            int top = Math.Clamp(anchorRect.Top + offset, workArea.Top, Math.Max(workArea.Top, workArea.Bottom - height));
            NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, left, top, width, height,
                NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
        }
        Show();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(WindowMessageHook);
    }

    private IntPtr WindowMessageHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_ENTERSIZEMOVE) _userAdjusted = true;
        if (msg == NativeMethods.WM_SIZING && _model.Image is { } image)
        {
            var bounds = Marshal.PtrToStructure<NativeMethods.RECT>(lParam);
            double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
            ConstrainImageResize(ref bounds, (int)wParam, (double)image.PixelWidth / image.PixelHeight,
                ImageInset * dpi, MinWidth * dpi, MinHeight * dpi);
            Marshal.StructureToPtr(bounds, lParam, false);
            handled = true;
            return new IntPtr(1);
        }
        return IntPtr.Zero;
    }

    // 在原生调整矩形生效前约束，避免 SizeChanged 后二次修正引起抖动。
    internal static void ConstrainImageResize(ref NativeMethods.RECT bounds, int edge,
        double ratio, double inset, double minWidth, double minHeight)
    {
        double width = bounds.Right - bounds.Left - inset;
        double height = bounds.Bottom - bounds.Top - inset;
        bool horizontal = edge is NativeMethods.WMSZ_LEFT or NativeMethods.WMSZ_RIGHT;
        bool vertical = edge is NativeMethods.WMSZ_TOP or NativeMethods.WMSZ_BOTTOM;
        // 角拖动投影到等比方向，横向、纵向移动都能响应；边拖动由该轴驱动。
        double imageHeight = horizontal ? width / ratio : vertical ? height
            : (width * ratio + height) / (ratio * ratio + 1);
        imageHeight = Math.Max(imageHeight, Math.Max((minWidth - inset) / ratio, minHeight - inset));
        int targetWidth = (int)Math.Ceiling(imageHeight * ratio + inset);
        int targetHeight = (int)Math.Ceiling(imageHeight + inset);

        if (vertical)
        {
            int center = bounds.Left + (bounds.Right - bounds.Left) / 2;
            bounds.Left = center - targetWidth / 2;
            bounds.Right = bounds.Left + targetWidth;
        }
        else if (edge is NativeMethods.WMSZ_LEFT or NativeMethods.WMSZ_TOPLEFT or NativeMethods.WMSZ_BOTTOMLEFT)
            bounds.Left = bounds.Right - targetWidth;
        else
            bounds.Right = bounds.Left + targetWidth;

        if (horizontal)
        {
            int center = bounds.Top + (bounds.Bottom - bounds.Top) / 2;
            bounds.Top = center - targetHeight / 2;
            bounds.Bottom = bounds.Top + targetHeight;
        }
        else if (edge is NativeMethods.WMSZ_TOP or NativeMethods.WMSZ_TOPLEFT or NativeMethods.WMSZ_TOPRIGHT)
            bounds.Top = bounds.Bottom - targetHeight;
        else
            bounds.Bottom = bounds.Top + targetHeight;
    }

    private void Surface_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 1 || e.ButtonState != MouseButtonState.Pressed) return;
        _userAdjusted = true;
        e.Handled = true;
        DragMove();
    }

    private void Model_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(FloatingCardViewModel.Image) || _userAdjusted || _model.Image is not { } image) return;
        // 初次按图片比例收紧窗口，最小尺寸也联动；之后由原生 WM_SIZING 保持比例。
        double maxWidth = (double)FindResource("FloatingCardWidth");
        double maxHeight = (double)FindResource("FloatingCardHeight");
        double scale = Math.Min(1, Math.Min((maxWidth - ImageInset) / image.PixelWidth, (maxHeight - ImageInset) / image.PixelHeight));
        scale = Math.Max(scale, Math.Max((MinWidth - ImageInset) / image.PixelWidth, (MinHeight - ImageInset) / image.PixelHeight));
        Width = image.PixelWidth * scale + ImageInset;
        Height = image.PixelHeight * scale + ImageInset;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(ClampToWorkArea));
    }

    private void ClampToWorkArea()
    {
        if (_closed) return;
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        if (!TryGetWorkArea(hwnd, out NativeMethods.RECT area) || !NativeMethods.GetWindowRect(hwnd, out NativeMethods.RECT bounds)) return;
        int left = Math.Clamp(bounds.Left, area.Left, Math.Max(area.Left, area.Right - (bounds.Right - bounds.Left)));
        int top = Math.Clamp(bounds.Top, area.Top, Math.Max(area.Top, area.Bottom - (bounds.Bottom - bounds.Top)));
        NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, left, top, 0, 0,
            NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
    }

    private static bool TryGetWorkArea(IntPtr hwnd, out NativeMethods.RECT area)
    {
        var info = new NativeMethods.MONITORINFO { Size = Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        IntPtr monitor = NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST);
        bool found = NativeMethods.GetMonitorInfo(monitor, ref info);
        area = info.WorkArea;
        return found;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
            HwndSource.FromHwnd(hwnd)?.RemoveHook(WindowMessageHook);
        _model.PropertyChanged -= Model_PropertyChanged;
        _model.Dispose();
        DataContext = null;
        base.OnClosed(e);
    }
}
