using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Clipora.Controls;

/// <summary>
/// 设置行控件：标题 + info 图标（hover 浮窗预览描述、click 展开/收起）+ 右侧控件槽。
/// 适用于设置页所有行类型（ToggleSwitch / ComboBox / 快捷键 / 按钮等）。
/// 继承 ContentControl 而非 UserControl，避免引入独立 NameScope 导致 x:Name 注册失败。
/// </summary>
public class SettingsRowControl : ContentControl
{
    /// <summary>全局实例追踪，用于离开设置页时批量重置 IsExpanded。</summary>
    private static readonly List<WeakReference<SettingsRowControl>> Instances = [];

    #region Title

    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(nameof(Title), typeof(string), typeof(SettingsRowControl),
            new PropertyMetadata(string.Empty, OnTitleChanged));

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    private static void OnTitleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is SettingsRowControl c && c._titleBlock is not null)
            c._titleBlock.Text = (string)e.NewValue;
    }

    #endregion

    #region Description

    public static readonly DependencyProperty DescriptionProperty =
        DependencyProperty.Register(nameof(Description), typeof(string), typeof(SettingsRowControl),
            new PropertyMetadata(string.Empty, OnDescriptionChanged));

    public string Description
    {
        get => (string)GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }

    private static void OnDescriptionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is SettingsRowControl c)
            c.UpdateDescription();
    }

    #endregion

    #region ShowDescriptionInline

    public static readonly DependencyProperty ShowDescriptionInlineProperty =
        DependencyProperty.Register(nameof(ShowDescriptionInline), typeof(bool), typeof(SettingsRowControl),
            new PropertyMetadata(false, OnShowDescriptionInlineChanged));

    /// <summary>
    /// true 时描述始终显示在行内（用于核心信息如数据目录路径）；
    /// false（默认）时描述藏入 info 浮窗，hover 预览、click 展开。
    /// </summary>
    public bool ShowDescriptionInline
    {
        get => (bool)GetValue(ShowDescriptionInlineProperty);
        set => SetValue(ShowDescriptionInlineProperty, value);
    }

    private static void OnShowDescriptionInlineChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is SettingsRowControl c)
            c.UpdateDescription();
    }

    #endregion

    #region IsExpanded

    public static readonly DependencyProperty IsExpandedProperty =
        DependencyProperty.Register(nameof(IsExpanded), typeof(bool), typeof(SettingsRowControl),
            new PropertyMetadata(false, OnIsExpandedChanged));

    /// <summary>
    /// 纯 UI 状态：用户点击 info 图标后展开行内描述。
    /// 离开设置页时通过 <see cref="ResetAllExpanded"/> 批量重置。
    /// </summary>
    public bool IsExpanded
    {
        get => (bool)GetValue(IsExpandedProperty);
        set => SetValue(IsExpandedProperty, value);
    }

    private static void OnIsExpandedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is SettingsRowControl c)
            c.UpdateDescription();
    }

    #endregion

    #region ErrorText

    public static readonly DependencyProperty ErrorTextProperty =
        DependencyProperty.Register(nameof(ErrorText), typeof(string), typeof(SettingsRowControl),
            new PropertyMetadata(string.Empty, OnErrorTextChanged));

    /// <summary>
    /// 错误提示（红色），始终在行内可见，不纳入 info 浮窗。
    /// </summary>
    public string ErrorText
    {
        get => (string)GetValue(ErrorTextProperty);
        set => SetValue(ErrorTextProperty, value);
    }

    private static void OnErrorTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is SettingsRowControl c)
            c.UpdateError();
    }

    #endregion

    #region RightContent

    public static readonly DependencyProperty RightContentProperty =
        DependencyProperty.Register(nameof(RightContent), typeof(object), typeof(SettingsRowControl),
            new PropertyMetadata(null, OnRightContentChanged));

    /// <summary>
    /// 右侧控件内容（ToggleSwitch / ComboBox / 快捷键按钮组等）。
    /// </summary>
    public object? RightContent
    {
        get => GetValue(RightContentProperty);
        set => SetValue(RightContentProperty, value);
    }

    private static void OnRightContentChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is SettingsRowControl c && c._rightContentPresenter is not null)
            c._rightContentPresenter.Content = e.NewValue;
    }

    #endregion

    // 内部视觉元素
    private Border? _hoverHighlight;
    private TextBlock? _titleBlock;
    private TextBlock? _infoIcon;
    private TextBlock? _descBlock;
    private TextBlock? _errorBlock;
    private ContentPresenter? _rightContentPresenter;
    private ToolTip? _descToolTip;
    private Storyboard? _hoverInStoryboard;
    private Storyboard? _hoverOutStoryboard;

    public SettingsRowControl()
    {
        Instances.Add(new WeakReference<SettingsRowControl>(this));
        Loaded += OnLoaded;
    }

    /// <summary>
    /// 离开设置页时调用，批量重置所有 SettingsRowControl 的 IsExpanded 为 false。
    /// </summary>
    public static void ResetAllExpanded()
    {
        foreach (var wr in Instances.ToArray())
        {
            if (wr.TryGetTarget(out var c))
                c.IsExpanded = false;
        }
        Instances.RemoveAll(w => !w.TryGetTarget(out _));
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (Content is not null)
            return; // 已构建过

        BuildVisualTree();
        CreateDescToolTip();
        UpdateInfoIcon();
        UpdateDescription();
        UpdateError();

        _infoIcon!.MouseEnter += OnInfoIconMouseEnter;
        _infoIcon.MouseLeave += OnInfoIconMouseLeave;
        _infoIcon.MouseLeftButtonDown += OnInfoIconClick;
    }

    /// <summary>
    /// 构建完整的视觉树并设置为 ContentControl 的 Content。
    /// </summary>
    private void BuildVisualTree()
    {
        // 悬停高亮层
        _hoverHighlight = new Border
        {
            CornerRadius = new CornerRadius(6),
            Opacity = 0,
        };
        _hoverHighlight.SetResourceReference(Border.BackgroundProperty, "ControlFillColorDefaultBrush");

        // 内容 Grid
        var contentGrid = new Grid { Margin = new Thickness(8, 9, 8, 9) };
        contentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        contentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // 左侧 StackPanel
        var leftPanel = new StackPanel
        {
            Margin = new Thickness(0, 0, 12, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };

        // 标题行：标题文字 + info 图标
        var titleRow = new StackPanel { Orientation = Orientation.Horizontal };

        _titleBlock = new TextBlock
        {
            FontSize = 14,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        _titleBlock.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
        _titleBlock.SetBinding(TextBlock.TextProperty,
            new System.Windows.Data.Binding("Title") { Source = this });
        titleRow.Children.Add(_titleBlock);

        // info 图标：&#xE946; = Segoe Fluent Icons info circle
        _infoIcon = new TextBlock
        {
            Text = "",
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 12,
            Margin = new Thickness(4, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = Cursors.Hand,
        };
        _infoIcon.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");
        titleRow.Children.Add(_infoIcon);

        leftPanel.Children.Add(titleRow);

        // 错误文本：始终可见
        _errorBlock = new TextBlock
        {
            FontSize = 12,
            Margin = new Thickness(0, 2, 0, 0),
            TextWrapping = TextWrapping.Wrap,
        };
        _errorBlock.SetResourceReference(TextBlock.ForegroundProperty, "SystemFillColorCriticalBrush");
        _errorBlock.SetBinding(TextBlock.TextProperty,
            new System.Windows.Data.Binding("ErrorText") { Source = this });
        leftPanel.Children.Add(_errorBlock);

        // 描述文本：默认隐藏
        _descBlock = new TextBlock
        {
            FontSize = 12,
            Margin = new Thickness(0, 2, 0, 0),
            TextWrapping = TextWrapping.Wrap,
        };
        _descBlock.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");
        _descBlock.SetBinding(TextBlock.TextProperty,
            new System.Windows.Data.Binding("Description") { Source = this });
        leftPanel.Children.Add(_descBlock);

        Grid.SetColumn(leftPanel, 0);
        contentGrid.Children.Add(leftPanel);

        // 右侧：ContentPresenter
        _rightContentPresenter = new ContentPresenter { VerticalAlignment = VerticalAlignment.Center };
        _rightContentPresenter.SetBinding(ContentPresenter.ContentProperty,
            new System.Windows.Data.Binding("RightContent") { Source = this });
        Grid.SetColumn(_rightContentPresenter, 1);
        contentGrid.Children.Add(_rightContentPresenter);

        // 根 Grid：悬停层 + 内容层
        var rootGrid = new Grid();
        rootGrid.Children.Add(_hoverHighlight);
        rootGrid.Children.Add(contentGrid);

        // 悬停动画
        _hoverInStoryboard = CreateHoverStoryboard(1, TimeSpan.FromSeconds(0.12));
        _hoverOutStoryboard = CreateHoverStoryboard(0, TimeSpan.FromSeconds(0.16));

        rootGrid.MouseEnter += (_, _) => _hoverHighlight.BeginStoryboard(_hoverInStoryboard);
        rootGrid.MouseLeave += (_, _) => _hoverHighlight.BeginStoryboard(_hoverOutStoryboard);

        Content = rootGrid;
    }

    private static Storyboard CreateHoverStoryboard(double toOpacity, TimeSpan duration)
    {
        var anim = new DoubleAnimation(toOpacity, duration)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTargetProperty(anim, new PropertyPath("Opacity"));
        var sb = new Storyboard();
        sb.Children.Add(anim);
        return sb;
    }

    /// <summary>创建 CliporaCard 风格的 ToolTip 浮窗。</summary>
    private void CreateDescToolTip()
    {
        var contentBorder = new Border
        {
            MaxWidth = 280,
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(12, 10, 12, 10),
            SnapsToDevicePixels = true,
        };
        contentBorder.SetResourceReference(Border.BackgroundProperty, "CardBackgroundFillColorDefaultBrush");
        contentBorder.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");

        var descText = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
        };
        descText.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        descText.SetBinding(TextBlock.TextProperty,
            new System.Windows.Data.Binding("Description") { Source = this });

        contentBorder.Child = descText;

        _descToolTip = new ToolTip
        {
            Content = contentBorder,
            Template = CreateToolTipTemplate(),
        };
    }

    /// <summary>创建透明外壳的 ToolTip ControlTemplate。</summary>
    private static ControlTemplate CreateToolTipTemplate()
    {
        var borderFactory = new FrameworkElementFactory(typeof(Border));
        borderFactory.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        borderFactory.SetValue(Border.BorderThicknessProperty, new Thickness(0));
        borderFactory.SetValue(Border.PaddingProperty, new Thickness(0));

        var cpFactory = new FrameworkElementFactory(typeof(ContentPresenter));
        cpFactory.SetValue(ContentPresenter.MarginProperty, new Thickness(0));
        borderFactory.AppendChild(cpFactory);

        return new ControlTemplate(typeof(ToolTip)) { VisualTree = borderFactory };
    }

    /// <summary>根据 ShowDescriptionInline / IsExpanded / Description 更新可见性。</summary>
    private void UpdateDescription()
    {
        if (_descBlock is null)
            return;

        var hasDesc = !string.IsNullOrEmpty(Description);

        if (ShowDescriptionInline)
        {
            _descBlock.Visibility = Visibility.Visible;
        }
        else if (IsExpanded)
        {
            _descBlock.Visibility = Visibility.Visible;
        }
        else
        {
            _descBlock.Visibility = Visibility.Collapsed;
        }

        UpdateInfoIcon();
    }

    /// <summary>更新 info 图标可见性和 ToolTip 状态。</summary>
    private void UpdateInfoIcon()
    {
        if (_infoIcon is null)
            return;

        var hasInfo = !string.IsNullOrEmpty(Description) && !ShowDescriptionInline;

        _infoIcon.Visibility = hasInfo ? Visibility.Visible : Visibility.Collapsed;

        // 展开时禁用 ToolTip（描述已在眼前）
        _infoIcon.ToolTip = hasInfo && !IsExpanded ? _descToolTip : null;
    }

    /// <summary>更新错误文本可见性。</summary>
    private void UpdateError()
    {
        if (_errorBlock is null)
            return;

        var hasError = !string.IsNullOrEmpty(ErrorText);
        _errorBlock.Visibility = hasError ? Visibility.Visible : Visibility.Collapsed;
    }

    #region Info icon 交互

    private void OnInfoIconMouseEnter(object sender, MouseEventArgs e)
    {
        if (!IsExpanded)
        {
            _infoIcon!.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
        }
    }

    private void OnInfoIconMouseLeave(object sender, MouseEventArgs e)
    {
        _infoIcon!.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");
    }

    private void OnInfoIconClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        IsExpanded = !IsExpanded;
    }

    #endregion
}
