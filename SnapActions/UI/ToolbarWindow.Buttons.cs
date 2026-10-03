using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using SnapActions.Actions;
using SnapActions.Actions.UserActions;
using ModernFontIcon = iNKORE.UI.WPF.Modern.Controls.FontIcon;
using TextBlock = System.Windows.Controls.TextBlock;
using FontFamily = System.Windows.Media.FontFamily;

namespace SnapActions.UI;

// Dynamic-button construction lives here: inline context buttons, pinned buttons (with
// drag-to-reorder), the overflow button, and the sub-menu buttons that the toolbar pops out.
// The split is geometric rather than logical — these methods are what makes the file long,
// not what makes the toolbar conceptually distinct.
public partial class ToolbarWindow
{
    // Segoe Fluent Icons glyph map, keyed by the action's IconKey. Codes live in IconGlyphs so a
    // code point is defined (and edited) only once — see IconGlyphs.cs. 本表沿用备份项目的
    // FluentGlyphs；没有特定码点的动作（格式化/编码等）由 IconGlyphFor 按 Id 回退到通用字形。
    private static readonly Dictionary<string, string> FluentGlyphs = new(StringComparer.Ordinal)
    {
        ["IconPaste"] = IconGlyphs.Paste,
        ["IconCopy"] = IconGlyphs.Copy,
        ["IconOpenUrl"] = IconGlyphs.OpenUrl,
        ["IconEmail"] = IconGlyphs.Email,
        ["IconOpenFile"] = IconGlyphs.OpenFile,
        ["IconFolder"] = IconGlyphs.Folder,
        ["IconConvert"] = IconGlyphs.Swap,
        ["IconCalculate"] = IconGlyphs.Calculate,
        ["IconIpLookup"] = IconGlyphs.IpLookup,
        ["IconEncode"] = IconGlyphs.Encode,
        ["IconTime"] = IconGlyphs.Time,
        ["IconSearch"] = IconGlyphs.Search,
        ["IconContext"] = IconGlyphs.Context,
        ["IconTransform"] = IconGlyphs.Transform,
        ["IconTranslate"] = IconGlyphs.TranslateSta,
        ["IconDelete"] = IconGlyphs.Delete,
    };

    /// <summary>
    /// Resolves an action's icon as a Segoe Fluent Icons glyph (FontIcon).
    /// Falls back by action id for parametrized families whose IconKey is empty
    /// (case transforms, encoders, per-engine search actions).
    /// </summary>
    private static string? IconGlyphFor(IAction action)
    {
        if (action.IconKey is { Length: > 0 } key && FluentGlyphs.TryGetValue(key, out var glyph))
            return glyph;
        if (action.Id.StartsWith("search_", StringComparison.Ordinal)) return IconGlyphs.Search;
        return action.Id switch
        {
            "upper" or "lower" or "title" or "pascal" or "camel" or "snake" or "kebab" or "reverse"
                => IconGlyphs.Transform,
            _ when action.Id is "url_encode" or "url_decode" or "base64_encode" or "base64_decode"
                or "html_encode" or "html_decode" or "hex_encode" or "hex_decode"
                or "rot13" or "md5" or "sha1" or "sha256" or "sha512" => IconGlyphs.Encode,
            _ => null
        };
    }

    // Segoe Fluent Icons 字体（含 MDL2 回退）：FontIcon 用它渲染 IconGlyphs 码点。
    private static readonly FontFamily SegoeFluentFontFamily = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    private static ModernFontIcon CreateIcon(string glyph, double size, Brush brush) =>
        new()
        {
            Glyph = glyph,
            FontSize = size,
            FontFamily = SegoeFluentFontFamily,
            Foreground = brush,
        };

    /// <summary>WPF 主工具栏按钮样式（备份 ActionButtonStyle）。</summary>
    private Style ActionButtonStyle =>
        (Style)FindResource("ActionButtonStyle");

    /// <summary>Icon visual for an action button: Segoe Fluent glyph first, existing Geometry resource second.</summary>
    private object? ActionIconVisual(IAction action)
    {
        // 自定义 JS 脚本动作与内置文本转换共用 IconTransform 字形（见 UserScriptAction.IconKey），
        // 固定区/子菜单里换成 JS 徽标才能一眼分清哪个是用户脚本。
        if (action is UserScriptAction)
            return JsBadge.CreateToolbarIcon();
        if (IconGlyphFor(action) is { } glyph)
            return CreateIcon(glyph, 16, (Brush)FindResource("TextFillColorPrimaryBrush"));
        var geo = TryFindResource(action.IconKey) as Geometry;
        return geo != null
            ? new Path { Data = geo, Fill = (Brush)FindResource("TextFillColorPrimaryBrush"), Width = 16, Height = 16, Stretch = Stretch.Uniform }
            : null;
    }

    internal void RebuildInlineActions()
    {
        ContextActionsPanel.Children.Clear();
        PinnedActionsPanel.Children.Clear();
        ContextSeparator.Visibility = PinnedSeparator.Visibility = MoreButton.Visibility = Visibility.Collapsed;
        var pinned = (Registry?.GetPinnedActions(_appName) ?? []).Where(a => a.Id != "translate").ToList();
        var pinnedIds = pinned.Select(a => a.Id).ToHashSet();
        // 内联上下文区取注册表的 "Context" 组，而不是按 Category 过滤：「上下文触发」正则命中的 JS 脚本
        // 动作是 Transform 类别（仍在转换子菜单/固定区），但注册表会把它同时放进 Context 组。
        // 已固定的动作一律只在固定区出现，避免同一个动作在工具栏上渲染两次。
        var context = (_actionGroups.FirstOrDefault(g => g.Name == "Context")?.Actions ?? [])
            .Where(a => !pinnedIds.Contains(a.Id) && a.Id != "translate").ToList();
        var overflow = new List<IAction>();
        double reserved = 10 + 44 + 16; // border/padding, More, and the two inline separators
        foreach (UIElement child in MainToolbar.Children)
        {
            if (child == ContextActionsPanel || child == PinnedActionsPanel || child == MoreButton || child.Visibility != Visibility.Visible) continue;
            child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            reserved += child.DesiredSize.Width;
        }
        double remaining = Math.Max(0, MainBorder.MaxWidth - reserved);
        bool pinsOverflowed = false;
        foreach (var action in pinned)
        {
            var button = CreatePinnedButton(action);
            button.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            if (pinsOverflowed || button.DesiredSize.Width > remaining)
            { overflow.Add(action); pinsOverflowed = true; continue; }
            PinnedActionsPanel.Children.Add(button);
            remaining -= button.DesiredSize.Width;
        }
        int maxContext = Config.SettingsManager.Current.MaxInlineContextActions;
        foreach (var action in context)
        {
            var button = CreateActionButton(action);
            button.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            if (ContextActionsPanel.Children.Count >= maxContext || button.DesiredSize.Width > remaining)
            { overflow.Add(action); continue; }
            ContextActionsPanel.Children.Add(button);
            remaining -= button.DesiredSize.Width;
        }
        ContextSeparator.Visibility = ContextActionsPanel.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        PinnedSeparator.Visibility = PinnedActionsPanel.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        MoreButton.Tag = overflow;
        MoreButton.Visibility = overflow.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        MoreButton.ToolTip = $"更多操作 ({overflow.Count})";
    }

    private void MoreButton_Click(object sender, RoutedEventArgs e)
    {
        if (MoreButton.Tag is List<IAction> actions) ShowContextOverflowSubMenu(actions);
    }

    private void ShowContextOverflowSubMenu(List<IAction> actions)
    {
        // Reuse the existing sub-menu plumbing but skip _actionGroups (these are the *overflow*,
        // not a registered category). Edit-mode arrows / pin toggles aren't meaningful here.
        _currentSubMenuGroup = "More actions";
        _currentSubMenuCategory = null;
        _editMode = false;
        _hoverPreviewMode = false;

        ClearSubMenu();
        ResetPreview();
        SubMenuTitle.Text = "更多操作";
        SubMenuHeader.Visibility = Visibility.Visible;
        CustomizationHint.Visibility = Visibility.Visible;
        GearButton.Visibility = Visibility.Collapsed; // no edit mode for the ad-hoc overflow list
        foreach (var a in actions)
            AddSubMenuItem(CreateSubMenuButton(a, false));
        SubMenuPopup.IsOpen = true;
        StartDismissTimer();
    }

    private Button CreateActionButton(IAction action)
    {
        var icon = ActionIconVisual(action);
        var btn = new Button
        {
            Tag = action,
            ToolTip = action.Name,
            Style = ActionButtonStyle,
            Content = icon != null
                ? icon
                : new TextBlock { Text = action.Name.Length > 3 ? action.Name[..3] : action.Name,
                    FontSize = 11, Foreground = (Brush)FindResource("TextFillColorPrimaryBrush"),
                    VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center } as object
        };
        System.Windows.Automation.AutomationProperties.SetName(btn, action.Name);
        btn.Click += ActionButton_Click;
        // Hover preview — same MouseEnter/Leave handlers as submenu buttons but routed through
        // InlineButton_* so the popup opens in preview-only mode if it isn't already open.
        btn.MouseEnter += InlineButton_MouseEnter;
        btn.MouseLeave += InlineButton_MouseLeave;
        ConfigureActionButton(btn, action);
        return btn;
    }

    private Button CreatePinnedButton(IAction action)
    {
        var btn = new Button
        {
            ToolTip = action.Name + "  (拖动以重新排序)",
            Tag = action,
            MinWidth = 36,
            Width = double.NaN, MaxWidth = 160, Padding = new Thickness(6, 2, 6, 2),
            Style = ActionButtonStyle,
        };
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        if (ActionIconVisual(action) is { } icon)
        {
            (icon as FrameworkElement)!.Width = 20;
            (icon as FrameworkElement)!.Height = 20;
            (icon as FrameworkElement)!.Margin = new Thickness(0, 0, 4, 0);
            sp.Children.Add((UIElement)icon);
        }
        sp.Children.Add(new TextBlock
        {
            Text = action.Name, FontSize = 12, MaxWidth = 120, TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = (Brush)FindResource("TextFillColorPrimaryBrush"),
            VerticalAlignment = VerticalAlignment.Center
        });
        btn.Content = sp;
        System.Windows.Automation.AutomationProperties.SetName(btn, action.Name);
        btn.Click += ActionButton_Click;
        // Hover preview for pinned actions too — same routing as inline context buttons.
        btn.MouseEnter += InlineButton_MouseEnter;
        btn.MouseLeave += InlineButton_MouseLeave;

        ConfigureActionButton(btn, action);

        return btn;
    }

    private void MovePinned(IAction? action, int direction)
    {
        if (action == null) return;
        var pinned = Config.SettingsManager.Current.PinnedActionIds;
        int idx = pinned.IndexOf(action.Id);
        int newIdx = idx + direction;
        if (idx < 0 || newIdx < 0 || newIdx >= pinned.Count) return;
        (pinned[idx], pinned[newIdx]) = (pinned[newIdx], pinned[idx]);
        Config.SettingsManager.Save();
    }

    // 子菜单动作区是 4 列自适应列宽的网格：条目按行优先填充，分组标题跨整行。
    // 列定义在 ClearSubMenu 里按 SubMenuColumns 重建，列数只有这一个来源。
    private const int SubMenuColumns = 4;
    private int _subMenuCursor;

    /// <summary>清空子菜单网格并重建列定义。</summary>
    private void ClearSubMenu()
    {
        SubMenuPanel.Children.Clear();
        SubMenuPanel.RowDefinitions.Clear();
        SubMenuPanel.ColumnDefinitions.Clear();
        for (var i = 0; i < SubMenuColumns; i++)
            SubMenuPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _subMenuCursor = 0;
    }

    /// <summary>把一个条目放进网格的下一格；fullRow=true 时独占整行（分组标题用）。</summary>
    private void AddSubMenuItem(UIElement element, bool fullRow = false)
    {
        if (fullRow)
        {
            // 标题独占整行：先把当前行剩下的格子空出来
            var remainder = _subMenuCursor % SubMenuColumns;
            if (remainder != 0) _subMenuCursor += SubMenuColumns - remainder;
            SetSubMenuItemCell(element, _subMenuCursor / SubMenuColumns, 0, SubMenuColumns);
            _subMenuCursor += SubMenuColumns;
        }
        else
        {
            SetSubMenuItemCell(element, _subMenuCursor / SubMenuColumns, _subMenuCursor % SubMenuColumns, 1);
            _subMenuCursor++;
        }
        SubMenuPanel.Children.Add(element);
    }

    // Grid 的行必须真实存在，否则 Grid.Row 会被钳到 0（所有条目重叠在第一行）；行高随内容自适应。
    private void SetSubMenuItemCell(UIElement element, int row, int column, int columnSpan)
    {
        while (SubMenuPanel.RowDefinitions.Count <= row)
            SubMenuPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(element, row);
        Grid.SetColumn(element, column);
        Grid.SetColumnSpan(element, columnSpan);
    }

    private Button CreateSubMenuButton(IAction action, bool isEditMode)
    {
        var pinned = Config.SettingsManager.Current.PinnedActionIds;
        // bool isPinned = pinned.Contains(action.Id);

        bool isOff = Config.ToolbarPreferences.IsHidden(Config.SettingsManager.Current, action);

        var btn = new Button
        {
            Tag = action,
            Width = double.NaN, MinWidth = isEditMode ? 60 : 90,
            Padding = new Thickness(6, 1, 6, 1),
            Margin = new Thickness(1),
            // 与备份一致：编辑模式用紧凑 ActionButtonStyle，普通列表用行式 PopoverItemStyle
            Style = isEditMode
                ? (Style)FindResource("ActionButtonStyle")
                : (Style)FindResource("PopoverItemStyle"),
            Opacity = isEditMode && isOff ? 0.4 : 1.0
        };
        var sp = new StackPanel { Orientation = Orientation.Horizontal };

        if (isEditMode)
        {
            // Eye toggle (enable/disable) — 照抄备份 Geometry（TextBrush/AccentBrush）
            sp.Children.Add(new Path
            {
                Data = (Geometry)FindResource(isOff ? "IconEyeOff" : "IconEyeOn"),
                Fill = (Brush)FindResource(isOff ? "TextFillColorSecondaryBrush" : "SystemFillColorAttentionBrush"),
                Width = 12, Height = 12, Stretch = Stretch.Uniform, Margin = new Thickness(0, 0, 4, 0)
            });
            // Pin toggle — 照抄备份 Geometry（IconPin / IconPinOff，WarningBrush 表示已固定）
            // sp.Children.Add(new Path
            // {
            //     Data = (Geometry)FindResource(isPinned ? "IconPin" : "IconPinOff"),
            //     Fill = (Brush)FindResource(isPinned ? "SystemFillColorCautionBrush" : "TextFillColorSecondaryBrush"),
            //     Width = 12, Height = 12, Stretch = Stretch.Uniform, Margin = new Thickness(0, 0, 6, 0)
            // });
        }
        else
        {
            if (ActionIconVisual(action) is { } icon)
            {
                (icon as FrameworkElement)!.Width = 20;
                (icon as FrameworkElement)!.Height = 20;
                (icon as FrameworkElement)!.Margin = new Thickness(0, 0, 3, 0);
                sp.Children.Add((UIElement)icon);
            }
        }

        sp.Children.Add(new TextBlock
        {
            Text = action.Name, FontSize = 13,
            Foreground = (Brush)FindResource(isEditMode && isOff ? "TextFillColorSecondaryBrush" : "TextFillColorPrimaryBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            TextDecorations = isEditMode && isOff ? TextDecorations.Strikethrough : null
        });

        // Arrows only make sense for actions in an ordered list — search engines (ordered in
        // SearchEngines) and pinned actions (ordered in PinnedActionIds). For an unpinned non-search
        // action, MoveAction would silently no-op, leaving the user staring at buttons that do
        // nothing.
        // bool canReorder = isEditMode && (action.Category == ActionCategory.Search || isPinned);
        // if (canReorder)
        // {
        //     // Move up/down arrows for reordering（照抄备份：紧凑 ▲▼ 按钮）
        //     var moveUp = new Button
        //     {
        //         Content = new TextBlock { Text = "▲", FontSize = 8, Foreground = (Brush)FindResource("TextFillColorSecondaryBrush") },
        //         Background = System.Windows.Media.Brushes.Transparent, BorderThickness = new Thickness(0),
        //         Width = 16, Height = 16, Padding = new Thickness(0), Margin = new Thickness(2, 0, 0, 0),
        //         Tag = action, Cursor = System.Windows.Input.Cursors.Hand
        //     };
        //     moveUp.Click += MoveActionUp_Click;
        //     sp.Children.Add(moveUp);
        //
        //     var moveDown = new Button
        //     {
        //         Content = new TextBlock { Text = "▼", FontSize = 8, Foreground = (Brush)FindResource("TextFillColorSecondaryBrush") },
        //         Background = System.Windows.Media.Brushes.Transparent, BorderThickness = new Thickness(0),
        //         Width = 16, Height = 16, Padding = new Thickness(0), Margin = new Thickness(0, 0, 0, 0),
        //         Tag = action, Cursor = System.Windows.Input.Cursors.Hand
        //     };
        //     moveDown.Click += MoveActionDown_Click;
        //     sp.Children.Add(moveDown);
        // }

        btn.Content = sp;
        System.Windows.Automation.AutomationProperties.SetName(btn, action.Name);
        if (isEditMode)
        {
            btn.Click += ToggleActionButton_Click;
        }
        else { btn.Click += ActionButton_Click; btn.MouseEnter += SubMenuButton_MouseEnter; btn.MouseLeave += SubMenuButton_MouseLeave; }
        ConfigureActionButton(btn, action, isEditMode);
        return btn;
    }
}
