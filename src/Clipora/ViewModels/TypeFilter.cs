using Clipora.Common;
using Clipora.Models;

namespace Clipora.ViewModels;

/// <summary>类型筛选 chip：标签 + 对应类型（null = 全部）+ 选中态。</summary>
public sealed class TypeFilter : ObservableObject
{
    private bool _isActive;

    public TypeFilter(string label, ClipType? type, bool isActive = false)
    {
        Label = label;
        Type = type;
        _isActive = isActive;
    }

    public string Label { get; }

    public ClipType? Type { get; }

    public bool IsActive
    {
        get => _isActive;
        set => Set(ref _isActive, value);
    }
}
