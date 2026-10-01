using System.Reflection;
using System.Windows;
using Microsoft.Win32;
using SnapActions.Config;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using Wpf.Ui.Markup;

namespace SnapActions.UI;

internal static class ThemeManager
{
    internal static void Start() { Apply(); SystemEvents.UserPreferenceChanged += OnPreferenceChanged; }
    internal static void Stop() => SystemEvents.UserPreferenceChanged -= OnPreferenceChanged;

    private static void OnPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (SettingsManager.Current.Theme == "system") Application.Current?.Dispatcher.InvokeAsync(Apply);
    }

    /// <summary>
    /// 明暗配色由 ThemesDictionary（Light/Dark 两套 Fluent token）接管。这里把用户的主题设置
    /// (system/light/dark) 翻译成主题字典的 Theme 并强制收敛，触发全局 DynamicResource
    /// 重估即可换肤。
    /// 注意：不走 <see cref="ApplicationThemeManager.Apply"/> —— 它会遍历所有窗口调用内部
    /// WindowBackgroundManager 重设窗口背景，把 AllowsTransparency 的划词工具栏（layered window）
    /// 涂成不透明底色，造成“切换主题后工具栏出现大背景框”。纯字典切换同榜单次生效，且不影响
    /// 工具栏的透明卡片/阴影观感。
    /// </summary>
    internal static void Apply()
    {
        if (Application.Current == null) return;
        ApplicationTheme appTheme;
        switch (SettingsManager.Current.Theme)
        {
            case "light": appTheme = ApplicationTheme.Light; break;
            case "dark": appTheme = ApplicationTheme.Dark; break;
            // 兜底：跟随系统明暗（含旧配置残留的 "translucent" 值）。
            default:
                appTheme = ApplicationThemeManager.GetSystemTheme() switch
                {
                    SystemTheme.Dark or SystemTheme.Glow or SystemTheme.CapturedMotion => ApplicationTheme.Dark,
                    _ => ApplicationTheme.Light,
                };
                break;
        }

        // 接管主题字典实例：让主题位字典始终是显式的 ThemesDictionary 并重新设置 Theme，
        // 其 setter 按主题重设 Source，触发全局 DynamicResource 一次性重估（同 Apply 的字典路径）。
        ForceThemeDictionary(appTheme);

        // WPF-UI 的 ApplicationThemeManager 只在 Apply() 里更新内部缓存；而我们刻意不走 Apply
        // （它会重设窗口背景、破坏 AllowsTransparency 工具栏）。不同步的话 GetAppTheme() 永远返回
        // 进程初值主题，重开 FluentWindow（设置窗口）时其 Mica 背景/标题栏会按旧的缓存主题构建，
        // 造成“切换主题后关闭再打开设置窗口仍残留上一次主题”。这里仅反射同步该静态缓存，
        // 不触发库的字典替换/背景遍历等副作用。
        SyncWpfUiThemeCache(appTheme);
        RefreshOpenSettingsWindowBackdrop(appTheme);

        // 主题切换会触发所有窗口重绘，WPF 的 layered window（AllowsTransparency=True）偶发残留
        // 不透明底色。等当前渲染完成后，对可见的透明窗口做一次合成表面重建兜底。
        Application.Current.Dispatcher.InvokeAsync(
            RefreshTransparentSurfaces, System.Windows.Threading.DispatcherPriority.Loaded);
        // 注：工具栏配色已跟随主题 token（DynamicResource 自动重估），无需逐窗口刷新材质。
    }

    /// <summary>
    /// 同步 WPF-UI 内部缓存的当前应用主题（_cachedApplicationTheme, 私有静态字段）。
    /// 字段名/位置以 WPF-UI 4.3.0 为准（PackageReference 已锁定版本），失效时静默跳过。
    /// </summary>
    private static void SyncWpfUiThemeCache(ApplicationTheme appTheme)
    {
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Static;
        typeof(ApplicationThemeManager).GetField("_cachedApplicationTheme", flags)?.SetValue(null, appTheme);
    }

    /// <summary>让已打开的设置窗口立即按新主题重建 Mica 窗口背景（仅限设置窗口，绝不触碰
    /// AllowsTransparency 的划词工具栏）。掉队时静默忽略——重开的窗口本就由缓存主题保证。</summary>
    private static void RefreshOpenSettingsWindowBackdrop(ApplicationTheme appTheme)
    {
        try
        {
            foreach (Window window in Application.Current.Windows)
                if (window is SettingsWindow { IsVisible: true })
                    WindowBackgroundManager.UpdateBackground(window, appTheme, WindowBackdropType.Mica);
        }
        catch { /* 背景刷新为尽力而为 */ }
    }

    /// <summary>
    /// 强制重建可见的 AllowsTransparency 窗口的合成表面：通过瞬时 1px 位移 + 重绘让 DWM
    /// 重新合成 alpha 通道，清除主题切换后残留的矩形不透明背景。不改可见性，几乎不可感知。
    /// </summary>
    private static void RefreshTransparentSurfaces()
    {
        if (Application.Current == null) return;
        foreach (Window window in Application.Current.Windows)
        {
            if (!window.AllowsTransparency || !window.IsVisible) continue;
            var left = window.Left;
            var top = window.Top;
            window.Left = left + 0.01;
            window.Top = top;
            window.Left = left;
            window.Top = top;
            window.InvalidateVisual();
        }
    }

    /// <summary>
    /// 找到应用资源主题位（合并字典中第一个指向 WPF-UI Theme 目录的字典），确保其为
    /// <see cref="ThemesDictionary"/> 实例并重新设置 Theme —— 其 setter 会按主题重设 Source，
    /// 触发全局 DynamicResource 一次性重估。找不到匹配字典则不动（无主题字典可切）。
    /// </summary>
    private static void ForceThemeDictionary(ApplicationTheme theme)
    {
        var merged = Application.Current.Resources.MergedDictionaries;
        for (var i = 0; i < merged.Count; i++)
        {
            if (merged[i] is ThemesDictionary td)
            {
                td.Theme = theme;
                return;
            }
            if (merged[i]?.Source?.ToString() is { } src
                && src.Contains("wpf.ui;", StringComparison.OrdinalIgnoreCase)
                && src.Contains("/theme/", StringComparison.OrdinalIgnoreCase))
            {
                merged[i] = new ThemesDictionary { Theme = theme };
                return;
            }
        }
    }
}