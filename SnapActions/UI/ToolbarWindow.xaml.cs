using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using SnapActions.Actions;
using SnapActions.Config;
using SnapActions.Core;
using SnapActions.Detection;
using SnapActions.Helpers;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;

namespace SnapActions.UI;

// This file is the toolbar window's shell — lifecycle, positioning, dismiss, type badge,
// and the simple top-level button handlers. Heavier concerns are split into:
//   - ToolbarWindow.Buttons.cs  : dynamic button creation (context, pinned, sub-menu, drag-drop)
//   - ToolbarWindow.Preview.cs  : hover-preview band, "Copied!" toast, failure UI
//   - ToolbarWindow.Actions.cs  : action execution, edit-mode toggles, sub-menu navigation
public partial class ToolbarWindow : Window
{
    /// <summary>最近创建的工具栏实例，供 TranslateAction 等外部动作打开翻译弹层。进程内单例。</summary>
    internal static ToolbarWindow? Current { get; private set; }

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private readonly DispatcherTimer _dismissTimer;
    private List<ActionGroup> _actionGroups = [];
    private TextAnalysis _analysis = TextAnalysis.PlainText;
    private Point _anchorPoint;
    private string? _appName;
    private ActionCategory? _currentSubMenuCategory;
    private string? _currentSubMenuGroup;
    private double _dpiX = 1.0, _dpiY = 1.0;
    private double _dragOffsetX, _dragOffsetY;
    private long _dragTick;

    // Edit mode for action toggles
    private bool _editMode;

    // Bumped on every (re)show. Async post-delay continuations (the "Copied!"/failure toasts and
    // the delayed clipboard-restore) capture this before awaiting and bail if it changed — without
    // it, a delay that fires AFTER a new selection reshowed the singleton toolbar would tear down
    // the freshly-shown bar (or restore a now-stale clipboard).
    private int _generation;

    // True when the sub-menu popup is open just to host the hover-preview band (no submenu items
    // populated, no title). Reset whenever the popup is closed or repurposed for a real submenu.
    private bool _hoverPreviewMode;

    // 拖动把手：按下时捕捉鼠标移动工具栏窗口，松开即停。
    private bool _isDragging;
    private bool _isEditable;
    private bool _isPasteMode;
    private ToolbarOperationContext? _operationContext;
    private string _selectedText = "";
    private FlowDirection _selectionFlowDirection;
    private SelectionProviderKind _selectionProvider;
    private DispatcherTimer? _hoverCloseTimer;

    public ToolbarWindow()
    {
        Current = this;
        InitializeComponent();
        InitializeTranslatePopup();
        _dismissTimer = new DispatcherTimer();
        _dismissTimer.Tick += OnDismissTimerTick;
        InitializeCustomization();
        SettingsManager.Changed += OnSettingsChanged;

        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            ClipboardTransaction.SetClipboardOwnerWindow(hwnd);
            // Use the IntPtr variants so 64-bit ex-styles (e.g. anything past bit 31) survive.
            // SetWindowLong silently truncates to 32 bits on x64, which would corrupt high-bit flags.
            var style = GetWindowLongPtr(hwnd, GWL_EXSTYLE);
            SetWindowLongPtr(hwnd, GWL_EXSTYLE,
                new IntPtr(style.ToInt64() | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW));
        };
        
        // 构造函数里一次挂好，跟动态内容无关
        SubMenuPopup.Opened += (_, _) =>
        {

            if (SubMenuPopup.Child is not { } root) return;
            root.MouseEnter += SubMenuRoot_MouseEnter;
            root.MouseLeave += SubMenuRoot_MouseLeave;
            AlignSubMenuDirection();
            // 布局完成后再收一次预览条宽度（此时按钮的 ActualWidth 才可信）
            Dispatcher.InvokeAsync(ClampPreviewBandWidth, DispatcherPriority.Loaded);
        };

        // Esc dismisses the toolbar even though it never has keyboard focus (WS_EX_NOACTIVATE).
        // Subscribed for the life of the window — the SelectionTracker keeps a single toolbar
        // around for the whole process, so there's no leak; we still detach in Closed for safety.
        KeyboardHook.EscPressed += OnGlobalEsc;
        Closed += (_, _) =>
        {
            if (ReferenceEquals(Current, this)) Current = null;
            KeyboardHook.EscPressed -= OnGlobalEsc;
            SettingsManager.Changed -= OnSettingsChanged;
            ClipboardTransaction.SetClipboardOwnerWindow(IntPtr.Zero);
        };
    }

    public ActionRegistry? Registry { get; set; }

    private bool TryStartToolbarAction(out SelectionOperation operation)
    {
        var context = Volatile.Read(ref _operationContext);
        if (context != null && context.ActionGate.TryStart())
        {
            operation = context.Operation;
            return true;
        }

        operation = default;
        return false;
    }

    /// <summary>子菜单弹出方向（复用通用对齐逻辑，见 AlignPopupDirection）。</summary>
    private void AlignSubMenuDirection()
    {
        AlignPopupDirection(SubMenuPopup);
    }
    
    /// <summary>把预览条宽度限制为子菜单网格一行的宽度（列数 × 单按钮宽），悬停预览文本再长也不会撑宽弹层。
    /// 单按钮宽按第一个动作按钮的实际宽度取（内容会把它撑得比 MinWidth 更宽），找不到时回退 90。</summary>
    private void ClampPreviewBandWidth()
    {
        Button? first = null;
        foreach (var child in SubMenuPanel.Children)
            if (child is Button button) { first = button; break; }
        if (first == null) return;
        double unit = 90;
        var w = first.ActualWidth > 0 ? first.ActualWidth : first.DesiredSize.Width;
        if (w > 0) unit = w;
        PreviewBorder.MaxWidth = unit * SubMenuColumns;
    }

    /// <summary>翻译弹层弹出方向：根据屏幕剩余空间从工具栏下方或上方弹出，上方时把
    /// VerticalOffset 取反，保持与工具栏相同的 8px 间隙。</summary>
    private void AlignTranslateDirection()
    {
        AlignPopupDirection(TranslatePopup);
    }
    /// <summary>
    /// 按工具栏所在屏的工作区给翻译卡算尺寸上限。上限取"工具栏上方可用空间"与
    /// "下方可用空间"中更大的一侧 —— 这样无论弹层朝上还是朝下，都不会伸到任务栏里。
    /// 内容超出部分由 TranslateResultBox 的滚动条自己消化。
    /// </summary>
    private void ClampTranslateSize()
    {
        if (!IsVisible || MainBorder.ActualHeight <= 0) return;

        double dx = _dpiX > 0 ? _dpiX : 1.0;
        double dy = _dpiY > 0 ? _dpiY : 1.0;

        // MainBorder.PointToScreen 返回物理像素；ScreenHelper.GetScreenBounds 也按物理像素算。
        var pt = MainBorder.PointToScreen(new Point(0, 0));
        var sb = ScreenHelper.GetScreenBounds(pt);   // ★ 必须是工作区 rcWork，不是整屏

        double gapPx            = Math.Abs(TranslatePopup.VerticalOffset) * dy;
        double toolbarTopPx     = pt.Y;
        double toolbarBottomPx  = pt.Y + MainBorder.ActualHeight * dy;

        double spaceBelowPx = sb.Bottom - 8 - toolbarBottomPx - gapPx;
        double spaceAbovePx = toolbarTopPx - gapPx - (sb.Top + 8);
        double usablePx     = Math.Max(spaceBelowPx, spaceAbovePx);

        // 再收 16px 给 DropShadowPanel 的 margin 和圆角
        double maxHeightDip = (usablePx - 16 * dy) / dy;
        double maxWidthDip  = (sb.Width / dx) - 40;

        TranslateCard.MaxHeight = Math.Max(TranslateCard.MinHeight, maxHeightDip);
        TranslateCard.MaxWidth  = Math.Max(TranslateCard.MinWidth,  maxWidthDip);

        // 已经超出就立即收回来（不仅在拖拽时）
        if (TranslateCard.Height > TranslateCard.MaxHeight) TranslateCard.Height = TranslateCard.MaxHeight;
        if (TranslateCard.Width  > TranslateCard.MaxWidth)  TranslateCard.Width  = TranslateCard.MaxWidth;
    }

    /// <summary>根据屏幕剩余空间决定弹出方向；上方弹出时取反 VerticalOffset 保持间隙。</summary>
    private void AlignPopupDirection(Popup popup)
    {
        if (!IsVisible) return;
        if (popup.Child is not FrameworkElement child) return;
        if (child.ActualHeight <= 0)
            child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

        double dy = _dpiY > 0 ? _dpiY : 1.0;
        double popupHeight = (child.ActualHeight > 0 ? child.ActualHeight : child.DesiredSize.Height) + 12;
        double gap = Math.Abs(popup.VerticalOffset);

        // ★ 用工具栏所在屏的工作区（物理像素），不再用 SystemParameters.WorkArea（主屏）。
        var pt = MainBorder.PointToScreen(new Point(0, 0));
        var sb = ScreenHelper.GetScreenBounds(pt);

        double toolbarTopPx    = pt.Y;
        double toolbarBottomPx = pt.Y + MainBorder.ActualHeight * dy;
        double gapPx           = gap * dy;
        double popupHeightPx   = popupHeight * dy;

        bool belowFits = toolbarBottomPx + gapPx + popupHeightPx <= sb.Bottom - 8;
        bool aboveFits = toolbarTopPx - gapPx - popupHeightPx >= sb.Top + 8;

        if (belowFits)
        {
            popup.Placement = PlacementMode.Bottom;
            popup.VerticalOffset = gap;
        }
        else if (aboveFits)
        {
            popup.Placement = PlacementMode.Top;
            popup.VerticalOffset = -gap;
        }
        else
        {
            // 上下都放不下（空间特别小）：选空间更大的一侧。ClampTranslateSize 已经把卡片
            // 限到该侧能容纳的高度，所以这里不会再越界。
            double spaceBelowPx = sb.Bottom - 8 - toolbarBottomPx - gapPx;
            double spaceAbovePx = toolbarTopPx - gapPx - (sb.Top + 8);
            if (spaceBelowPx >= spaceAbovePx)
            {
                popup.Placement = PlacementMode.Bottom;
                popup.VerticalOffset = gap;
            }
            else
            {
                popup.Placement = PlacementMode.Top;
                popup.VerticalOffset = -gap;
            }
        }
    }

    private void OnGlobalEsc()
    {
        // 翻译弹层打开时 Esc 只收翻译，不隐藏整个工具栏。
        if (TranslatePopup.IsOpen)
        {
            Application.Current.Dispatcher.InvokeAsync(() =>
            {
                CloseTranslatePopup();
                StartDismissTimer();
            });
            return;
        }

        // Revoke on the hook thread before the key is delivered. The immutable reference avoids
        // reading a multi-field operation struct concurrently with a new toolbar Show.
        var context = Volatile.Read(ref _operationContext);
        if (context == null) return;
        context.Operation.InvalidateIfCurrent();

        // Marshal only the WPF work. If another operation reshowed the singleton in the meantime,
        // the old Esc event must not hide or revoke that newer toolbar.
        Application.Current.Dispatcher.InvokeAsync(() =>
        {
            if (IsVisible) HideToolbar(context);
        });
    }

    // ── Show ─────────────────────────────────────────────────────

    internal void Show(string text, TextAnalysis analysis, List<ActionGroup> groups,
        double x, double y, bool isEditable, SelectionOperation operation,
        bool? rightToLeft = null, SelectionProviderKind provider = SelectionProviderKind.UiAutomation)
    {
        Volatile.Write(
            ref _operationContext, new ToolbarOperationContext(operation));
        _selectedText = text;
        _selectionProvider = provider;
        _selectionFlowDirection = rightToLeft.HasValue
            ? rightToLeft.Value ? FlowDirection.RightToLeft : FlowDirection.LeftToRight
            : GetPreviewFlowDirection(text);
        _analysis = analysis;
        _appName = ForegroundApp.GetActiveProcessName();
        _anchorPoint = new Point(x, y);
        _actionGroups = groups.Select(g =>
                g with { Actions = g.Actions.Where(a => isEditable || a is not IOperationAction).ToList() })
            .Where(g => g.Actions.Count > 0).ToList();
        _isEditable = isEditable;
        _isPasteMode = false;
        _editMode = false;

        CopyButton.Visibility = Visibility.Visible;
        PasteButton.Visibility = Visibility.Visible;
        TranslationButton.Visibility = Visibility.Visible;
        // DeleteButton.Visibility = Visibility.Visible;   // 删除按钮已停用
        // CustomizeButton.Visibility = Visibility.Visible;
        BuildToolbarButtons();
        UpdateTypeBadge();
        var bounds = ScreenHelper.GetScreenBounds(new Point(x, y));
        var dpi = ScreenHelper.GetDpiForPoint(new Point(x, y));
        MainBorder.MaxWidth = Math.Max(240, bounds.Width / Math.Max(1, dpi.X) - 16);
        RebuildInlineActions();
        PositionAndShow(x, y);
    }

    internal void ShowPasteMode(double x, double y, SelectionOperation operation)
    {
        Volatile.Write(
            ref _operationContext, new ToolbarOperationContext(operation));
        try
        {
            _selectedText = Clipboard.ContainsText() ? Clipboard.GetText() ?? "" : "";
        }
        catch
        {
            _selectedText = "";
        }

        _analysis = TextAnalysis.PlainText;
        _actionGroups = [];
        _isPasteMode = true;
        _isEditable = true;
        _editMode = false;

        CopyButton.Visibility = Visibility.Collapsed;
        // DeleteButton.Visibility = Visibility.Collapsed;  // 删除按钮已停用
        // CustomizeButton.Visibility = Visibility.Collapsed;

        // 隐藏所有类别按钮转换可通过粘贴悬停访问
        ContextActionsPanel.Children.Clear();
        ContextSeparator.Visibility = Visibility.Collapsed;
        PinnedActionsPanel.Children.Clear();
        PinnedSeparator.Visibility = Visibility.Collapsed;
        
        TranslateSeparator.Visibility = Visibility.Collapsed;
        TransformSeparator.Visibility = Visibility.Collapsed;
        SearchSeparator.Visibility = Visibility.Collapsed;
        
        PasteButton.Visibility = Visibility.Collapsed;
        TranslationButton.Visibility = Visibility.Collapsed;
        TransformButton.Visibility = Visibility.Collapsed;
        EncodeButton.Visibility = Visibility.Collapsed;
        SearchButton.Visibility = Visibility.Collapsed;
        
        TypeBadge.Visibility = Visibility.Collapsed;
        MoreButton.Visibility = Visibility.Collapsed;

        PositionAndShow(x, y);

        // Open the Paste As options right away (PositionAndShow closed the popup). Empty
        // clipboard leaves just the plain V button, same as before.
        if (!string.IsNullOrEmpty(_selectedText)) ShowPasteAsMenu();
    }

    /// <summary>Pure transforms remain useful on read-only selections; replacement is a separate capability.</summary>
    private void BuildToolbarButtons()
    {
        var s = SettingsManager.Current;

        // Single pass to avoid repeated O(n) scans.
        var groupNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var g in _actionGroups) groupNames.Add(g.Name);

        // 粘贴按钮是工具栏固定按钮，不属于任何 ActionGroup，仅由设置开关控制显示
        //（若依赖 groupNames.Contains("Paste") 会恒为 false，导致勾选与否都不显示）。
        var hasPaste = s.ShowPasteActions;
        var hasTranslate = s.ShowTranslateActions && groupNames.Contains("Translate");
        var hasTransform = s.ShowTransformActions && groupNames.Contains("Transform");
        var hasEncode = s.ShowEncodeActions && groupNames.Contains("Encode");
        var hasSearch = s.ShowSearchActions && groupNames.Contains("Search");
        
        // Transform之前的分隔符也适用于仅编码的情况。
        
        TranslateSeparator.Visibility = hasTranslate ? Visibility.Visible : Visibility.Collapsed;
        TransformSeparator.Visibility = hasTransform || hasEncode ? Visibility.Visible : Visibility.Collapsed;
        SearchSeparator.Visibility = hasSearch ? Visibility.Visible : Visibility.Collapsed;
        
        PasteButton.Visibility = hasPaste ? Visibility.Visible : Visibility.Collapsed;
        TranslationButton.Visibility = hasTranslate ? Visibility.Visible : Visibility.Collapsed;
        TransformButton.Visibility = hasTransform ? Visibility.Visible : Visibility.Collapsed;
        EncodeButton.Visibility = hasEncode ? Visibility.Visible : Visibility.Collapsed;
        SearchButton.Visibility = hasSearch ? Visibility.Visible : Visibility.Collapsed;

        // 翻译：写死按钮（XAML 声明，不再动态加载），仅按当前选区刷新启用态，功能链路不变
        // （Find translate action → ActionButton_Click → TranslateAction.Execute → 翻译弹窗）。
        var translate = _actionGroups.SelectMany(g => g.Actions).FirstOrDefault(a => a.Id == "translate");
        var canTranslate = translate is { } t && t.CanExecute(_selectedText, _analysis);
        TranslationButton.IsEnabled = canTranslate;
        TranslationButton.Opacity = canTranslate ? 1.0 : 0.45;
        TranslationButton.ToolTip = canTranslate ? "翻译" : "翻译 — 当前选区无法翻译";
    }

    // ── Positioning ──────────────────────────────────────────────

    private void PositionAndShow(double cursorX, double cursorY)
    {
        _generation++;

        var fadeOut = (Storyboard)FindResource("FadeOut");
        fadeOut.Stop(this);

        SubMenuPopup.IsOpen = false;
        CloseTranslatePopup();
        ResetPreview();

        Width = double.NaN;
        Height = double.NaN;
        SizeToContent = SizeToContent.WidthAndHeight;
        Opacity = 0;

        var monitorDpi = ScreenHelper.GetDpiForPoint(new Point(cursorX, cursorY));
        _dpiX = monitorDpi.X > 0 ? monitorDpi.X : 1.0;
        _dpiY = monitorDpi.Y > 0 ? monitorDpi.Y : 1.0;

        // Show 之前先设置 Left/Top，让 WPF 在处理窗口挂载时就知道它属于
        // 哪个屏幕，从而用正确的 DPI 上下文初始化布局。
        Left = cursorX / _dpiX;
        Top  = cursorY / _dpiY;

        Show();
        UpdateLayout();

        // 首帧渲染后（此时 DPI 上下文已经切换完成）再做精细定位。
        Dispatcher.InvokeAsync(() =>
        {
            if (!IsVisible) return;
            ApplyPrecisePosition(cursorX, cursorY);
            ((Storyboard)FindResource("FadeIn")).Begin(this);
        }, DispatcherPriority.Loaded);

        StartDismissTimer();
    }
    
    private void ApplyPrecisePosition(double cursorX, double cursorY)
    {
        UpdateLayout();

        var anchor = MainBorder.TranslatePoint(new Point(0, 0), this);
        double padX = anchor.X, padY = anchor.Y;
        var tw = MainBorder.ActualWidth  > 10 ? MainBorder.ActualWidth  : 120;
        var th = MainBorder.ActualHeight > 10 ? MainBorder.ActualHeight : 40;

        var sb = ScreenHelper.GetScreenBounds(new Point(cursorX, cursorY));
        double wpfX = cursorX / _dpiX, wpfY = cursorY / _dpiY;
        double sL = sb.Left / _dpiX, sT = sb.Top  / _dpiY;
        double sR = sb.Right / _dpiX, sB = sb.Bottom / _dpiY;

        var left = wpfX - (padX + tw / 2);
        var top  = wpfY - (padY + th) - 15;

        if (left < sL + 8) left = sL + 8;
        if (left + tw > sR - 8) left = sR - 8 - tw;
        if (top < sT + 8) top = wpfY + 20;
        if (top + th > sB - 8) top = sB - 8 - th;
        left = Math.Max(left, sL);
        top  = Math.Max(top, sT);

        Left = left - padX;
        Top  = top - padY;
    }
    // ── Dismiss ──────────────────────────────────────────────────

    private void OnDismissTimerTick(object? sender, EventArgs e)
    {
        // 拖拽移动期间暂停自动关闭，否则长拖拽会触发计时器把鼠标移出工具栏而误隐藏。
        if (_isDragging)
        {
            StartDismissTimer();
            return;
        }

        NativeMethods.GetCursorPos(out var pt);
        if (IsPointInside(pt.X, pt.Y))
        {
            StartDismissTimer();
            return;
        }

        HideToolbar();
    }

    private void StartDismissTimer()
    {
        _dismissTimer.Stop();
        // 拖拽 / 右键菜单 / 子菜单编辑模式进行中都不自动收起。
        if (_draggingAction != null || _activeActionMenu != null || _editMode) return;
        var timeout = SettingsManager.Current.ToolbarDismissTimeout;
        if (timeout <= 0) return;
        _dismissTimer.Interval = TimeSpan.FromMilliseconds(timeout);
        _dismissTimer.Start();
    }

    public void HideToolbar()
    {
        HideToolbar(Volatile.Read(ref _operationContext));
    }

    internal void HideToolbarIfOperationStale()
    {
        var context = Volatile.Read(ref _operationContext);
        if (context != null && !context.Operation.IsCurrent)
            HideToolbar(context);
    }

    private void HideToolbar(ToolbarOperationContext? expectedContext)
    {
        // 拖动进行中不隐藏窗口：关闭弹出层/窗口会让 OLE 拖动的 dragSource 退出可视树，拖动循环挂死。
        if (_draggingAction != null) return;
        var currentContext = Volatile.Read(ref _operationContext);
        if (expectedContext != null
            && !ReferenceEquals(expectedContext, currentContext))
            return;

        currentContext?.Operation.InvalidateIfCurrent();
        if (!IsVisible) return;
        _generation++;
        _dismissTimer.Stop();
        _editMode = false;
        _hoverPreviewMode = false;
        SubMenuPopup.IsOpen = false;
        CloseTranslatePopup();
        var fadeOut = (Storyboard)FindResource("FadeOut");
        fadeOut.Stop(this);
        fadeOut.Completed -= FadeOut_Completed;
        fadeOut.Completed += FadeOut_Completed;
        fadeOut.Begin(this);
    }

    private void FadeOut_Completed(object? sender, EventArgs e)
    {
        var fadeOut = (Storyboard)FindResource("FadeOut");
        fadeOut.Completed -= FadeOut_Completed;
        Hide();
    }

    public bool IsPointInside(int screenX, int screenY)
    {
        if (!IsVisible) return false;

        // Compare in physical pixels everywhere. The toolbar's Left/Top/ActualWidth/Height are
        // in DIPs of the monitor where it was placed (stored as _dpiX/_dpiY at show time). The
        // sub-menu popup may render on a *different* monitor (WPF auto-positions to keep it on
        // screen) and so needs its own DPI lookup — sharing _dpiX/_dpiY with the toolbar is
        // wrong when the two are on monitors of different scale.
        var mainDpiX = _dpiX > 0 ? _dpiX : 1.0;
        var mainDpiY = _dpiY > 0 ? _dpiY : 1.0;
        var mainL = Left * mainDpiX;
        var mainT = Top * mainDpiY;
        var mainR = mainL + ActualWidth * mainDpiX;
        var mainB = mainT + ActualHeight * mainDpiY;
        if (screenX >= mainL && screenX <= mainR && screenY >= mainT && screenY <= mainB)
            return true;

        // 子菜单与翻译弹层都可能渲染在别的显示器（WPF 自动保持屏幕内），各自按自身 DPI 换算。
        if (PointInsidePopup(SubMenuPopup, screenX, screenY)) return true;
        if (PointInsidePopup(TranslatePopup, screenX, screenY)) return true;

        return false;
    }

    private static bool PointInsidePopup(Popup popup, int screenX, int screenY)
    {
        if (!popup.IsOpen || popup.Child is not FrameworkElement child) return false;
        try
        {
            var pt = child.PointToScreen(new Point(0, 0));
            var popupDpi = ScreenHelper.GetDpiForPoint(pt);
            var pdx = popupDpi.X > 0 ? popupDpi.X : 1.0;
            var pdy = popupDpi.Y > 0 ? popupDpi.Y : 1.0;
            var popR = pt.X + child.ActualWidth * pdx;
            var popB = pt.Y + child.ActualHeight * pdy;
            return screenX >= pt.X && screenX <= popR && screenY >= pt.Y && screenY <= popB;
        }
        catch
        {
            return false;
        }
    }

    // ── Type badge ───────────────────────────────────────────────

    private void UpdateTypeBadge()
    {
        if (_analysis.Type != TextType.PlainText)
        {
            TypeBadge.Visibility = Visibility.Visible;
            TypeLabel.Text = _analysis.Type switch
            {
                TextType.Url => "URL", TextType.Email => "EMAIL",
                TextType.FilePath => "FILE PATH", TextType.Json => "JSON",
                TextType.ColorCode => $"COLOR {_analysis.Metadata?.GetValueOrDefault("format", "")?.ToUpper()}",
                TextType.XmlHtml => _analysis.Metadata?.GetValueOrDefault("subtype", "xml")?.ToUpper() ?? "XML",
                TextType.MathExpression => "MATH",
                TextType.IpAddress => _analysis.Metadata?.GetValueOrDefault("version", "IP") ?? "IP",
                TextType.Uuid => "UUID", TextType.Base64 => "BASE64", TextType.Jwt => "JWT",
                TextType.DateTime => "DATE/TIME",
                TextType.Unit => $"UNIT {_analysis.Metadata?.GetValueOrDefault("symbol", "")}".TrimEnd(),
                _ => ""
            };
        }
        else
        {
            TypeBadge.Visibility = Visibility.Collapsed;
        }
    }

    // ── Drag handle ─────────────────────────────────────────────

    private void DragHandle_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!IsVisible)
        {
            Log.Warn("DragHandle down ignored — toolbar not visible");
            return;
        }

        // Record the grab offset between the cursor (physical px = DIP × monitor scale) and the
        // window's top-left corner, so the toolbar keeps the same relative grab point while moving.
        NativeMethods.GetCursorPos(out var pt);
        _dragOffsetX = pt.X - Left * _dpiX;
        _dragOffsetY = pt.Y - Top * _dpiY;
        _isDragging = true;
        _dismissTimer.Stop(); // 拖拽期间不自动关闭
        e.Handled = true;
        try
        {
            if (!DragHandle.CaptureMouse()) _isDragging = false;
        }
        catch (Exception ex)
        {
            Log.Error("DragHandle CaptureMouse threw", ex);
            _isDragging = false;
        }
    }

    private void DragHandle_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDragging) return;
        NativeMethods.GetCursorPos(out var pt);
        var newLeft = (pt.X - _dragOffsetX) / _dpiX;
        var newTop = (pt.Y - _dragOffsetY) / _dpiY;
        ClampToolbarToScreen(pt, ref newLeft, ref newTop);
        Left = newLeft;
        Top = newTop;
        // 子菜单定位相对工具栏（MainBorder）锚定；拖动改变窗口角点不会动 popup 自身，
        // 需强制其重新计算位置跟随工具栏。
        RefreshOpenPopupPlacements();
        // Throttle logging: mouse moves fire far more often than the 10 MB rotated log needs.
        if (_dragTick++ % 40 == 0)
            Log.Info($"DragHandle MOVE #{_dragTick}: -> Left={newLeft:0.##} Top={newTop:0.##}");
        e.Handled = true;
    }

    private void DragHandle_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isDragging) return;
        _isDragging = false;
        _dragTick = 0;
        try
        {
            DragHandle.ReleaseMouseCapture();
        }
        catch
        {
        }

        e.Handled = true;
        StartDismissTimer();
    }

    /// <summary>Re-runs the placement callback of every open card popup so it follows a toolbar drag.
    /// 拖动可能把工具栏带到屏幕另一侧，先重判弹出方向（含 VerticalOffset 取反），再刷新位置。</summary>
    private void RefreshOpenPopupPlacements()
    {
        if (SubMenuPopup.IsOpen)
        {
            AlignSubMenuDirection();
            RepositionPopup(SubMenuPopup);
        }
        if (TranslatePopup.IsOpen)
        {
            AlignTranslateDirection();
            RepositionPopup(TranslatePopup);
        }
    }

    private static void RepositionPopup(Popup popup)
    {
        if (!popup.IsOpen || popup.Child is not FrameworkElement child) return;
        try
        {
            // The public surface has no re-run - tell the Popup to invalidate its placement via
            // this internal hook (re-invokes the placement logic with the current target geometry).
            var method = popup.GetType().GetMethod("Reposition",
                BindingFlags.Instance | BindingFlags.NonPublic);
            if (method != null) method.Invoke(popup, null);
            else child.InvalidateMeasure();
        }
        catch
        {
            /* keep dragging smooth even if a popup refresh hiccups */
        }
    }

    /// <summary>
    ///     Clamps the toolbar window's top-left corner (in DIPs) so its MainBorder stays fully inside
    ///     the monitor under <paramref name="pt" /> — with a small margin so it never hugs the edge.
    ///     Anchored on MainBorder (which owns the visible surface) rather than the window, because the
    ///     window includes the transparent drop-shadow margin.
    /// </summary>
    private void ClampToolbarToScreen(NativeMethods.POINT pt, ref double left, ref double top)
    {
        if (MainBorder == null) return;
        var anchor = MainBorder.TranslatePoint(new Point(0, 0), this);
        double padX = anchor.X, padY = anchor.Y;
        var tw = MainBorder.ActualWidth > 10 ? MainBorder.ActualWidth : 120;
        var th = MainBorder.ActualHeight > 10 ? MainBorder.ActualHeight : 40;

        // 使用光标当前所在监视器的DP I工作区。
        var dpi = ScreenHelper.GetDpiForPoint(new Point(pt.X, pt.Y));
        var dpiX = dpi.X > 0 ? dpi.X : _dpiX;
        var dpiY = dpi.Y > 0 ? dpi.Y : _dpiY;
        var sb = ScreenHelper.GetScreenBounds(new Point(pt.X, pt.Y));
        double sL = sb.Left / dpiX, sT = sb.Top / dpiY;
        double sR = sb.Right / dpiX, sB = sb.Bottom / dpiY;

        double sMin = sL + 8, tMin = sT + 8;
        double cl = left + padX, ct = top + padY;
        if (cl < sMin) cl = sMin;
        if (cl + tw > sR - 8) cl = Math.Max(sMin, sR - 8 - tw);
        if (ct < tMin) ct = tMin;
        if (ct + th > sB - 8) ct = Math.Max(tMin, sB - 8 - th);
        left = cl - padX;
        top = ct - padY;
    }

    // ── Top-level button handlers ────────────────────────────────

    private async void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        Log.Info("Copy button clicked");
        var gen = _generation;
        if (!TryStartToolbarAction(out var operation)) return;
        if (!await operation.CanUseSelectionAsync())
        {
            if (_generation == gen) await ShowFailureAndHide("选择已更改--复制已取消");
            return;
        }

        if (!ClipboardTransaction.TryCommitClipboardMutation(
                operation, () => TrySetClipboardText(_selectedText)))
        {
            await ShowFailureAndHide("无法写入剪贴板--请重试");
            return;
        }

        await ShowCopiedToast();
        // Don't hide if a new selection reshowed the toolbar during the toast.
        if (_generation == gen) HideToolbar();
    }

    private async void PasteButton_Click(object sender, RoutedEventArgs e)
    {
        var generation = _generation;
        if (!TryStartToolbarAction(out var operation)) return;
        var pasteOutcome = await InputExecutor.SimulatePasteAsync(operation);
        if (_generation != generation) return;
        switch (pasteOutcome.Status)
        {
            case InputExecutor.InputInjectionStatus.Succeeded:
                HideToolbar();
                return;
            case InputExecutor.InputInjectionStatus.Partial:
                await ShowFailureAndHide(
                    InputExecutor.CanRollbackAfterPartialPaste(pasteOutcome)
                        ? "松开被按住的键后，Windows 拒绝了粘贴快捷键"
                        : pasteOutcome.CleanupSucceeded
                            ? "Windows 仅接受了部分粘贴快捷键"
                            : "Windows 接受了部分粘贴快捷键，且按键释放不完整");
                return;
            case InputExecutor.InputInjectionStatus.Rejected:
            default:
                await ShowFailureAndHide("焦点已移动 — 粘贴已取消");
                break;
        }
    }

    private async void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        var generation = _generation;
        if (!TryStartToolbarAction(out var operation)) return;
        var deleteOutcome = await InputExecutor.SimulateDeleteAsync(operation);
        if (_generation != generation) return;
        if (deleteOutcome.Status == InputExecutor.InputInjectionStatus.Succeeded)
        {
            HideToolbar();
            return;
        }

        if (deleteOutcome.Status == InputExecutor.InputInjectionStatus.Partial)
        {
            await ShowFailureAndHide(
                deleteOutcome.CleanupSucceeded
                    ? "Windows 仅接受了部分删除快捷键"
                    : "Windows 接受了部分删除快捷键，且按键释放不完整");
            return;
        }

        await ShowFailureAndHide("焦点已移动 — 删除已取消");
    }

    // ── CategoryButton hover handle ────────────────────────────────
    
    /// <summary>hover 模式下的延迟关闭：给鼠标从按钮移到 Popup 的"真空带"留时间。</summary>
    private void ScheduleHoverClose()
    {
        CancelHoverClose();
        _hoverCloseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _hoverCloseTimer.Tick += (_, _) =>
        {
            CancelHoverClose();

            // 拖动进行中绝不关闭弹出层：dragSource 在其中，关闭会让 OLE 拖动循环挂死。
            if (_draggingAction != null) return;
            // 右键菜单打开期间绝不关闭弹出层：菜单的 PlacementTarget 是弹出层里的按钮，
            // 关闭弹出层会把按钮摘出可视树，菜单失去宿主后留在屏幕上没人收。
            if (_activeActionMenu != null) return;
            // 编辑模式下弹出层不再自动收起：用户要在这个列表里连续勾选/拖动。
            if (_editMode) return;
            // 到点时如果鼠标还在按钮或 Popup 上，就放弃关闭
            if (SubMenuPopup.IsMouseOver) return;
            if (TransformButton.IsMouseOver || EncodeButton.IsMouseOver) return;

            SubMenuPopup.IsOpen = false;
            _editMode = false;
            _hoverPreviewMode = false;
            ResetPreview();
        };
        _hoverCloseTimer.Start();
    }

    private void CancelHoverClose()
    {
        _hoverCloseTimer?.Stop();
        _hoverCloseTimer = null;
    }
    private void CategoryButton_Enter(object sender, MouseEventArgs e)
    {
        if (!SettingsManager.Current.HoverOpen) return;
        if (sender is not Button { Tag: ActionCategory category }) return;
        // 拖动进行中不切换/重建子菜单：拖动路径会经过分类按钮，重建会清空 SubMenuPanel.Children，
        // 把 OLE 的 dragSource 从可视树里摘掉，拖动被立即取消（“拖不动”的来源之一）。
        if (_draggingAction != null) return;
        // 鼠标重新进入按钮 → 取消待关闭
        CancelHoverClose();

        var groupName = CategoryToGroupName(category);

        // 已经是这个分组，不重建（避免每次 hover 都闪烁）
        if (SubMenuPopup.IsOpen && _currentSubMenuGroup == groupName) return;

        _hoverPreviewMode = true;   // 关键：让 ShowSubMenu 不走 toggle 分支
        ShowSubMenu(groupName, category);
    }

    private void CategoryButton_Leave(object sender, MouseEventArgs e)
    {
        if (!SettingsManager.Current.HoverOpen) return;
        if (!SubMenuPopup.IsOpen) return;
        if (_draggingAction != null) return; // 拖动中保持弹出层存活（dragSource 在其中）
        ScheduleHoverClose();
    }

    private void SubMenuRoot_MouseEnter(object sender, MouseEventArgs e)
    {
        CancelHoverClose();
    }

    private void SubMenuRoot_MouseLeave(object sender, MouseEventArgs e)
    {
        if (!SettingsManager.Current.HoverOpen) return;
        if (!SubMenuPopup.IsOpen) return;
        if (_draggingAction != null) return; // 拖动中保持弹出层存活（dragSource 在其中）
        ScheduleHoverClose();
    }
    
    /// <summary>ActionCategory → ShowSubMenu 内部使用的 groupName。</summary>
    private static string CategoryToGroupName(ActionCategory category) => category switch
    {
        ActionCategory.Transform => "Transform",
        ActionCategory.Encode    => "Encode",
        _ => category.ToString()
    };
    private void TransformButton_Click(object sender, RoutedEventArgs e)
    {
        ShowSubMenu("Transform", ActionCategory.Transform);
    }

    private void EncodeButton_Click(object sender, RoutedEventArgs e)
    {
        ShowSubMenu("Encode", ActionCategory.Encode);
    }
    
    private void SearchButton_Click(object sender, RoutedEventArgs e)
    {
        ShowSubMenu("Search", ActionCategory.Search);
    }

    // 翻译固定按钮：从当前上下文动作中解析 translate 动作并走标准执行链路（与动态按钮一致）。
    private void TranslationButton_Click(object sender, RoutedEventArgs e)
    {
        var action = _actionGroups.SelectMany(g => g.Actions).FirstOrDefault(a => a.Id == "translate");
        if (action == null) return;
        TranslationButton.Tag = action;
        ActionButton_Click(sender, e);
    }

    // Use *Ptr variants — 32-bit truncation in the legacy GetWindowLong/SetWindowLong corrupts
    // high-bit ex-style flags on 64-bit Windows. The current style mask (NOACTIVATE | TOOLWINDOW
    // = 0x08000080) fits in 32 bits so the legacy path worked, but new flags in future edits
    // could silently drop.
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    private sealed class ToolbarOperationContext(SelectionOperation operation)
    {
        internal SelectionOperation Operation { get; } = operation;
        internal OperationActionGate ActionGate { get; } = new();
    }
    // ── TranslatePopup resize ────────────────────────────────────

    /// <summary>右下角缩放手柄：拖拽改变翻译卡尺寸，并重新评估上下弹出方向。</summary>
    private void TranslateResizeThumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        // 首次缩放时，把当前实际尺寸固化成显式值（保证后续累加可信）
        if (double.IsNaN(TranslateCard.Width) || TranslateCard.Width <= 0)
            TranslateCard.Width = TranslateCard.ActualWidth > 0 ? TranslateCard.ActualWidth : 420;
        if (double.IsNaN(TranslateCard.Height) || TranslateCard.Height <= 0)
            TranslateCard.Height = TranslateCard.ActualHeight > 0 ? TranslateCard.ActualHeight : 400;

        var newWidth  = TranslateCard.Width  + e.HorizontalChange;
        var newHeight = TranslateCard.Height + e.VerticalChange;
        
        ClampTranslateSize();
        
        // 下限：由 XAML 的 MinWidth/MinHeight 决定
        newWidth  = Math.Max(TranslateCard.MinWidth,  newWidth);
        newHeight = Math.Max(TranslateCard.MinHeight, newHeight);

        TranslateCard.Width  = Math.Clamp(newWidth,  TranslateCard.MinWidth,  TranslateCard.MaxWidth);
        TranslateCard.Height = Math.Clamp(newHeight, TranslateCard.MinHeight, TranslateCard.MaxHeight);

        ClampTranslateSize();
        // 尺寸变了，可能原来"下方放得下"现在放不下了 → 重判方向（含 VerticalOffset 取反）
        // AlignTranslateDirection();
    }
}