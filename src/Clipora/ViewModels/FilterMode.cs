using Clipora.Common;

namespace Clipora.ViewModels;

public enum FilterMode
{
    Type,
    Tag,
}

/// <summary>搜索区的筛选模式分段项。</summary>
public sealed class FilterModeOption : ObservableObject
{
    private bool _isActive;

    public FilterModeOption(string label, FilterMode mode, bool isActive = false)
    {
        Label = label;
        Mode = mode;
        _isActive = isActive;
    }

    public string Label { get; }

    public FilterMode Mode { get; }

    public bool IsActive
    {
        get => _isActive;
        set => Set(ref _isActive, value);
    }
}
