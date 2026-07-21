using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Clipora.Abstractions;
using Clipora.Interop;
using Clipora.Services;
using Clipora.ViewModels;

namespace Clipora.Views;

/// <summary>集中管理图片卡片悬停意图、加载、生命周期与稳定 Popup 定位。</summary>
internal sealed class ImagePreviewController : IDisposable
{
    private const double HoverToleranceDip = 4;
    private const double PopupGapDip = 12;
    private const double MaximumOuterSizeDip = 400;
    private const double PreviewChromeDip = 20; // Border 2px + Image Margin 18px
    private const double MaximumContentSizeDip = MaximumOuterSizeDip - PreviewChromeDip;
    private static readonly TimeSpan MonitorInterval = TimeSpan.FromMilliseconds(33);

    private readonly Window _owner;
    private readonly ListBox _itemsList;
    private readonly Popup _popup;
    private readonly Border _previewBorder;
    private readonly Image _previewImage;
    private readonly IImagePreviewLoader _loader;
    private readonly ImagePreviewInteractionPolicy _policy = new();
    private readonly DispatcherTimer _monitor;
    private readonly Func<long> _nowMilliseconds;

    private FrameworkElement? _target;
    private ClipItemViewModel? _targetViewModel;
    private string? _targetPath;
    private CancellationTokenSource? _loadCancellation;
    private Rect _plannedPopupBounds = Rect.Empty;
    private bool _windowMoveOrResizeActive;
    private bool _disposed;
    private int _deactivationCheckVersion;

    public ImagePreviewController(
        Window owner,
        ListBox itemsList,
        Popup popup,
        Border previewBorder,
        Image previewImage,
        IImagePreviewLoader loader,
        Func<long>? nowMilliseconds = null)
    {
        _owner = owner;
        _itemsList = itemsList;
        _popup = popup;
        _previewBorder = previewBorder;
        _previewImage = previewImage;
        _loader = loader;
        _nowMilliseconds = nowMilliseconds ?? (() => Environment.TickCount64);

        _monitor = new DispatcherTimer(DispatcherPriority.Input, owner.Dispatcher)
        {
            Interval = MonitorInterval,
        };
        _monitor.Tick += OnMonitorTick;
        _popup.CustomPopupPlacementCallback = PlacePopup;
        _owner.Deactivated += OnOwnerDeactivated;
        _owner.IsVisibleChanged += OnOwnerIsVisibleChanged;
        _owner.Closed += OnOwnerClosed;
    }

    public void HandleThumbnailEnter(FrameworkElement target)
    {
        if (_disposed || !TryGetCandidate(target, out ClipItemViewModel? vm, out string? path))
            return;

        if (HasPointerInteraction())
        {
            SuppressForPointerInteraction();
            return;
        }

        if (_policy.IsScrollActive)
            return;

        if (ReferenceEquals(_target, target)
            && _policy.Phase is ImagePreviewPhase.Pending or ImagePreviewPhase.Loading or ImagePreviewPhase.Open)
        {
            return;
        }

        if (!IsCandidateUnderCursor(target, vm, path, out _))
            return;

        ArmTarget(target, vm, path, afterScroll: false);
    }

    public void HandleThumbnailLeave(FrameworkElement target)
    {
        if (_disposed || !ReferenceEquals(_target, target))
            return;

        if (!ValidateCurrentTarget(out ValidationFailure failure))
            CancelCurrent(failure == ValidationFailure.PointerInteraction);
    }

    public void NotifyScrollInput() => BeginScrollSuppression();

    public void NotifyScrollChanged(double horizontalChange, double verticalChange)
    {
        if (Math.Abs(horizontalChange) > 0.01 || Math.Abs(verticalChange) > 0.01)
            BeginScrollSuppression();
    }

    public void NotifyPointerInteraction() => SuppressForPointerInteraction();

    public void NotifyWindowGeometryChanged() => SuppressForPointerInteraction();

    public void NotifyWindowLifecycleEnd() => SuppressForPointerInteraction();

    public void HandleWindowMessage(int message)
    {
        switch (message)
        {
            case NativeMethods.WM_NCLBUTTONDOWN:
            case NativeMethods.WM_NCRBUTTONDOWN:
            case NativeMethods.WM_NCMBUTTONDOWN:
                SuppressForPointerInteraction();
                break;
            case NativeMethods.WM_ENTERSIZEMOVE:
                _windowMoveOrResizeActive = true;
                SuppressForPointerInteraction();
                break;
            case NativeMethods.WM_EXITSIZEMOVE:
                _windowMoveOrResizeActive = false;
                SuppressForPointerInteraction();
                break;
            case NativeMethods.WM_CAPTURECHANGED:
                SuppressForPointerInteraction();
                break;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _monitor.Stop();
        _monitor.Tick -= OnMonitorTick;
        _owner.Deactivated -= OnOwnerDeactivated;
        _owner.IsVisibleChanged -= OnOwnerIsVisibleChanged;
        _owner.Closed -= OnOwnerClosed;
        _popup.CustomPopupPlacementCallback = null;
        _policy.Cancel(requireFreshEntry: true);
        CancelIoAndVisual(clearTarget: true);
    }

    private void BeginScrollSuppression()
    {
        if (_disposed)
            return;

        _policy.NotifyScroll(_nowMilliseconds());
        CancelIoAndVisual(clearTarget: true);
        UpdateMonitorState();
    }

    private void SuppressForPointerInteraction()
    {
        if (_disposed)
            return;

        _policy.NotifyPointerInteraction();
        CancelIoAndVisual(clearTarget: true);
        UpdateMonitorState();
    }

    private void ArmTarget(
        FrameworkElement target,
        ClipItemViewModel vm,
        string path,
        bool afterScroll)
    {
        long now = _nowMilliseconds();
        long requestVersion = afterScroll
            ? _policy.ArmAfterScroll(now)
            : _policy.ArmFromPointerEntry(now);
        if (requestVersion < 0)
            return;

        CancelIoAndVisual(clearTarget: true);
        _target = target;
        _targetViewModel = vm;
        _targetPath = path;
        UpdateMonitorState();
    }

    private void OnMonitorTick(object? sender, EventArgs e)
    {
        if (_disposed)
            return;

        long now = _nowMilliseconds();
        if (_policy.IsScrollActive)
        {
            if (_policy.TrySettleScroll(now)
                && !_policy.RequiresFreshEntry
                && TryGetThumbnailUnderCursor(out FrameworkElement? target, out ClipItemViewModel? vm, out string? path))
            {
                ArmTarget(target, vm, path, afterScroll: true);
            }

            UpdateMonitorState();
            return;
        }

        if (_policy.Phase is ImagePreviewPhase.Pending or ImagePreviewPhase.Loading or ImagePreviewPhase.Open)
        {
            if (!ValidateCurrentTarget(out ValidationFailure failure))
            {
                CancelCurrent(failure == ValidationFailure.PointerInteraction);
                return;
            }

            if (_policy.ShouldBeginLoading(now))
            {
                long requestVersion = _policy.RequestVersion;
                if (_policy.TryBeginLoading(requestVersion))
                    StartLoading(requestVersion);
            }
        }

        UpdateMonitorState();
    }

    private void StartLoading(long requestVersion)
    {
        FrameworkElement? target = _target;
        string? path = _targetPath;
        if (target is null || string.IsNullOrWhiteSpace(path))
        {
            CancelCurrent(requireFreshEntry: false);
            return;
        }

        PresentationSource? source = PresentationSource.FromVisual(target);
        if (source?.CompositionTarget is null)
        {
            CancelCurrent(requireFreshEntry: false);
            return;
        }

        double scaleX = PositiveOrOne(source.CompositionTarget.TransformToDevice.M11);
        double scaleY = PositiveOrOne(source.CompositionTarget.TransformToDevice.M22);
        int maxPixelWidth = Math.Max(1, (int)Math.Ceiling(MaximumContentSizeDip * scaleX));
        int maxPixelHeight = Math.Max(1, (int)Math.Ceiling(MaximumContentSizeDip * scaleY));

        _loadCancellation?.Cancel();
        _loadCancellation?.Dispose();
        _loadCancellation = new CancellationTokenSource();
        _ = LoadAndShowAsync(
            requestVersion,
            path,
            maxPixelWidth,
            maxPixelHeight,
            _loadCancellation.Token);
    }

    private async Task LoadAndShowAsync(
        long requestVersion,
        string path,
        int maxPixelWidth,
        int maxPixelHeight,
        CancellationToken cancellationToken)
    {
        BitmapSource? bitmap;
        try
        {
            bitmap = await _loader.LoadAsync(
                path,
                maxPixelWidth,
                maxPixelHeight,
                cancellationToken);
        }
        catch
        {
            bitmap = null;
        }

        if (_disposed
            || cancellationToken.IsCancellationRequested
            || !_policy.IsCurrent(requestVersion, ImagePreviewPhase.Loading))
        {
            return;
        }

        if (bitmap is null)
        {
            CancelCurrent(requireFreshEntry: false);
            return;
        }

        if (!ValidateCurrentTarget(out ValidationFailure failure))
        {
            CancelCurrent(failure == ValidationFailure.PointerInteraction);
            return;
        }

        if (!TryPlanPlacement(bitmap)
            || !_policy.TryOpen(requestVersion))
        {
            CancelCurrent(requireFreshEntry: false);
            return;
        }

        try
        {
            _previewImage.Source = bitmap;
            _previewBorder.Opacity = 0;
            _popup.IsOpen = true;

            var animation = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            };
            _previewBorder.BeginAnimation(
                UIElement.OpacityProperty,
                animation,
                HandoffBehavior.SnapshotAndReplace);
        }
        catch
        {
            CancelCurrent(requireFreshEntry: false);
            return;
        }

        UpdateMonitorState();
    }

    private bool TryPlanPlacement(BitmapSource bitmap)
    {
        FrameworkElement? target = _target;
        if (target is null
            || !ImagePreviewScreenGeometry.TryGetElementBounds(target, 0, out Rect targetBounds))
        {
            return false;
        }

        PresentationSource? source = PresentationSource.FromVisual(target);
        if (source?.CompositionTarget is null)
            return false;

        double scaleX = PositiveOrOne(source.CompositionTarget.TransformToDevice.M11);
        double scaleY = PositiveOrOne(source.CompositionTarget.TransformToDevice.M22);
        IntPtr ownerHandle = new WindowInteropHelper(_owner).Handle;
        if (ownerHandle == IntPtr.Zero
            || !NativeMethods.GetWindowRect(ownerHandle, out NativeMethods.RECT ownerRect))
        {
            return false;
        }

        var targetCenter = new NativeMethods.POINT
        {
            X = (int)Math.Round(targetBounds.Left + targetBounds.Width / 2),
            Y = (int)Math.Round(targetBounds.Top + targetBounds.Height / 2),
        };
        IntPtr monitor = NativeMethods.MonitorFromPoint(targetCenter, NativeMethods.MONITOR_DEFAULTTONEAREST);
        var monitorInfo = new NativeMethods.MONITORINFO
        {
            Size = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFO>(),
        };
        if (monitor == IntPtr.Zero || !NativeMethods.GetMonitorInfo(monitor, ref monitorInfo))
            return false;

        Size desiredDip = ImagePreviewPlacementPolicy.GetDesiredOuterSize(
            bitmap.PixelWidth,
            bitmap.PixelHeight,
            96 * scaleX,
            96 * scaleY,
            MaximumOuterSizeDip,
            PreviewChromeDip);
        if (desiredDip.IsEmpty)
            return false;

        var workArea = new Rect(
            monitorInfo.WorkArea.Left,
            monitorInfo.WorkArea.Top,
            monitorInfo.WorkArea.Right - monitorInfo.WorkArea.Left,
            monitorInfo.WorkArea.Bottom - monitorInfo.WorkArea.Top);
        var windowBounds = new Rect(
            ownerRect.Left,
            ownerRect.Top,
            ownerRect.Right - ownerRect.Left,
            ownerRect.Bottom - ownerRect.Top);
        var desiredPhysical = new Size(desiredDip.Width * scaleX, desiredDip.Height * scaleY);
        ImagePreviewPlacement placement = ImagePreviewPlacementPolicy.Calculate(
            workArea,
            windowBounds,
            targetBounds,
            desiredPhysical,
            PopupGapDip * Math.Max(scaleX, scaleY));

        _plannedPopupBounds = placement.Bounds;
        _previewBorder.Width = placement.Bounds.Width / scaleX;
        _previewBorder.Height = placement.Bounds.Height / scaleY;
        _popup.PlacementTarget = target;
        return true;
    }

    private CustomPopupPlacement[] PlacePopup(Size popupSize, Size targetSize, Point offset)
    {
        FrameworkElement? target = _target;
        PresentationSource? source = target is null ? null : PresentationSource.FromVisual(target);
        if (target is null
            || _plannedPopupBounds.IsEmpty
            || source?.CompositionTarget is null)
        {
            return [new CustomPopupPlacement(new Point(0, 0), PopupPrimaryAxis.None)];
        }

        try
        {
            Point targetScreen = target.PointToScreen(new Point(0, 0));
            Point popupOffset = ImagePreviewScreenGeometry.ToTargetRelativePopupOffset(
                _plannedPopupBounds,
                targetScreen);
            return
            [
                new CustomPopupPlacement(
                    popupOffset,
                    PopupPrimaryAxis.None),
            ];
        }
        catch (InvalidOperationException)
        {
            return [new CustomPopupPlacement(new Point(0, 0), PopupPrimaryAxis.None)];
        }
    }

    private bool ValidateCurrentTarget(out ValidationFailure failure)
    {
        failure = ValidationFailure.TargetInvalid;
        if (_target is null || _targetViewModel is null || string.IsNullOrWhiteSpace(_targetPath))
            return false;

        if (HasPointerInteraction())
        {
            failure = ValidationFailure.PointerInteraction;
            return false;
        }

        if (_policy.IsScrollActive)
        {
            failure = ValidationFailure.Scroll;
            return false;
        }

        return IsCandidateUnderCursor(_target, _targetViewModel, _targetPath, out failure);
    }

    private bool IsCandidateUnderCursor(
        FrameworkElement target,
        ClipItemViewModel expectedViewModel,
        string expectedPath,
        out ValidationFailure failure)
    {
        failure = ValidationFailure.TargetInvalid;
        if (!_owner.IsVisible
            || !target.IsLoaded
            || !target.IsVisible
            || !IsTargetBindingCurrent(target, expectedViewModel, expectedPath)
            || !ImagePreviewScreenGeometry.TryGetElementBounds(target, HoverToleranceDip, out Rect targetBounds)
            || !ImagePreviewScreenGeometry.TryGetElementBounds(_itemsList, 0, out Rect listBounds)
            || !NativeMethods.GetCursorPos(out NativeMethods.POINT cursor))
        {
            return false;
        }

        Rect effectiveBounds = Rect.Intersect(targetBounds, listBounds);
        if (effectiveBounds.IsEmpty || !effectiveBounds.Contains(cursor.X, cursor.Y))
        {
            failure = ValidationFailure.PointerOutside;
            return false;
        }

        failure = ValidationFailure.None;
        return true;
    }

    internal static bool IsTargetBindingCurrent(
        FrameworkElement target,
        ClipItemViewModel expectedViewModel,
        string expectedPath) =>
        ReferenceEquals(target.DataContext, expectedViewModel)
        && expectedViewModel.HasThumbnail
        && string.Equals(
            expectedViewModel.PreviewImagePath,
            expectedPath,
            StringComparison.OrdinalIgnoreCase);

    private bool TryGetThumbnailUnderCursor(
        out FrameworkElement target,
        out ClipItemViewModel viewModel,
        out string path)
    {
        target = null!;
        viewModel = null!;
        path = string.Empty;
        if (!_owner.IsVisible
            || HasPointerInteraction()
            || !NativeMethods.GetCursorPos(out NativeMethods.POINT cursor))
        {
            return false;
        }

        IInputElement? hit;
        try
        {
            Point listPoint = _itemsList.PointFromScreen(new Point(cursor.X, cursor.Y));
            hit = _itemsList.InputHitTest(listPoint);
        }
        catch (InvalidOperationException)
        {
            return false;
        }

        DependencyObject? current = hit as DependencyObject;
        while (current is not null)
        {
            if (current is FrameworkElement { Name: "ImagePreviewThumbnail" } candidate
                && TryGetCandidate(candidate, out ClipItemViewModel? vm, out string? candidatePath)
                && IsCandidateUnderCursor(candidate, vm, candidatePath, out _))
            {
                target = candidate;
                viewModel = vm;
                path = candidatePath;
                return true;
            }

            current = current is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);
        }

        return false;
    }

    private static bool TryGetCandidate(
        FrameworkElement target,
        out ClipItemViewModel viewModel,
        out string path)
    {
        viewModel = null!;
        path = string.Empty;
        if (target.DataContext is not ClipItemViewModel vm
            || !vm.HasThumbnail
            || string.IsNullOrWhiteSpace(vm.PreviewImagePath))
        {
            return false;
        }

        viewModel = vm;
        path = vm.PreviewImagePath;
        return true;
    }

    private bool HasPointerInteraction() =>
        _windowMoveOrResizeActive
        || Mouse.Captured is not null
        || IsAnyPhysicalMouseButtonDown();

    private static bool IsAnyPhysicalMouseButtonDown() =>
        IsKeyDown(NativeMethods.VK_LBUTTON)
        || IsKeyDown(NativeMethods.VK_RBUTTON)
        || IsKeyDown(NativeMethods.VK_MBUTTON)
        || IsKeyDown(NativeMethods.VK_XBUTTON1)
        || IsKeyDown(NativeMethods.VK_XBUTTON2);

    private static bool IsKeyDown(int virtualKey) =>
        (NativeMethods.GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    private void CancelCurrent(bool requireFreshEntry)
    {
        _policy.Cancel(requireFreshEntry);
        CancelIoAndVisual(clearTarget: true);
        UpdateMonitorState();
    }

    private void CancelIoAndVisual(bool clearTarget)
    {
        _loadCancellation?.Cancel();
        _loadCancellation?.Dispose();
        _loadCancellation = null;

        _previewBorder.BeginAnimation(UIElement.OpacityProperty, null);
        _previewBorder.Opacity = 0;
        _popup.IsOpen = false;
        _popup.PlacementTarget = null;
        _previewImage.Source = null;
        _previewBorder.Width = double.NaN;
        _previewBorder.Height = double.NaN;
        _plannedPopupBounds = Rect.Empty;

        if (clearTarget)
        {
            _target = null;
            _targetViewModel = null;
            _targetPath = null;
        }
    }

    private void UpdateMonitorState()
    {
        if (_disposed || !_policy.RequiresMonitoring)
            _monitor.Stop();
        else if (!_monitor.IsEnabled)
            _monitor.Start();
    }

    private void OnOwnerDeactivated(object? sender, EventArgs e)
    {
        int version = ++_deactivationCheckVersion;
        _owner.Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (_disposed || version != _deactivationCheckVersion)
                return;

            IntPtr foreground = NativeMethods.GetForegroundWindow();
            if (foreground == IntPtr.Zero)
            {
                SuppressForPointerInteraction();
                return;
            }

            NativeMethods.GetWindowThreadProcessId(foreground, out uint processId);
            if (processId != (uint)Environment.ProcessId)
                SuppressForPointerInteraction();
        });
    }

    private void OnOwnerIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is false)
            SuppressForPointerInteraction();
    }

    private void OnOwnerClosed(object? sender, EventArgs e) => Dispose();

    private static double PositiveOrOne(double value) =>
        value > 0 && double.IsFinite(value) ? value : 1.0;

    private enum ValidationFailure
    {
        None,
        PointerOutside,
        PointerInteraction,
        Scroll,
        TargetInvalid,
    }
}
