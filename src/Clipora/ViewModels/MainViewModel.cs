using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using Clipora.Abstractions;
using Clipora.Common;
using Clipora.Models;
using Clipora.Services;

namespace Clipora.ViewModels;

/// <summary>主面板：全量加载 + 8h 时间段可折叠分组（WPF 原生 ICollectionView + 虚拟化）。</summary>
public sealed class MainViewModel : ObservableObject
{
    private readonly IClipStore _store;
    private readonly ITagStore _tagStore;
    private readonly TagManagement _tagManagement;
    private readonly IClipboardMonitor _monitor;
    private readonly IClipWriter _writer;

    private string _searchText = string.Empty;
    private string _newTagName = string.Empty;
    private bool _isFilterOpen;
    private bool _isTagEditorOpen;
    private bool _isTagManagerOpen;
    private ClipType? _activeType;
    private long? _activeTagId;
    private FilterMode _filterMode = FilterMode.Type;

    /// <summary>时间段分组策略 + 折叠态缓存（段键/默认展开/捕获展开/置顶规则/点击翻转收口于此）。</summary>
    private readonly ClipGrouping _grouping = new();
    private readonly DispatcherTimer _midnightTimer = new();

    private static readonly string[] TagPalette =
    {
        "#6B7280", "#6E8AA8", "#4E9C7E", "#C08A4A", "#7E73C9", "#3B73C4", "#B86B6B", "#7A8A63",
    };

    /// <summary>规范数据源（全量 ClipItemViewModel）。列表绑定经 <see cref="ItemsView"/>。</summary>
    public ObservableCollection<ClipItemViewModel> Items { get; } = new();

    /// <summary>列表绑定源：WPF 原生 ICollectionView——置顶优先 + 时间倒序 + 8h 段分组，
    /// LiveSorting/LiveGrouping 单项精准移动、零闪烁；分组可折叠（折叠态在 <see cref="ClipGroupHeader"/>）。</summary>
    public ICollectionView ItemsView { get; }

    public ObservableCollection<FilterModeOption> FilterModes { get; }

    public ObservableCollection<TypeFilter> TypeFilters { get; }
    public ObservableCollection<TagChipViewModel> TagFilters { get; } = new();
    public ObservableCollection<TagManagementItemViewModel> ManagedTags { get; } = new();

    public RelayCommand UseCommand { get; }
    public RelayCommand ToggleFilterModeCommand { get; }
    public RelayCommand SelectFilterModeCommand { get; }
    public RelayCommand SelectTypeCommand { get; }
    public RelayCommand SelectTagCommand { get; }
    public RelayCommand CreateTagCommand { get; }
    public RelayCommand SubmitNewTagCommand { get; }
    public RelayCommand DeleteTagCommand { get; }
    public RelayCommand ToggleFilterCommand { get; }
    public RelayCommand PinCommand { get; }
    public RelayCommand DeleteCommand { get; }
    public RelayCommand ToggleTagEditorCommand { get; }
    public RelayCommand ToggleClipTagCommand { get; }
    public RelayCommand ToggleTagManagerCommand { get; }
    public RelayCommand CloseTagManagerCommand { get; }
    public RelayCommand CardActionCommand { get; }
    public RelayCommand CloseSelectionPopupCommand { get; }

    /// <summary>搜索框 + 类型筛选是否展开（默认收起）。</summary>
    public bool IsFilterOpen
    {
        get => _isFilterOpen;
        set => Set(ref _isFilterOpen, value);
    }

    /// <summary>是否有可见条目（用于空状态占位）。</summary>
    public bool HasVisibleItems => Items.Count > 0;

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (Set(ref _searchText, value))
                ItemsView.Refresh();
        }
    }

    public string NewTagName
    {
        get => _newTagName;
        set => Set(ref _newTagName, value);
    }

    public bool IsTypeFilterMode => _filterMode == FilterMode.Type;
    public bool IsTagFilterMode => _filterMode == FilterMode.Tag;
    public bool HasTagFilters => TagFilters.Any(t => t.Model is not null);
    public string ModeToggleLabel => _filterMode == FilterMode.Type ? "类型" : "标签";
    public bool HasOpenClipTagEditor => Items.Any(i => i.IsTagEditorOpen);
    public ClipItemViewModel? OpenClipTagEditor => Items.FirstOrDefault(i => i.IsTagEditorOpen);
    public bool HasManagedTags => ManagedTags.Count > 0;
    public bool IsTagManagerOpen
    {
        get => _isTagManagerOpen;
        set => Set(ref _isTagManagerOpen, value);
    }

    public bool IsTagEditorOpen
    {
        get => _isTagEditorOpen;
        set
        {
            if (Set(ref _isTagEditorOpen, value))
                SyncTagDeleteState();
        }
    }

    public MainViewModel(IClipStore store, ITagStore tagStore, IClipboardMonitor monitor, IClipWriter writer)
    {
        _store = store;
        _tagStore = tagStore;
        _tagManagement = new TagManagement(tagStore);
        _monitor = monitor;
        _writer = writer;

        FilterModes = new ObservableCollection<FilterModeOption>
        {
            new("类型", FilterMode.Type, isActive: true),
            new("标签", FilterMode.Tag),
        };

        TypeFilters = new ObservableCollection<TypeFilter>
        {
            new("全部", null, isActive: true),
            new("文字", ClipType.Text),
            new("图片", ClipType.Image),
            new("文件", ClipType.File),
            new("链接", ClipType.Url),
            new("颜色", ClipType.Color),
            new("代码", ClipType.Code),
            new("富文本", ClipType.RichText),
        };

        LoadTags();

        UseCommand = new RelayCommand(p =>
        {
            if (p is ClipItemViewModel vm)
                Use(vm);
        });

        ToggleFilterModeCommand = new RelayCommand(_ => ToggleFilterMode());

        SelectFilterModeCommand = new RelayCommand(p =>
        {
            if (p is FilterModeOption option)
            {
                if (SetFilterMode(option.Mode))
                    RefreshView();
            }
        });

        SelectTypeCommand = new RelayCommand(p =>
        {
            if (p is not TypeFilter selected)
                return;
            foreach (TypeFilter f in TypeFilters)
                f.IsActive = ReferenceEquals(f, selected);
            bool typeChanged = _activeType != selected.Type;
            _activeType = selected.Type;
            bool modeChangedFilter = SetFilterMode(FilterMode.Type);
            if (typeChanged || modeChangedFilter)
                RefreshView();
        });

        SelectTagCommand = new RelayCommand(p =>
        {
            if (p is not TagChipViewModel selected)
                return;
            foreach (TagChipViewModel f in TagFilters)
                f.IsActive = ReferenceEquals(f, selected);
            bool tagChanged = _activeTagId != selected.Id;
            _activeTagId = selected.Id;
            bool modeChangedFilter = SetFilterMode(FilterMode.Tag);
            if (tagChanged || modeChangedFilter)
                RefreshView();
        });

        CreateTagCommand = new RelayCommand(_ => ToggleOrCreateTag());
        SubmitNewTagCommand = new RelayCommand(_ => SubmitNewTag());
        DeleteTagCommand = new RelayCommand(p =>
        {
            if (p is TagChipViewModel tag)
                DeleteTag(tag);
        });

        ToggleFilterCommand = new RelayCommand(_ => IsFilterOpen = !IsFilterOpen);

        PinCommand = new RelayCommand(p =>
        {
            if (p is ClipItemViewModel vm)
                TogglePin(vm);
        });

        DeleteCommand = new RelayCommand(p =>
        {
            if (p is ClipItemViewModel vm)
                Delete(vm);
        });

        CardActionCommand = new RelayCommand(p =>
        {
            if (p is ClipItemViewModel vm)
                CardAction(vm);
        });

        CloseSelectionPopupCommand = new RelayCommand(_ => CloseSelectionPopup());

        ToggleTagEditorCommand = new RelayCommand(p =>
        {
            if (p is ClipItemViewModel vm)
                ToggleTagEditor(vm);
        });

        ToggleClipTagCommand = new RelayCommand(p =>
        {
            if (p is ClipTagOptionViewModel option)
                ToggleClipTag(option);
        });

        ToggleTagManagerCommand = new RelayCommand(_ => ToggleTagManager());
        CloseTagManagerCommand = new RelayCommand(_ => IsTagManagerOpen = false);

        ItemsView = CollectionViewSource.GetDefaultView(Items);
        ItemsView.SortDescriptions.Add(new SortDescription(nameof(ClipItemViewModel.IsPinned), ListSortDirection.Descending));
        ItemsView.SortDescriptions.Add(new SortDescription(nameof(ClipItemViewModel.CreatedAtLocal), ListSortDirection.Descending));
        ItemsView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ClipItemViewModel.GroupKey)));
        ItemsView.Filter = PassesFilter;
        if (ItemsView is ICollectionViewLiveShaping live)
        {
            live.IsLiveSorting = true;
            live.IsLiveGrouping = true;
        }

        Load();
        _monitor.ClipCaptured += OnClipCaptured;

        // 跨零点刷新：分组标题（今天→昨天）与卡片时间显示。段键按绝对日期，故新内容自动落入正确日期段，
        // 无需重分组；本计时器只负责刷新相对标签。
        _midnightTimer.Tick += OnMidnightTick;
        ScheduleMidnight();
    }

    private void ScheduleMidnight()
    {
        DateTime now = DateTime.Now;
        TimeSpan untilMidnight = now.Date.AddDays(1) - now;
        if (untilMidnight < TimeSpan.FromSeconds(1))
            untilMidnight = TimeSpan.FromSeconds(1);
        _midnightTimer.Interval = untilMidnight;
        _midnightTimer.Start();
    }

    private void OnMidnightTick(object? sender, EventArgs e)
    {
        _midnightTimer.Stop();
        _grouping.NotifyDateChanged();
        foreach (ClipItemViewModel vm in Items)
            vm.NotifyTimeChanged();
        ScheduleMidnight();
    }

    /// <summary>使用后触发（供上层执行隐藏面板 + 自动粘贴）。</summary>
    public event Action<ClipItem>? Used;

    /// <summary>仅引用文件原路径不可用时触发，供窗口层显示瞬时提示。</summary>
    public event Action? FileReferenceUnavailable;

    /// <summary>文件/链接打开失败时触发，供窗口层显示瞬时提示。</summary>
    public event Action<string>? OpenFailed;

    // —— 文字选取 Popup 状态 ——
    private string _selectionPopupText = string.Empty;
    private bool _isSelectionPopupCode;
    private bool _isSelectionPopupOpen;

    /// <summary>选取弹窗中显示的完整文字。</summary>
    public string SelectionPopupText
    {
        get => _selectionPopupText;
        set => Set(ref _selectionPopupText, value);
    }

    /// <summary>选取弹窗内容是否为代码（等宽字体）。</summary>
    public bool IsSelectionPopupCode
    {
        get => _isSelectionPopupCode;
        set => Set(ref _isSelectionPopupCode, value);
    }

    /// <summary>选取弹窗是否可见。</summary>
    public bool IsSelectionPopupOpen
    {
        get => _isSelectionPopupOpen;
        set => Set(ref _isSelectionPopupOpen, value);
    }

    /// <summary>选取弹窗字数统计文本。</summary>
    public string SelectionPopupCharCount => $"字数: {_selectionPopupText.Length:N0}";

    /// <summary>导入用户主动拖入主面板的外部数据。</summary>
    public bool ImportExternalDrop(IDataObject dataObject) => _monitor.Import(dataObject);

    /// <summary>再次使用：写回剪贴板，并通知上层。仅引用失效时不触发自动粘贴。</summary>
    public void Use(ClipItemViewModel vm)
    {
        bool dismissedOtherTagEditor = DismissOpenTagEditors(vm);
        if (dismissedOtherTagEditor || vm.IsTagEditorOpen)
            return;

        ClipItem model = _store.GetById(vm.Id) ?? vm.Model;
        ClipWriteResult result = _writer.Write(model);

        if (result == ClipWriteResult.ReferenceUnavailable)
        {
            vm.SetReferenceInvalid(true);
            FileReferenceUnavailable?.Invoke();
            return;
        }

        // Completed：若为仅引用项则清除失效状态
        if (vm.IsReferenceOnlyFile)
            vm.SetReferenceInvalid(false);

        Used?.Invoke(model);
    }

    /// <summary>卡片右上角操作按钮：文字类型弹窗选取，其他类型系统打开。</summary>
    private void CardAction(ClipItemViewModel vm)
    {
        if (vm.IsSelectableType)
        {
            OpenSelectionPopup(vm);
            return;
        }

        // 链接：浏览器打开
        if (vm.Type == ClipType.Url)
        {
            string? url = vm.Model.TextContent ?? vm.Preview;
            if (!string.IsNullOrWhiteSpace(url))
                TryOpenWithDefaultApp(url, "无法打开链接");
            return;
        }

        // 图片：设只读后打开
        if (vm.Type == ClipType.Image)
        {
            string? imagePath = vm.RefPath;
            if (!string.IsNullOrWhiteSpace(imagePath) && File.Exists(imagePath))
            {
                TrySetReadOnly(imagePath);
                TryOpenWithDefaultApp(imagePath, "无法打开图片");
            }
            return;
        }

        // 单文件
        if (vm.Type == ClipType.File && vm.IsSingleFile)
        {
            OpenSingleFile(vm);
            return;
        }
    }

    /// <summary>打开单个文件卡片：已保存 → 设只读打开；仅引用有效 → 直接打开；失效 → toast。</summary>
    private void OpenSingleFile(ClipItemViewModel vm)
    {
        ClipFileManifest? manifest = null;
        if (!string.IsNullOrEmpty(vm.Model.RefPath))
        {
            try { manifest = ClipFileManifest.Load(vm.Model.RefPath); }
            catch { manifest = null; }
        }

        if (manifest is not { Entries.Count: 1 })
            return;

        ClipFileManifestEntry entry = manifest.Entries[0];

        // 仅引用文件
        if (manifest.IsReferenceOnly)
        {
            var validation = ClipFileReferenceValidator.Validate(vm.Model);
            if (!validation.IsValid)
            {
                vm.SetReferenceInvalid(true);
                FileReferenceUnavailable?.Invoke();
                return;
            }

            if (validation.Paths.Count > 0)
                TryOpenWithDefaultApp(validation.Paths[0], "无法打开文件");
            return;
        }

        // 已保存文件：设只读后打开备份
        string? storedPath = entry.StoredPath;
        if (!string.IsNullOrWhiteSpace(storedPath) && File.Exists(storedPath))
        {
            TrySetReadOnly(storedPath);
            TryOpenWithDefaultApp(storedPath, "无法打开文件");
        }
    }

    /// <summary>打开文字选取弹窗。</summary>
    private void OpenSelectionPopup(ClipItemViewModel vm)
    {
        SelectionPopupText = vm.Model.TextContent ?? string.Empty;
        IsSelectionPopupCode = vm.IsCode;
        OnPropertyChanged(nameof(SelectionPopupCharCount));
        IsSelectionPopupOpen = true;
    }

    /// <summary>关闭文字选取弹窗。</summary>
    private void CloseSelectionPopup()
    {
        IsSelectionPopupOpen = false;
    }

    /// <summary>设置文件为只读，防止外部编辑覆盖 Clipora 备份。</summary>
    private static void TrySetReadOnly(string path)
    {
        try
        {
            FileAttributes attrs = File.GetAttributes(path);
            if ((attrs & FileAttributes.ReadOnly) == 0)
                File.SetAttributes(path, attrs | FileAttributes.ReadOnly);
        }
        catch
        {
            // best-effort：设只读失败不影响打开
        }
    }

    /// <summary>用系统默认程序打开文件/链接，失败时触发提示。</summary>
    private void TryOpenWithDefaultApp(string path, string failMessage)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            OpenFailed?.Invoke($"{failMessage}: {ex.Message}");
        }
    }


    private bool SetFilterMode(FilterMode mode)
    {
        if (_filterMode == mode)
            return false;

        FilterMode previousMode = _filterMode;
        _filterMode = mode;
        foreach (FilterModeOption option in FilterModes)
            option.IsActive = option.Mode == mode;
        OnPropertyChanged(nameof(IsTypeFilterMode));
        OnPropertyChanged(nameof(IsTagFilterMode));
        OnPropertyChanged(nameof(ModeToggleLabel));
        return ModeHasActiveFilter(previousMode) || ModeHasActiveFilter(mode);
    }

    /// <summary>刷新列表视图（重新跑筛选/排序/分组）。分组折叠态由共享 ClipGroupHeader 缓存保持。</summary>
    private void RefreshView() => ItemsView.Refresh();

    /// <summary>供窗口层点击分组标题时调用：翻转该组展开态（置顶组忽略），不触碰集合。</summary>
    public void ToggleGroup(ClipGroupHeader group) => _grouping.Toggle(group);

    public void ToggleFilterMode()
    {
        if (SetFilterMode(_filterMode == FilterMode.Type ? FilterMode.Tag : FilterMode.Type))
            RefreshView();
    }

    public bool DismissOpenTagEditors(ClipItemViewModel? except = null)
    {
        bool dismissed = false;
        foreach (ClipItemViewModel item in Items)
        {
            if (!item.IsTagEditorOpen || ReferenceEquals(item, except))
                continue;

            item.IsTagEditorOpen = false;
            dismissed = true;
        }

        return dismissed;
    }

    private bool ModeHasActiveFilter(FilterMode mode) =>
        mode == FilterMode.Type ? _activeType is not null : _activeTagId is not null;

    private void ToggleTagManager()
    {
        if (IsTagManagerOpen)
        {
            IsTagManagerOpen = false;
            return;
        }

        DismissOpenTagEditors();
        IsTagEditorOpen = false;
        NewTagName = string.Empty;
        LoadManagedTags();
        IsTagManagerOpen = true;
    }

    private void LoadManagedTags()
    {
        ManagedTags.Clear();
        IReadOnlyList<Tag> tags = _tagStore.List();
        for (int i = 0; i < tags.Count; i++)
        {
            var vm = new TagManagementItemViewModel(
                tags[i],
                TagPalette,
                CommitManagedTagRename,
                DeleteManagedTag,
                MoveManagedTag,
                SetManagedTagColor);
            vm.SetPosition(i == 0, i == tags.Count - 1);
            ManagedTags.Add(vm);
        }

        OnPropertyChanged(nameof(HasManagedTags));
    }

    private void CommitManagedTagRename(TagManagementItemViewModel tag)
    {
        switch (_tagManagement.Rename(tag.Id, tag.OriginalName, tag.Name))
        {
            case TagRenameOutcome.Rejected:
                tag.Name = tag.OriginalName; // 空/重复：回退显示名
                break;
            case TagRenameOutcome.Renamed:
                SyncTagsAfterManagementChange(refreshItemsView: false);
                break;
            // Unchanged：无操作
        }
    }

    private void DeleteManagedTag(TagManagementItemViewModel tag)
    {
        bool activeDeleted = _activeTagId == tag.Id;
        _tagManagement.Delete(tag.Id);
        if (activeDeleted)
            _activeTagId = null;

        SyncTagsAfterManagementChange(refreshItemsView: activeDeleted);
    }

    private void MoveManagedTag(TagManagementItemViewModel tag, int direction)
    {
        if (_tagManagement.Move(tag.Id, direction))
            SyncTagsAfterManagementChange(refreshItemsView: false);
    }

    private void SetManagedTagColor(TagManagementItemViewModel tag, string color)
    {
        if (_tagManagement.SetColor(tag.Id, tag.Color, color))
            SyncTagsAfterManagementChange(refreshItemsView: false);
    }

    private void SyncTagsAfterManagementChange(bool refreshItemsView)
    {
        LoadTags();
        if (IsTagManagerOpen)
            LoadManagedTags();
        RefreshAllClipTags();
        if (refreshItemsView)
            RefreshView();
    }

    private void LoadTags()
    {
        TagFilters.Clear();
        TagFilters.Add(new TagChipViewModel("全部标签", null, "#6B7280", _activeTagId is null));

        foreach (Tag tag in _tagStore.List())
            TagFilters.Add(new TagChipViewModel(tag, _activeTagId == tag.Id));

        SyncTagDeleteState();
        OnPropertyChanged(nameof(HasTagFilters));
    }

    private void SyncTagDeleteState()
    {
        foreach (TagChipViewModel tag in TagFilters)
            tag.SetDeleteVisible(IsTagEditorOpen);
    }

    private IReadOnlyList<Tag> CurrentTags() =>
        TagFilters
            .Where(t => t.Model is not null)
            .Select(t => t.Model!)
            .ToArray();

    private void ToggleOrCreateTag()
    {
        if (!IsTagEditorOpen)
        {
            IsTagEditorOpen = true;
            if (SetFilterMode(FilterMode.Tag))
                RefreshView();
            return;
        }

        if (string.IsNullOrWhiteSpace(NewTagName))
        {
            IsTagEditorOpen = false;
            return;
        }

        CreateTag();
    }

    private void SubmitNewTag()
    {
        if (string.IsNullOrWhiteSpace(NewTagName))
            return;

        CreateTag();
    }

    private void CreateTag()
    {
        string name = NewTagName.Trim();
        if (string.IsNullOrWhiteSpace(name))
            return;

        TagChipViewModel? existing = TagFilters.FirstOrDefault(
            t => string.Equals(t.Label, name, StringComparison.OrdinalIgnoreCase));
        if (existing is not null && existing.Model is not null)
        {
            NewTagName = string.Empty;
            IsTagEditorOpen = false;
            SelectTag(existing);
            return;
        }

        _tagStore.Create(name, PickTagColor());
        NewTagName = string.Empty;
        IsTagEditorOpen = false;
        LoadTags();
        if (IsTagManagerOpen)
            LoadManagedTags();
        SetFilterMode(FilterMode.Tag);
        RefreshAllClipTags();
        RefreshView();
    }

    private void DeleteTag(TagChipViewModel tag)
    {
        if (tag.Model is null)
            return;

        long deletedId = tag.Model.Id;
        _tagStore.Delete(deletedId);
        if (_activeTagId == deletedId)
            _activeTagId = null;

        LoadTags();
        if (IsTagManagerOpen)
            LoadManagedTags();
        RefreshAllClipTags();
        RefreshView();
    }

    private void SelectTag(TagChipViewModel selected)
    {
        foreach (TagChipViewModel f in TagFilters)
            f.IsActive = ReferenceEquals(f, selected);
        bool tagChanged = _activeTagId != selected.Id;
        _activeTagId = selected.Id;
        bool modeChangedFilter = SetFilterMode(FilterMode.Tag);
        if (tagChanged || modeChangedFilter)
            RefreshView();
    }

    private void ToggleTagEditor(ClipItemViewModel selected)
    {
        foreach (ClipItemViewModel item in Items)
        {
            if (!ReferenceEquals(item, selected))
                item.IsTagEditorOpen = false;
        }

        selected.IsTagEditorOpen = !selected.IsTagEditorOpen;
    }

    private void ToggleClipTag(ClipTagOptionViewModel option)
    {
        if (option.IsAssigned)
            _tagStore.Unassign(option.Clip.Id, option.Id);
        else
            _tagStore.Assign(option.Clip.Id, option.Id);

        RefreshClipTags(option.Clip);
        RefreshView();
    }

    private void RefreshAllClipTags()
    {
        foreach (ClipItemViewModel vm in Items)
            RefreshClipTags(vm);
    }

    private void RefreshClipTags(ClipItemViewModel vm)
    {
        vm.SetTags(CurrentTags(), _tagStore.GetTagIds(vm.Id));
    }

    private void TogglePin(ClipItemViewModel vm)
    {
        bool pinned = !vm.Model.IsPinned;
        _store.SetPinned(vm.Id, pinned);
        vm.Model.IsPinned = pinned;
        vm.NotifyPinnedChanged();
        RefreshView();
    }

    private void Delete(ClipItemViewModel vm)
    {
        _store.SoftDelete(vm.Id);
        Items.Remove(vm);
        OnPropertyChanged(nameof(HasVisibleItems));
        RefreshView();
    }

    private bool PassesFilter(object obj)
    {
        if (obj is not ClipItemViewModel vm) return false;
        return PassesItem(vm);
    }

    private bool PassesItem(ClipItemViewModel vm)
    {
        if (IsTypeFilterMode && _activeType is { } type && vm.Type != type)
            return false;
        if (IsTagFilterMode && _activeTagId is { } tagId && !vm.Tags.Any(t => t.Id == tagId))
            return false;

        if (!string.IsNullOrWhiteSpace(_searchText))
        {
            string needle = _searchText.Trim();
            bool inPreview = vm.Preview.Contains(needle, StringComparison.OrdinalIgnoreCase);
            bool inContent = vm.Model.TextContent?.Contains(needle, StringComparison.OrdinalIgnoreCase) ?? false;
            bool inOcr = vm.Model.OcrText?.Contains(needle, StringComparison.OrdinalIgnoreCase) ?? false;
            bool inOcrNoSpace = !inOcr && vm.Model.OcrText is { } ocr
                && ocr.Replace(" ", "").Contains(needle.Replace(" ", ""), StringComparison.OrdinalIgnoreCase);
            if (!inPreview && !inContent && !inOcr && !inOcrNoSpace)
                return false;
        }

        return true;
    }

    /// <summary>重新加载列表（保留期清理 / 设置变更后调用）。</summary>
    public void Reload()
    {
        Load();
        RefreshView();
    }

    private void Load()
    {
        _grouping.Clear();
        Items.Clear();

        // 一次性批量读取标签关联，避免逐条 GetTagIds 的 N+1 查询（全量加载关键支柱之一）。
        IReadOnlyDictionary<long, IReadOnlyList<long>> tagMap = _tagStore.GetAllTagAssignments();
        IReadOnlyList<Tag> allTags = CurrentTags();

        var loaded = new List<ClipItem>();
        foreach (ClipItem item in _store.Query(new ClipQuery { Take = int.MaxValue }))
        {
            var vm = new ClipItemViewModel(item, _grouping.Resolve);
            vm.SetTags(allTags, tagMap.GetValueOrDefault(item.Id, Array.Empty<long>()));
            _grouping.Resolve(item); // 预填充分组缓存，供默认展开计算
            Items.Add(vm);
            loaded.Add(item);
        }

        _grouping.ApplyDefaultExpansion(loaded);
        OnPropertyChanged(nameof(HasVisibleItems));
    }

    private void OnClipCaptured(object? sender, ClipItem item)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
            dispatcher.Invoke(() => AddOrBump(item));
        else
            AddOrBump(item);
    }

    private void AddOrBump(ClipItem item)
    {
        ClipItemViewModel? existing = Items.FirstOrDefault(x => x.Id == item.Id);
        if (existing is not null)
            Items.Remove(existing);

        ClipItemViewModel newVm = CreateClipViewModel(item);
        Items.Add(newVm);

        // 新捕获图片应立即开始异步加载，不能依赖虚拟化容器稍后是否触发 Loaded/DataContextChanged。
        // 历史图片仍保持进入视区后惰性加载；这里只处理本次新增的一张卡片。
        if (newVm.IsImage)
            _ = newVm.EnsureThumbnailLoadedAsync();

        // 保证新捕获条目所属分组展开，避免复制后内容落入折叠组而"看不见"。
        _grouping.ExpandFor(item);

        OnPropertyChanged(nameof(HasVisibleItems));
        // ICollectionView + LiveSorting/LiveGrouping 自动把该条精准移入对应分组，无需重建。
    }

    private ClipItemViewModel CreateClipViewModel(ClipItem item)
    {
        var vm = new ClipItemViewModel(item, _grouping.Resolve, animateOnNextLoad: true);
        RefreshClipTags(vm);
        return vm;
    }

    private static string PickTagColor()
    {
        return TagPalette[Random.Shared.Next(TagPalette.Length)];
    }
}
