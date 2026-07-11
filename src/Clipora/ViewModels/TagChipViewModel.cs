using System;
using System.Windows.Media;
using Clipora.Common;
using Clipora.Models;

namespace Clipora.ViewModels;

/// <summary>标签筛选 / 卡片标签显示共用的轻量 chip。</summary>
public sealed class TagChipViewModel : ObservableObject
{
    private bool _isActive;
    private bool _showDelete;

    public TagChipViewModel(Tag tag, bool isActive = false)
        : this(tag.Name, tag.Id, tag.Color, tag, isActive)
    {
    }

    public TagChipViewModel(string label, long? id, string color, bool isActive = false)
        : this(label, id, color, null, isActive)
    {
    }

    private TagChipViewModel(string label, long? id, string color, Tag? model, bool isActive)
    {
        Label = label;
        Id = id;
        Color = color;
        Model = model;
        _isActive = isActive;
        ColorBrush = MakeBrush(color);
    }

    public Tag? Model { get; }

    public long? Id { get; }

    public string Label { get; }

    public string Color { get; }

    public Brush ColorBrush { get; }

    public bool CanDelete => Model is not null;
    public bool ShowDelete => _showDelete && CanDelete;

    public bool IsActive
    {
        get => _isActive;
        set => Set(ref _isActive, value);
    }

    public void SetDeleteVisible(bool isVisible)
    {
        _showDelete = isVisible;
        OnPropertyChanged(nameof(ShowDelete));
    }

    internal static Brush MakeBrush(string color)
    {
        try
        {
            var brush = new SolidColorBrush((System.Windows.Media.Color)ColorConverter.ConvertFromString(color));
            brush.Freeze();
            return brush;
        }
        catch
        {
            var brush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x6B, 0x72, 0x80));
            brush.Freeze();
            return brush;
        }
    }
}

/// <summary>某张卡片可切换的标签项。</summary>
public sealed class ClipTagOptionViewModel : ObservableObject
{
    private bool _isAssigned;

    public ClipTagOptionViewModel(ClipItemViewModel clip, Tag tag, bool isAssigned)
    {
        Clip = clip;
        Model = tag;
        _isAssigned = isAssigned;
        ColorBrush = TagChipViewModel.MakeBrush(tag.Color);
    }

    public ClipItemViewModel Clip { get; }

    public Tag Model { get; }

    public long Id => Model.Id;

    public string Label => Model.Name;

    public Brush ColorBrush { get; }

    public bool IsAssigned
    {
        get => _isAssigned;
        set
        {
            if (Set(ref _isAssigned, value))
                OnPropertyChanged(nameof(IsActive));
        }
    }

    public bool IsActive => IsAssigned;
}
