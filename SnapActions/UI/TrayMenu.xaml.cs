using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Controls;
using H.NotifyIcon;
using SnapActions.Config;
using SnapActions.Helpers;
using ContextMenu = System.Windows.Controls.ContextMenu;
using MenuItem = System.Windows.Controls.MenuItem;
using TextBlock = System.Windows.Controls.TextBlock;

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
    private MenuItem? _enableItem;
    private MenuItem? _autoStartItem;
    private TextBlock? _statusHeader;

    public void Dispose()
    {
        _trayIcon?.Dispose();
        GC.SuppressFinalize(this);
    }

    public void Initialize()
    {
        _trayIcon = (TaskbarIcon)Application.Current.FindResource("TrayIcon");
        var menu = (ContextMenu)Application.Current.FindResource("TrayContextMenu");

        _enableItem = Item(menu, "enable");
        _autoStartItem = Item(menu, "autostart");
        _statusHeader = menu.Items.OfType<TextBlock>().FirstOrDefault(t => (string?)t.Tag == "status");

        if (_enableItem != null)
        {
            _enableItem.Checked += (_, _) => SetEnabled(true);
            _enableItem.Unchecked += (_, _) => SetEnabled(false);
        }
        if (_autoStartItem != null)
        {
            _autoStartItem.Checked += (_, _) => SetAutoStart(true);
            _autoStartItem.Unchecked += (_, _) => SetAutoStart(false);
        }
        if (Item(menu, "settings") is { } settingsItem) settingsItem.Click += (_, _) => ShowSettings();
        if (Item(menu, "exit") is { } exitItem) exitItem.Click += (_, _) => Application.Current.Shutdown();

        // Refresh check states from settings every time the tray menu opens so changes made via the
        // Settings window don't leave the tray showing stale state.
        menu.Opened += (_, _) => SyncStates();

        _trayIcon.Icon = CreateDefaultIcon();
        _trayIcon.TrayMouseDoubleClick += (_, _) => ShowSettings();
        _trayIcon.ForceCreate();
    }

    /// <summary>按 Tag 找菜单项：外观（XAML）与行为（本文件）通过 Tag 约定连接，互不依赖控件顺序。</summary>
    private static MenuItem? Item(ContextMenu menu, string tag) =>
        menu.Items.OfType<MenuItem>().FirstOrDefault(i => (string?)i.Tag == tag);

    private void SyncStates()
    {
        if (_enableItem != null) _enableItem.IsChecked = SettingsManager.Current.Enabled;
        if (_autoStartItem != null) _autoStartItem.IsChecked = SettingsManager.Current.AutoStart;
        if (_statusHeader != null)
            _statusHeader.Text = SettingsManager.Current.Enabled ? "SnapActions · 已启用" : "SnapActions · 已暂停";
    }

    private static void SetEnabled(bool enabled)
    {
        // Avoid recursion: only act when the user changed it (not the Opening sync above).
        if (SettingsManager.Current.Enabled == enabled) return;
        SettingsManager.Current.Enabled = enabled;
        SettingsManager.Save();
    }

    private static void SetAutoStart(bool enabled)
    {
        if (SettingsManager.Current.AutoStart == enabled) return;
        SettingsManager.SetAutoStart(enabled);
    }

    private void ShowSettings()
    {
        if (_settingsWindow is { IsVisible: true })
        {
            _settingsWindow.Activate();
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
