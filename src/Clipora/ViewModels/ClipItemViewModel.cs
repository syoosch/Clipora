using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Clipora.Common;
using Clipora.Models;
using Clipora.Services;

namespace Clipora.ViewModels;

/// <summary>单条卡片的显示模型。</summary>
public sealed class ClipItemViewModel : ObservableObject
{
    private static readonly Regex HexColorRegex =
        new(@"^#(?:[0-9a-fA-F]{6}|[0-9a-fA-F]{3})$", RegexOptions.Compiled);
    private static readonly Regex RgbColorRegex =
        new(@"^rgba?\(\s*(\d{1,3})\s*,\s*(\d{1,3})\s*,\s*(\d{1,3})(?:\s*,\s*(?:0|1|0?\.\d+))?\s*\)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private bool _isTagEditorOpen;
    private bool _isReferenceInvalid;
    private Task<ImageSource?>? _thumbnailLoadTask;
    private ImageSource? _thumbnail;
    private bool _animateOnNextLoad;
    private readonly Func<ClipItem, object>? _groupKeyResolver;

    public ClipItemViewModel(
        ClipItem model,
        Func<ClipItem, object>? groupKeyResolver = null,
        bool animateOnNextLoad = false)
    {
        Model = model;
        _groupKeyResolver = groupKeyResolver;
        ClipFileManifest? fileManifest = model.Type == ClipType.File && !string.IsNullOrEmpty(model.RefPath)
            ? ClipFileManifest.Load(model.RefPath)
            : null;
        IsMultiFile = fileManifest is { Entries.Count: > 1 };
        IsDirectoryFile = IsFile && !IsMultiFile && fileManifest?.Entries[0].IsDirectory == true;
        FileCountText = IsMultiFile ? ContentClassifier.MakeFileCountLabel(fileManifest!.Entries) : string.Empty;
        FilePreviewName1 = IsMultiFile ? fileManifest!.Entries[0].DisplayName : string.Empty;
        FilePreviewName2 = IsMultiFile ? fileManifest!.Entries[1].DisplayName : string.Empty;
        Preview = BuildPreview(model, fileManifest);
        (UrlHost, UrlDisplay) = BuildUrlDisplay(model, Preview);
        (ColorPreviewBrush, ColorPreviewText) = DetectColorPreview(model, Preview);
        IsReferenceOnlyFile = DetectReferenceOnlyFile(model, fileManifest);
        _isReferenceInvalid = fileManifest?.IsReferenceInvalid == true;
        _animateOnNextLoad = animateOnNextLoad;

        // 单个可渲染图片文件：分类时已生成缩略图，让文件卡片也能像图片一样预览（仍是 File 类型）。
        IsSingleImageFile = IsSingleFile && !string.IsNullOrEmpty(model.ThumbnailPath);
        PreviewImagePath = ResolvePreviewImagePath(model, fileManifest, IsSingleImageFile);
    }

    public ClipItem Model { get; }

    public long Id => Model.Id;
    public ClipType Type => Model.Type;
    public string Preview { get; }
    public string? SourceApp => Model.SourceApp;
    public bool IsImage => Model.Type == ClipType.Image;
    public string? RefPath => Model.RefPath;
    public bool IsFile => Model.Type == ClipType.File;
    public bool IsMultiFile { get; }
    /// <summary>单个文件卡片，且其唯一条目为目录。</summary>
    public bool IsDirectoryFile { get; }
    public bool IsSingleFile => IsFile && !IsMultiFile;
    /// <summary>单个可渲染图片文件（带缩略图的 File 卡片）。</summary>
    public bool IsSingleImageFile { get; }
    /// <summary>是否显示缩略图：剪贴板图片，或带缩略图的图片文件。</summary>
    public bool HasThumbnail => IsImage || IsSingleImageFile;
    /// <summary>悬停大图预览的全图路径：图片项取 RefPath，图片文件取其副本/原文件路径。</summary>
    public string? PreviewImagePath { get; }
    public bool IsUrl => Model.Type == ClipType.Url;
    public bool IsCode => Model.Type == ClipType.Code;
    public bool IsColor => Model.Type == ClipType.Color;
    public bool IsStandardPreview => !IsImage && !IsFile && !IsUrl && !IsCode;

    /// <summary>卡片右上角操作按钮是否可见：多文件/文件夹隐藏，其余显示。</summary>
    public bool ShowActionButton => !IsMultiFile && !IsDirectoryFile;
    /// <summary>是否为可选取文字的类型（弹窗选取）。</summary>
    public bool IsSelectableType => Type is ClipType.Text or ClipType.RichText or ClipType.Code or ClipType.Color;
    /// <summary>操作按钮图标（Segoe Fluent Icons）。</summary>
    public string ActionButtonIcon => IsSelectableType ? "" : "";
    /// <summary>操作按钮 Tooltip。</summary>
    public string ActionButtonTooltip => IsSelectableType ? "选取" : "打开";
    public bool IsReferenceOnlyFile { get; }

    /// <summary>仅引用项的原路径在最近一次惰性检测中不可用。</summary>
    public bool IsReferenceInvalid
    {
        get => _isReferenceInvalid;
        private set
        {
            if (Set(ref _isReferenceInvalid, value))
                OnPropertyChanged(nameof(ReferenceStatusText));
        }
    }

    /// <summary>右下角状态文字："仅引用" 或 "已失效"。</summary>
    public string ReferenceStatusText => _isReferenceInvalid ? "已失效" : "仅引用";

    /// <summary>
    /// 设置失效状态（由点用/拖出检测后调用）。只在值变化时通知 UI，不触碰 manifest。
    /// </summary>
    public void SetReferenceInvalid(bool value) => IsReferenceInvalid = value;

    public string FileCountText { get; }
    public string FilePreviewName1 { get; }
    public string FilePreviewName2 { get; }
    public string UrlHost { get; }
    public string UrlDisplay { get; }
    public Brush? ColorPreviewBrush { get; }
    public string ColorPreviewText { get; }
    public bool HasColorPreview => ColorPreviewBrush is not null;
    /// <summary>缩略图（惰性异步加载：首次稳定进入视区后才从磁盘读取）。</summary>
    public ImageSource? Thumbnail => _thumbnail;

    /// <summary>显式触发缩略图加载。以 Dispatcher 后台优先级执行，避免抢占交互。</summary>
    public async Task EnsureThumbnailLoadedAsync()
    {
        if (!HasThumbnail)
            return;

        Task<ImageSource?> loadTask = _thumbnailLoadTask ??= QueueThumbnailLoadAsync(Model);
        ImageSource? thumbnail = await loadTask.ConfigureAwait(false);
        if (thumbnail is null)
        {
            // 瞬态文件/WIC 解码失败不能永久缓存 null；允许后续可见性或容器事件重新尝试。
            _ = Interlocked.CompareExchange(ref _thumbnailLoadTask, null, loadTask);
            return;
        }

        if (ReferenceEquals(_thumbnail, thumbnail))
            return;

        void ApplyThumbnail()
        {
            if (ReferenceEquals(_thumbnail, thumbnail))
                return;
            _thumbnail = thumbnail;
            OnPropertyChanged(nameof(Thumbnail));
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
            await dispatcher.InvokeAsync(ApplyThumbnail);
        else
            ApplyThumbnail();
    }

    private static async Task<ImageSource?> QueueThumbnailLoadAsync(ClipItem model)
    {
        Dispatcher? dispatcher = Application.Current?.Dispatcher;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            ImageSource? thumbnail = dispatcher is null
                ? LoadThumbnail(model)
                : await dispatcher.InvokeAsync(
                    () => LoadThumbnail(model),
                    DispatcherPriority.Background).Task.ConfigureAwait(false);
            if (thumbnail is not null)
                return thumbnail;
            if (attempt < 2)
                await Task.Delay(60).ConfigureAwait(false);
        }
        return null;
    }

    /// <summary>只让真正新捕获的卡片播放一次入场动画；滚动回收容器不重复播放。</summary>
    public bool TryTakeEntranceAnimation()
    {
        if (!_animateOnNextLoad)
            return false;

        _animateOnNextLoad = false;
        return true;
    }
    public ObservableCollection<TagChipViewModel> Tags { get; } = new();
    public ObservableCollection<ClipTagOptionViewModel> TagOptions { get; } = new();

    public bool IsPinned => Model.IsPinned;
    public bool HasTags => Tags.Count > 0;
    public bool HasTagOptions => TagOptions.Count > 0;

    public bool IsTagEditorOpen
    {
        get => _isTagEditorOpen;
        set => Set(ref _isTagEditorOpen, value);
    }

    /// <summary>用于排序（本地时间，倒序）。</summary>
    public DateTime CreatedAtLocal => Model.CreatedAt.ToLocalTime();

    public string TimeText => TimeFormat.Display(Model.CreatedAt);

    /// <summary>分组键对象：由 MainViewModel 解析为共享 <see cref="ClipGroupHeader"/>（支持折叠态保持）。
    /// 置顶项归入"置顶"组，其余按 8 小时段（凌晨/日间/晚间）。无 resolver 时回退为字符串键（自检/单测用）。</summary>
    public object GroupKey => _groupKeyResolver?.Invoke(Model) ?? ClipGrouping.KeyFor(Model);

    public string TypeLabel => Model.Type switch
    {
        ClipType.Text => "文字",
        ClipType.RichText => "富文本",
        ClipType.Url => "链接",
        ClipType.Code => "代码",
        ClipType.Image => "图片",
        ClipType.File => "文件",
        ClipType.Color => "颜色",
        _ => "",
    };

    /// <summary>类型柔和色标（用于卡片头部小圆点，一眼区分类型）。</summary>
    public Brush TypeColor => Model.Type == ClipType.Color && ColorPreviewBrush is not null
        ? ColorPreviewBrush
        : TypeBrush(Model.Type);

    private static Brush TypeBrush(ClipType type)
    {
        // 协调克制的中性偏冷配色（ui-ux-pro-max 色彩建议 + 沿用克制基调，去饱和）。
        string hex = type switch
        {
            ClipType.Text => "#6B7280",      // 冷灰
            ClipType.RichText => "#C08A4A",   // 柔琥珀
            ClipType.Url => "#3B73C4",        // 沉静蓝
            ClipType.Code => "#7E73C9",       // 柔紫
            ClipType.Image => "#4E9C7E",      // 柔绿
            ClipType.File => "#6E8AA8",       // 石板蓝
            ClipType.Color => "#8B5CF6",
            _ => "#6B7280",
        };
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }

    private static string BuildPreview(ClipItem model, ClipFileManifest? fileManifest)
    {
        string preview = model.PreviewText;
        if (model.Type != ClipType.File)
            return preview;

        preview = preview.Replace("（仅引用）", string.Empty, StringComparison.Ordinal).Trim();

        if (fileManifest is { Entries.Count: > 1 })
            return ContentClassifier.MakeFilePreview(fileManifest.Entries, fileManifest.IsReferenceOnly);

        string[] prefixes = { "文件 ", "文件夹 " };
        foreach (string prefix in prefixes)
        {
            if (preview.StartsWith(prefix, StringComparison.Ordinal))
                return preview[prefix.Length..].TrimStart();
        }

        return preview;
    }

    private static bool DetectReferenceOnlyFile(ClipItem model, ClipFileManifest? fileManifest)
    {
        if (model.Type != ClipType.File)
            return false;

        if (model.PreviewText.Contains("（仅引用）", StringComparison.Ordinal))
            return true;

        return fileManifest?.IsReferenceOnly == true;
    }

    private static (string Host, string Display) BuildUrlDisplay(ClipItem model, string preview)
    {
        if (model.Type != ClipType.Url)
            return (string.Empty, string.Empty);

        string raw = (model.TextContent ?? preview).Trim();
        if (!Uri.TryCreate(raw, UriKind.Absolute, out Uri? uri))
            return (preview, raw);

        string host = uri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase)
            ? uri.Host[4..]
            : uri.Host;
        return (host, raw);
    }

    private static (Brush? Brush, string Text) DetectColorPreview(ClipItem model, string preview)
    {
        if (model.Type != ClipType.Color)
            return (null, string.Empty);

        string text = (model.TextContent ?? preview).Trim();
        if (string.IsNullOrWhiteSpace(text))
            return (null, string.Empty);

        Match hex = HexColorRegex.Match(text);
        if (hex.Success && TryCreateHexBrush(hex.Value, out Brush? hexBrush))
            return (hexBrush, hex.Value.ToUpperInvariant());

        Match rgb = RgbColorRegex.Match(text);
        if (rgb.Success && TryCreateRgbBrush(rgb, out Brush? rgbBrush))
            return (rgbBrush, rgb.Value);

        return (null, string.Empty);
    }

    private static bool TryCreateHexBrush(string value, out Brush? brush)
    {
        brush = null;
        string hex = value.TrimStart('#');
        if (hex.Length == 3)
            hex = string.Concat(hex.Select(c => new string(c, 2)));

        try
        {
            var color = (Color)ColorConverter.ConvertFromString("#" + hex);
            brush = FreezeBrush(color);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryCreateRgbBrush(Match match, out Brush? brush)
    {
        brush = null;
        if (!byte.TryParse(match.Groups[1].Value, out byte r) ||
            !byte.TryParse(match.Groups[2].Value, out byte g) ||
            !byte.TryParse(match.Groups[3].Value, out byte b))
            return false;

        brush = FreezeBrush(Color.FromRgb(r, g, b));
        return true;
    }

    private static Brush FreezeBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    /// <summary>置顶状态变化后刷新相关显示。</summary>
    public void NotifyPinnedChanged()
    {
        OnPropertyChanged(nameof(IsPinned));
        OnPropertyChanged(nameof(GroupKey));
    }

    /// <summary>定时刷新时间显示（避免"今天 HH:mm"等跨刻度不更新）。</summary>
    public void NotifyTimeChanged() => OnPropertyChanged(nameof(TimeText));

    public void SetTags(IReadOnlyList<Tag> allTags, IReadOnlyCollection<long> assignedIds)
    {
        Tags.Clear();
        TagOptions.Clear();

        var assigned = assignedIds.ToHashSet();
        foreach (Tag tag in allTags)
        {
            bool isAssigned = assigned.Contains(tag.Id);
            if (isAssigned)
                Tags.Add(new TagChipViewModel(tag));
            TagOptions.Add(new ClipTagOptionViewModel(this, tag, isAssigned));
        }

        OnPropertyChanged(nameof(HasTags));
        OnPropertyChanged(nameof(HasTagOptions));
    }

    private static ImageSource? LoadThumbnail(ClipItem model)
    {
        if (string.IsNullOrEmpty(model.ThumbnailPath) || !File.Exists(model.ThumbnailPath))
            return null;

        try
        {
            BitmapSource thumbnail = ClipboardImageNormalizer.LoadAndRepair(model.ThumbnailPath, out bool repaired);
            // 仅剪贴板图片项的 RefPath 是图片本体；图片文件的 RefPath 是 manifest，不可当图片修复。
            if (repaired && model.Type == ClipType.Image && !string.IsNullOrEmpty(model.RefPath) && File.Exists(model.RefPath))
                ClipboardImageNormalizer.LoadAndRepair(model.RefPath, out _);
            return thumbnail;
        }
        catch
        {
            return null;
        }
    }

    private static string? ResolvePreviewImagePath(ClipItem model, ClipFileManifest? fileManifest, bool isSingleImageFile)
    {
        if (model.Type == ClipType.Image)
            return model.RefPath;

        if (!isSingleImageFile || fileManifest is not { Entries.Count: 1 })
            return null;

        ClipFileManifestEntry entry = fileManifest.Entries[0];
        string? path = !string.IsNullOrWhiteSpace(entry.StoredPath) ? entry.StoredPath : entry.OriginalPath;
        return !string.IsNullOrWhiteSpace(path) && File.Exists(path) ? path : null;
    }
}
