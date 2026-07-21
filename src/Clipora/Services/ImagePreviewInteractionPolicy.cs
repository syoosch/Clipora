namespace Clipora.Services;

internal enum ImagePreviewPhase
{
    Idle,
    Pending,
    Loading,
    Open,
    Suppressed,
}

/// <summary>图片预览的纯状态机；时间单位统一为单调毫秒。</summary>
internal sealed class ImagePreviewInteractionPolicy
{
    internal const long HoverDelayMilliseconds = 350;
    internal const long ScrollIdleMilliseconds = 120;

    private long _pendingSinceMilliseconds;
    private long _lastScrollActivityMilliseconds;
    private long _requestVersion;

    public ImagePreviewPhase Phase { get; private set; } = ImagePreviewPhase.Idle;
    public bool IsScrollActive { get; private set; }
    public bool RequiresFreshEntry { get; private set; }
    public long RequestVersion => _requestVersion;
    public bool RequiresMonitoring =>
        IsScrollActive || Phase is ImagePreviewPhase.Pending or ImagePreviewPhase.Loading or ImagePreviewPhase.Open;

    public long ArmFromPointerEntry(long nowMilliseconds)
    {
        if (IsScrollActive)
            return -1;

        RequiresFreshEntry = false;
        return Arm(nowMilliseconds);
    }

    public long ArmAfterScroll(long nowMilliseconds)
    {
        if (IsScrollActive || RequiresFreshEntry)
            return -1;

        return Arm(nowMilliseconds);
    }

    public bool ShouldBeginLoading(long nowMilliseconds) =>
        Phase == ImagePreviewPhase.Pending
        && Elapsed(nowMilliseconds, _pendingSinceMilliseconds) >= HoverDelayMilliseconds;

    public bool TryBeginLoading(long requestVersion)
    {
        if (Phase != ImagePreviewPhase.Pending || requestVersion != _requestVersion)
            return false;

        Phase = ImagePreviewPhase.Loading;
        return true;
    }

    public bool TryOpen(long requestVersion)
    {
        if (Phase != ImagePreviewPhase.Loading || requestVersion != _requestVersion)
            return false;

        Phase = ImagePreviewPhase.Open;
        return true;
    }

    public bool IsCurrent(long requestVersion, ImagePreviewPhase phase) =>
        requestVersion == _requestVersion && Phase == phase;

    public void NotifyScroll(long nowMilliseconds)
    {
        InvalidateRequest();
        IsScrollActive = true;
        _lastScrollActivityMilliseconds = nowMilliseconds;
        Phase = ImagePreviewPhase.Suppressed;
    }

    public bool TrySettleScroll(long nowMilliseconds)
    {
        if (!IsScrollActive
            || Elapsed(nowMilliseconds, _lastScrollActivityMilliseconds) < ScrollIdleMilliseconds)
        {
            return false;
        }

        IsScrollActive = false;
        Phase = RequiresFreshEntry ? ImagePreviewPhase.Suppressed : ImagePreviewPhase.Idle;
        return true;
    }

    public void NotifyPointerInteraction()
    {
        RequiresFreshEntry = true;
        InvalidateRequest();
        Phase = ImagePreviewPhase.Suppressed;
    }

    public void Cancel(bool requireFreshEntry = false)
    {
        if (requireFreshEntry)
            RequiresFreshEntry = true;

        InvalidateRequest();
        Phase = IsScrollActive || RequiresFreshEntry
            ? ImagePreviewPhase.Suppressed
            : ImagePreviewPhase.Idle;
    }

    private long Arm(long nowMilliseconds)
    {
        _requestVersion++;
        _pendingSinceMilliseconds = nowMilliseconds;
        Phase = ImagePreviewPhase.Pending;
        return _requestVersion;
    }

    private void InvalidateRequest() => _requestVersion++;

    private static long Elapsed(long nowMilliseconds, long sinceMilliseconds) =>
        nowMilliseconds >= sinceMilliseconds ? nowMilliseconds - sinceMilliseconds : long.MaxValue;
}
