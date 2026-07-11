using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Windows.Media;
using Clipora.Common;
using Clipora.Models;

namespace Clipora.ViewModels;

/// <summary>标签管理弹层中的一行标签。</summary>
public sealed class TagManagementItemViewModel : ObservableObject
{
    private readonly Action<TagManagementItemViewModel> _commitRename;
    private readonly Action<TagManagementItemViewModel> _delete;
    private readonly Action<TagManagementItemViewModel, int> _move;
    private readonly Action<TagManagementItemViewModel, string> _setColor;
    private string _name;
    private bool _isFirst;
    private bool _isLast;

    public TagManagementItemViewModel(
        Tag model,
        IReadOnlyList<string> palette,
        Action<TagManagementItemViewModel> commitRename,
        Action<TagManagementItemViewModel> delete,
        Action<TagManagementItemViewModel, int> move,
        Action<TagManagementItemViewModel, string> setColor)
    {
        Model = model;
        _name = model.Name;
        _commitRename = commitRename;
        _delete = delete;
        _move = move;
        _setColor = setColor;

        foreach (string color in palette)
            ColorOptions.Add(new TagColorOptionViewModel(color, string.Equals(color, model.Color, StringComparison.OrdinalIgnoreCase)));

        CommitRenameCommand = new RelayCommand(_ => _commitRename(this));
        DeleteCommand = new RelayCommand(_ => _delete(this));
        MoveUpCommand = new RelayCommand(_ => _move(this, -1));
        MoveDownCommand = new RelayCommand(_ => _move(this, 1));
        SelectColorCommand = new RelayCommand(p =>
        {
            if (p is TagColorOptionViewModel option)
                _setColor(this, option.Color);
        });
    }

    public Tag Model { get; }

    public long Id => Model.Id;

    public string OriginalName => Model.Name;

    public string Name
    {
        get => _name;
        set => Set(ref _name, value);
    }

    public string Color => Model.Color;

    public Brush ColorBrush => TagChipViewModel.MakeBrush(Model.Color);

    public ObservableCollection<TagColorOptionViewModel> ColorOptions { get; } = new();

    public RelayCommand CommitRenameCommand { get; }

    public RelayCommand DeleteCommand { get; }

    public RelayCommand MoveUpCommand { get; }

    public RelayCommand MoveDownCommand { get; }

    public RelayCommand SelectColorCommand { get; }

    public bool CanMoveUp => !_isFirst;

    public bool CanMoveDown => !_isLast;

    public void SetPosition(bool isFirst, bool isLast)
    {
        if (Set(ref _isFirst, isFirst, nameof(CanMoveUp)))
            OnPropertyChanged(nameof(CanMoveUp));
        if (Set(ref _isLast, isLast, nameof(CanMoveDown)))
            OnPropertyChanged(nameof(CanMoveDown));
    }
}

public sealed class TagColorOptionViewModel : ObservableObject
{
    private bool _isSelected;

    public TagColorOptionViewModel(string color, bool isSelected)
    {
        Color = color;
        ColorBrush = TagChipViewModel.MakeBrush(color);
        _isSelected = isSelected;
    }

    public string Color { get; }

    public Brush ColorBrush { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set => Set(ref _isSelected, value);
    }
}
