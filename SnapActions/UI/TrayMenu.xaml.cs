using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using H.NotifyIcon;
using SnapActions.Config;
using SnapActions.Helpers;
using ContextMenu = System.Windows.Controls.ContextMenu;
using MenuItem = System.Windows.Controls.MenuItem;
using FrameworkElement = System.Windows.FrameworkElement;

namespace SnapActions.UI;

/// <summary>
///     托盘图标与它的上下文菜单。菜单外观在 <c>UI/TrayMenu.xaml</c>（由 App.xaml 合并），行为都在这里：
///     条目按 Tag 关联（enable / autostart / settings / exit），每次打开菜单都从设置同步勾选状态与状态行。
///     托盘用的是 H.NotifyIcon 的 WPF <see cref="TaskbarIcon"/>，菜单是真正的 WPF <see cref="ContextMenu"/>，
///     因此跟随应用主题与 WPF-UI 的 Fluent 菜单样式（旧的 WinForms ContextMenuStrip 只能吃系统样式）。
/// </summary>
public class TrayMenu : IDisposable
{
    private TaskbarIcon? _trayIcon;
    private SettingsWindow? _settingsWindow;
    private MenuItem? _autoStartItem;
    private MenuItem? _settingsItem;
    private MenuItem? _exitItem;

    public void Dispose()
    {
        _trayIcon?.Dispose();
        GC.SuppressFinalize(this);
    }

    public void Initialize()
    {
        _trayIcon = (TaskbarIcon)Application.Current.FindResource("TrayIcon");
        
        var menu = _trayIcon.ContextMenu;
        if (menu != null)
        {
            _autoStartItem = FindMenuItem(menu, "AutoStartItem");
            _settingsItem = FindMenuItem(menu, "SettingsItem");
            _exitItem = FindMenuItem(menu, "ExitItem");
        }

        if (_autoStartItem != null)
        {
            _autoStartItem.Click += (_, _) => SetAutoStart();
            SetAutoStartGlyph();
        }
        
        if (_settingsItem != null)
            _settingsItem.Click += (_, _) => ShowSettings();
        
        if (_exitItem != null)
            _exitItem.Click += (_, _) => Application.Current.Shutdown();

        // Refresh check states from settings every time the tray menu opens so changes made via the
        // Settings window don't leave the tray showing stale state.

        _trayIcon.Icon = CreateDefaultIcon();
        _trayIcon.TrayLeftMouseDown += (_, _) => ShowSettings();
        _trayIcon.ForceCreate();
    }
    
    private static MenuItem? FindMenuItem(ContextMenu menu, string tag)
    {
        foreach (var obj in menu.Items)
            if (obj is MenuItem mi && (string?)mi.Tag == tag)
                return mi;
        return null;
    }

    private static void SetAutoStart()
    {
        SettingsManager.SetAutoStart(!SettingsManager.Current.AutoStart);
        SetAutoStartGlyph();
        SettingsManager.Save();
    }
    
    private static void SetAutoStartGlyph()
    {
        TrayIconState.Instance.AutoStartGlyph = SettingsManager.Current.AutoStart
            ? IconGlyphs.AutoStartEnable
            : IconGlyphs.AutoStartDisable;
    }

    private void ShowSettings()
    {
        if (_settingsWindow is { IsVisible: true })
        {
            _settingsWindow.Close();
            return;
        }

        _settingsWindow = new SettingsWindow();
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    private static Icon CreateDefaultIcon()
    {
        // Try the embedded resource (survives single-file publish)
        try
        {
            var uri = new Uri("pack://application:,,,/app.ico", UriKind.Absolute);
            var sri = Application.GetResourceStream(uri);
            if (sri != null)
            {
                using var s = sri.Stream;
                return new Icon(s, 16, 16);
            }

            Log.Warn("Tray icon: pack:// resource not found; falling back to file");
        }
        catch (Exception ex)
        {
            Log.Warn($"Tray icon: pack:// load failed ({ex.Message}); falling back to file");
        }

        // Fallback to a side-by-side file (dev runs / framework-dependent publish)
        try
        {
            var icoPath = Path.Combine(AppContext.BaseDirectory, "app.ico");
            if (File.Exists(icoPath))
                return new Icon(icoPath, 16, 16);
            Log.Warn($"Tray icon: app.ico not found at {icoPath}; using generated icon");
        }
        catch (Exception ex)
        {
            Log.Warn($"Tray icon: file load failed ({ex.Message}); using generated icon");
        }

        // Last resort: generate programmatically
        using var bmp = new Bitmap(16, 16);
        using var g = Graphics.FromImage(bmp);
        using var accent = new SolidBrush(Color.FromArgb(137, 180, 250));
        using var dark = new SolidBrush(Color.FromArgb(30, 30, 46));
        g.Clear(Color.Transparent);
        g.FillRectangle(dark, 1, 1, 14, 14);
        // Cursor line
        g.FillRectangle(accent, 4, 3, 1, 10);
        // Text lines
        g.FillRectangle(accent, 6, 5, 7, 1);
        using var textBrush = new SolidBrush(Color.FromArgb(180, 205, 214, 244));
        g.FillRectangle(textBrush, 6, 8, 6, 1);
        g.FillRectangle(textBrush, 6, 11, 4, 1);
        // Green dot
        using var greenBrush = new SolidBrush(Color.FromArgb(166, 227, 161));
        g.FillRectangle(greenBrush, 12, 11, 2, 2);

        var hIcon = bmp.GetHicon();
        Icon? icon = null;
        try
        {
            icon = Icon.FromHandle(hIcon);
            // Clone() copies the icon image into an independently-managed handle, so destroying
            // hIcon below doesn't invalidate the returned Icon.
            return (Icon)icon.Clone();
        }
        finally
        {
            // Run regardless of whether Icon.FromHandle/Clone threw — otherwise the GDI handle
            // returned by GetHicon leaks for the lifetime of the process.
            icon?.Dispose();
            DestroyIcon(hIcon);
        }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);
}
