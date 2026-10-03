namespace SnapActions.UI;

/// <summary>
/// Single source of truth for the Segoe Fluent icon glyph codes used across the toolbar and its
/// result popups. Both the dynamic action-icon map (ToolbarWindow.Buttons.cs) and the XAML
/// &lt;ui:FontIcon Glyph&gt; usages reference these constants via {x:Static}, so an icon is defined
/// in exactly one place. 码点集沿用 CollocationToolbar-old 备份项目的 IconGlyphs，并补充本项目
/// 需要的子菜单箭头与设置导航图标。
/// </summary>
public static class IconGlyphs
{
    // 顶级工具栏操作
    public const string Copy     = "\uE8C8";   // Copy
    public const string Paste    = "\uE77F";   // Paste
    public const string Delete   = "\uE74D";   // Delete
    public const string Search   = "\uE721";   // Search
    public const string Settings = "\uE713";   // Gear / Settings
    public const string More     = "\uE712";   // More（三点）
    public const string EnableActions = "\uE74C";  // EnableActions

    // 结果弹出/翻译卡 chrome
    public const string Close     = "\uE711";  // Close (X)
    public const string Swap      = "\uE8AB";  // Translate direction / convert
    public const string Retry     = "\uE72C";  // Retry
    public const string Replace   = "\uE70F";  // Replace
    public const string Pin       = "\uE77A";  // Pin — 图钉（阻止自动关闭）
    public const string PinActive = "\uE718";  // Pinned — 图钉已激活态（按下/固定）
    public const string Unpin     = "\uE77B";  // Unpin — 未固定（编辑模式开关）
    public const string DragHandle = "\uEC13"; // Global Navigation — 拖动手柄（移动工具栏）

    // 动态动作图标（FluentGlyphs 地图）——按备份分组维护。
    public const string Transform   = "\uEF60";  // Switch — Transform category
    public const string RightBottomExpand = "\uF169";
    public const string TranslateTab = "\uF2B7"; 
    public const string Translate   = "\uE8C1";  // 翻译组合（左上主形）
    public const string TranslateSta   = "\uE982";  // 翻译组合（左上主形）
    public const string TranslateEn = "\uE97E";  // 翻译组合角标（A，右下）
    public const string Encode      = "\uE943";  // Code — Encode category
    public const string OpenUrl     = "\uE71B";  // Link
    public const string Email       = "\uE715";  // Mail
    public const string OpenFile    = "\uE8E5";  // OpenFile
    public const string Folder      = "\uE8B7";  // Folder
    public const string Time        = "\uE823";  // Clock
    public const string IpLookup    = "\uE774";  // Globe
    public const string Calculate   = "\uE8EF";  // Calculator
    public const string Context     = "\uE712";  // More（与 More 同形，备份复用之）

    // 子菜单排序箭头（备份未含，本项目 CreateSubMenuButton 使用）
    public const string ChevronUp   = "\uE70E";  // ChevronUp
    public const string ChevronDown = "\uE70D";  // ChevronDown

    // 设置窗口左侧导航图标
    public const string Add  = "\uE710";  // Add
    public const string Apps = "\uE8F1";  // AllApps
    
    // 系统托盘菜单图标
    public const string Computer = "\uea6c";  // Computer
    public const string AutoStartEnable = "\ue8fb";  // AutoStartEnable
    public const string AutoStartDisable = "\ue711";  // AutoStartDisable
    public const string ToggleOn = AutoStartEnable;   // 动作组启用态（与开机自启同形）
    public const string ToggleOff = AutoStartDisable; // 动作组停用态
    public const string Exit = "\ue7e8";  // Exit
}