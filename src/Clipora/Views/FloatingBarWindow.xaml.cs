using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Clipora.Interop;
using Clipora.Services;

namespace Clipora.Views;

public partial class FloatingBarWindow : Window
{
    private const int DragThreshold = 4;
    private readonly DispatcherTimer _previewTimer;
    private bool _pointerDown;
    private bool _isDragging;
    private bool _previewVisible;
    private bool _dockLeft;
    private int _dragPointerOffsetY;
    private NativeMethods.POINT _dragStartCursor;

    // —— 悬浮球拖入即存（条 ↔ 68px 方块，仅横向单向延展、高度/Top 不变） ——
    private const double CollapsedWidthDip = 14;        // 窗口静止宽（含手柄右侧 4px 透明）
    private const double CollapsedHandleWidthDip = 10;  // 静止可见手柄宽
    private const double CollapsedHeightDip = 68;
    private const double ExpandedSizeDip = 68;          // 方块边长（与条等高）
    private const double HandleRestOpacity = 0.58;
    private static readonly TimeSpan MorphDuration = TimeSpan.FromMilliseconds(140);
    private static readonly TimeSpan FeedbackHold = TimeSpan.FromMilliseconds(700);
    private static readonly string GlyphDrop = char.ConvertFromUtf32(0xE8B7);     // 拖入提示（与面板浮层一致）
    private static readonly string GlyphSuccess = char.ConvertFromUtf32(0xE73E);  // 对勾 CheckMark
    private static readonly string GlyphFail = char.ConvertFromUtf32(0xE711);     // 叉 Cancel

    private readonly DispatcherTimer _dropWatchdog;
    private readonly DispatcherTimer _feedbackTimer;
    private bool _wantExpanded;     // 形变目标方向：true=方块，false=条
    private bool _hasRestRect;      // 是否已捕获静止位置（收回目标），避免收回中途再次展开时取到中间值
    private double _restLeft;
    private double _restTop;

    public event EventHandler? RestoreRequested;
    public event EventHandler? PreviewRequested;
    public event EventHandler? PreviewDismissed;

    /// <summary>拖入内容落点请求：由组合根接 <c>MainViewModel.ImportExternalDrop</c>，并把结果写回 <see cref="ExternalDropEventArgs.Imported"/>。</summary>
    public event EventHandler<ExternalDropEventArgs>? ExternalDropRequested;

    public FloatingBarWindow()
    {
        InitializeComponent();
        _previewTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(220),
        };
        _previewTimer.Tick += (_, _) =>
        {
            _previewTimer.Stop();
            // 拖入方块激活期间不弹主面板预览：放大窗口时光标下方会有合成 mouse-move 误触发。
            if (!IsVisible || !IsMouseOver || _pointerDown || _isDragging || _previewVisible || _wantExpanded)
                return;

            _previewVisible = true;
            PreviewRequested?.Invoke(this, EventArgs.Empty);
        };

        // 拖入看门狗：拖动悬停静止时 OLE 停发 DragOver，靠物理左键状态兜底收回，绝不卡膨胀态。
        _dropWatchdog = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _dropWatchdog.Tick += WatchdogTick;

        // 落点反馈停留：对勾/叉显示约 0.7s 后收回。
        _feedbackTimer = new DispatcherTimer { Interval = FeedbackHold };
        _feedbackTimer.Tick += (_, _) =>
        {
            _feedbackTimer.Stop();
            Collapse();
        };

        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible)
            {
                _previewTimer.Stop();
                DismissPreview();
                ResetMorphImmediate();
            }
        };
    }

    public void PositionNear(Window sourceWindow)
    {
        Rect workArea = GetMonitorWorkArea(sourceWindow);
        double sourceCenterY = sourceWindow.Top + (sourceWindow.ActualHeight / 2);
        double leftDistance = Math.Abs(sourceWindow.Left - workArea.Left);
        double rightDistance = Math.Abs(workArea.Right - (sourceWindow.Left + sourceWindow.ActualWidth));
        _dockLeft = leftDistance <= rightDistance;

        ApplyPlacement(workArea, _dockLeft, sourceCenterY - (Height / 2));
    }

    private void ApplyPlacement(Rect workArea, bool dockLeft, double top)
    {
        Handle.HorizontalAlignment = dockLeft ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        Left = dockLeft ? workArea.Left : workArea.Right - Width;
        Top = Math.Clamp(top, workArea.Top, Math.Max(workArea.Top, workArea.Bottom - Height));
    }

    private static Rect GetMonitorWorkArea(Window sourceWindow)
    {
        IntPtr hwnd = new WindowInteropHelper(sourceWindow).Handle;
        IntPtr monitor = NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST);
        var monitorInfo = new NativeMethods.MONITORINFO
        {
            Size = Marshal.SizeOf<NativeMethods.MONITORINFO>(),
        };

        if (monitor != IntPtr.Zero && NativeMethods.GetMonitorInfo(monitor, ref monitorInfo))
        {
            Matrix fromDevice = PresentationSource.FromVisual(sourceWindow)?.CompositionTarget?.TransformFromDevice
                ?? Matrix.Identity;
            Point topLeft = fromDevice.Transform(new Point(monitorInfo.WorkArea.Left, monitorInfo.WorkArea.Top));
            Point bottomRight = fromDevice.Transform(new Point(monitorInfo.WorkArea.Right, monitorInfo.WorkArea.Bottom));
            return new Rect(topLeft, bottomRight);
        }

        return SystemParameters.WorkArea;
    }

    private void Surface_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
            return;

        e.Handled = true;
        _previewTimer.Stop();
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        if (!NativeMethods.GetCursorPos(out _dragStartCursor)
            || !NativeMethods.GetWindowRect(hwnd, out NativeMethods.RECT windowRect))
        {
            return;
        }

        _dragPointerOffsetY = _dragStartCursor.Y - windowRect.Top;
        _pointerDown = true;
        _isDragging = false;
        Mouse.Capture(this);
    }

    private void Surface_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_pointerDown || e.LeftButton != MouseButtonState.Pressed)
            return;

        if (!NativeMethods.GetCursorPos(out NativeMethods.POINT cursor))
            return;

        int deltaX = cursor.X - _dragStartCursor.X;
        int deltaY = cursor.Y - _dragStartCursor.Y;
        if (!_isDragging && Math.Abs(deltaX) < DragThreshold && Math.Abs(deltaY) < DragThreshold)
            return;

        _isDragging = true;
        DismissPreview();
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        if (!TryGetMonitorWorkArea(cursor, out NativeMethods.RECT workArea)
            || !NativeMethods.GetWindowRect(hwnd, out NativeMethods.RECT windowRect))
        {
            return;
        }

        int switchToLeftAt = workArea.Left + ((workArea.Right - workArea.Left) / 4);
        int switchToRightAt = workArea.Left + (((workArea.Right - workArea.Left) * 3) / 4);
        if (_dockLeft && cursor.X >= switchToRightAt)
            _dockLeft = false;
        else if (!_dockLeft && cursor.X <= switchToLeftAt)
            _dockLeft = true;

        int width = windowRect.Right - windowRect.Left;
        int height = windowRect.Bottom - windowRect.Top;
        int left = _dockLeft ? workArea.Left : workArea.Right - width;
        int top = Math.Clamp(cursor.Y - _dragPointerOffsetY, workArea.Top, Math.Max(workArea.Top, workArea.Bottom - height));
        Handle.HorizontalAlignment = _dockLeft ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        NativeMethods.SetWindowPos(
            hwnd,
            IntPtr.Zero,
            left,
            top,
            0,
            0,
            NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
        e.Handled = true;
    }

    private void Surface_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_pointerDown || e.ChangedButton != MouseButton.Left)
            return;

        bool wasDragging = _isDragging;
        _pointerDown = false;
        _isDragging = false;
        Mouse.Capture(null);
        e.Handled = true;

        if (wasDragging)
        {
            if (IsMouseOver)
                StartPreviewTimer();
            return;
        }

        _previewVisible = false;
        RestoreRequested?.Invoke(this, EventArgs.Empty);
    }

    private void Surface_MouseEnter(object sender, MouseEventArgs e)
    {
        if (!_pointerDown && !_isDragging && !_previewVisible && !_wantExpanded)
            StartPreviewTimer();
    }

    private void Surface_MouseLeave(object sender, MouseEventArgs e)
    {
        _previewTimer.Stop();
        DismissPreview();
    }

    private void StartPreviewTimer()
    {
        _previewTimer.Stop();
        _previewTimer.Start();
    }

    private void DismissPreview()
    {
        if (!_previewVisible)
            return;

        _previewVisible = false;
        PreviewDismissed?.Invoke(this, EventArgs.Empty);
    }

    private static bool TryGetMonitorWorkArea(NativeMethods.POINT point, out NativeMethods.RECT workArea)
    {
        workArea = default;
        IntPtr monitor = NativeMethods.MonitorFromPoint(point, NativeMethods.MONITOR_DEFAULTTONEAREST);
        if (monitor == IntPtr.Zero)
            return false;

        var monitorInfo = new NativeMethods.MONITORINFO
        {
            Size = Marshal.SizeOf<NativeMethods.MONITORINFO>(),
        };
        if (!NativeMethods.GetMonitorInfo(monitor, ref monitorInfo))
            return false;

        workArea = monitorInfo.WorkArea;
        return true;
    }

    // —— 拖入即存：拖入事件 ——

    private void Surface_DragEnter(object sender, DragEventArgs e) => UpdateDropState(e);

    private void Surface_DragOver(object sender, DragEventArgs e) => UpdateDropState(e);

    private void UpdateDropState(DragEventArgs e)
    {
        bool canAccept = ExternalDropSupport.HasAcceptableFormat(e.Data);
        e.Effects = canAccept ? DragDropEffects.Copy : DragDropEffects.None;
        if (canAccept)
        {
            Expand();
            PetWatchdog();
        }

        e.Handled = true;
    }

    private void Surface_DragLeave(object sender, DragEventArgs e)
    {
        e.Handled = true;
        // 拖出方块判定走实时物理光标比对窗口矩形，避免经过子元素时误判闪烁。
        if (DragWindowHelpers.IsCursorInsideWindow(this))
            return;

        Collapse();
    }

    private void Surface_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        _dropWatchdog.Stop();
        bool imported = false;
        if (ExternalDropSupport.HasAcceptableFormat(e.Data))
        {
            var args = new ExternalDropEventArgs(e.Data);
            ExternalDropRequested?.Invoke(this, args);
            imported = args.Imported;
        }

        e.Effects = imported ? DragDropEffects.Copy : DragDropEffects.None;
        ShowDropFeedback(imported);
    }

    private void ShowDropFeedback(bool success)
    {
        DropGlyph.Text = success ? GlyphSuccess : GlyphFail;

        // 正常路径落点前必已 DragEnter 展开；若未展开（极端）就不停留，直接还原。
        if (!_wantExpanded)
        {
            DropGlyph.Text = GlyphDrop;
            return;
        }

        _feedbackTimer.Stop();
        _feedbackTimer.Start();
    }

    // —— 拖入即存：条 ↔ 68px 方块（仅横向单向延展） ——
    //
    // 卡顿根因是逐帧重设 layered 置顶窗的 Left/Top/Width/Height（每帧 resize 透明窗很重，
    // 且圆角逐帧缩放在四角出渲染瑕疵）。这里改为：窗口尺寸只在展开/收回各 resize 一次，
    // 中间形变只对内层手柄 Handle.Width 做 WPF 合成动画（高度/Top 恒定，故只横向单向生长）。

    private void Expand()
    {
        if (_wantExpanded)
            return;

        // 仅在真正静止态捕获静止位置；收回中途再次展开时沿用原值，不取中间帧。
        if (!_hasRestRect)
        {
            SyncRestPositionFromWindow();
            _restLeft = Left;
            _restTop = Top;
            _hasRestRect = true;
        }

        _wantExpanded = true;

        // 拖入接管交互：取消任何待弹/已弹的悬停预览，避免放大窗口时合成 mouse-move 唤出主面板。
        _previewTimer.Stop();
        DismissPreview();

        // 一次性把窗口放大成 68×68 方块：贴左→左缘锁工作区左、向右长；贴右→右缘锁工作区右、向左长。
        // 高度与 Top 不变。此刻手柄仍为静止细条，加宽出的部分透明，视觉无跳变。
        Rect workArea = GetMonitorWorkArea(this);
        Width = ExpandedSizeDip;
        Height = CollapsedHeightDip;
        Top = _restTop;
        Left = _dockLeft ? workArea.Left : workArea.Right - ExpandedSizeDip;

        AnimateMorph(ExpandedSizeDip, glyphTo: 1.0, onCompleted: null);
        _dropWatchdog.Start();
    }

    private void Collapse()
    {
        _dropWatchdog.Stop();
        if (!_wantExpanded)
            return;

        _wantExpanded = false;
        AnimateMorph(CollapsedHandleWidthDip, glyphTo: 0.0, onCompleted: FinishCollapse);
    }

    /// <summary>仅对手柄宽度与图标不透明度做合成动画；窗口尺寸不在动画中改变。</summary>
    private void AnimateMorph(double handleWidthTo, double glyphTo, Action? onCompleted)
    {
        // 清除 hover 触发的手柄动画，避免其 HoldEnd 覆盖形变；方块沿用条的静止半透明。
        Handle.BeginAnimation(OpacityProperty, null);
        Handle.Opacity = HandleRestOpacity;
        Handle.Height = CollapsedHeightDip;

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var widthAnim = new DoubleAnimation(handleWidthTo, MorphDuration) { EasingFunction = ease };
        if (onCompleted != null)
            widthAnim.Completed += (_, _) => onCompleted();
        Handle.BeginAnimation(WidthProperty, widthAnim);

        var glyphAnim = new DoubleAnimation(glyphTo, MorphDuration) { EasingFunction = ease };
        DropGlyph.BeginAnimation(OpacityProperty, glyphAnim);
    }

    private void FinishCollapse()
    {
        // 收回动画结束。若期间又被展开则忽略（新展开动画已接管，旧 Completed 不应回缩窗口）。
        if (_wantExpanded)
            return;

        // 把手柄/图标固化成静止值并解除动画占用，让 hover 样式动画后续能重新接管。
        Handle.BeginAnimation(WidthProperty, null);
        DropGlyph.BeginAnimation(OpacityProperty, null);
        Handle.Width = CollapsedHandleWidthDip;
        DropGlyph.Opacity = 0;
        DropGlyph.Text = GlyphDrop;   // 还原拖入图标，供下次

        // 此刻手柄已是细条，窗口由 68 收回 14（细条≤14，不产生视觉跳变）。
        Width = CollapsedWidthDip;
        Height = CollapsedHeightDip;
        Left = _restLeft;
        Top = _restTop;
        _hasRestRect = false;
    }

    /// <summary>移位拖动用 SetWindowPos，WPF Left/Top 可能滞后；用真实物理位置回填 DIP 坐标。</summary>
    private void SyncRestPositionFromWindow()
    {
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || !NativeMethods.GetWindowRect(hwnd, out NativeMethods.RECT current))
            return;

        Matrix fromDevice = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice
            ?? Matrix.Identity;
        Point topLeft = fromDevice.Transform(new Point(current.Left, current.Top));
        Left = topLeft.X;
        Top = topLeft.Y;
    }

    private void ResetMorphImmediate()
    {
        _dropWatchdog.Stop();
        _feedbackTimer.Stop();
        _wantExpanded = false;
        _hasRestRect = false;

        Handle.BeginAnimation(WidthProperty, null);
        Handle.BeginAnimation(OpacityProperty, null);
        DropGlyph.BeginAnimation(OpacityProperty, null);
        Width = CollapsedWidthDip;
        Height = CollapsedHeightDip;
        Handle.Width = CollapsedHandleWidthDip;
        Handle.Height = CollapsedHeightDip;
        Handle.Opacity = HandleRestOpacity;
        DropGlyph.Opacity = 0;
        DropGlyph.Text = GlyphDrop;
    }

    private void PetWatchdog()
    {
        _dropWatchdog.Stop();
        _dropWatchdog.Start();
    }

    private void WatchdogTick(object? sender, EventArgs e)
    {
        if (DragWindowHelpers.IsPhysicalLeftButtonDown())
        {
            _dropWatchdog.Stop();
            _dropWatchdog.Start();
            return;
        }

        _dropWatchdog.Stop();
        Collapse();
    }
}

/// <summary>悬浮球拖入落点事件参数：携带拖入数据，由接手方写回是否成功入库。</summary>
public sealed class ExternalDropEventArgs : EventArgs
{
    public ExternalDropEventArgs(IDataObject data) => Data = data;

    public IDataObject Data { get; }

    public bool Imported { get; set; }
}
