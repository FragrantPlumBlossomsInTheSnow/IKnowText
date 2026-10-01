using System.IO;
using System.Runtime.InteropServices;
using SnapActions.Config;
using SnapActions.Helpers;

namespace SnapActions.UI;

public class TrayIconManager : IDisposable
{
    private ContextMenuStrip? _contextMenu;
    private SettingsWindow? _settingsWindow;
    private NotifyIcon? _trayIcon;

    public void Dispose()
    {
        _trayIcon?.Dispose();
        _contextMenu?.Dispose();
        GC.SuppressFinalize(this);
    }

    public void Initialize()
    {
        _contextMenu = new ContextMenuStrip();

        var enableItem = new ToolStripMenuItem("启用")
        {
            Checked = SettingsManager.Current.Enabled,
            CheckOnClick = true
        };
        enableItem.CheckedChanged += (_, _) =>
        {
            // Avoid recursion: only act when the user changed it (not the Opening sync below).
            if (SettingsManager.Current.Enabled == enableItem.Checked) return;
            SettingsManager.Current.Enabled = enableItem.Checked;
            SettingsManager.Save();
        };

        var settingsItem = new ToolStripMenuItem("设置...");
        settingsItem.Click += (_, _) => ShowSettings();

        var autoStartItem = new ToolStripMenuItem("开机自启")
        {
            Checked = SettingsManager.Current.AutoStart,
            CheckOnClick = true
        };
        autoStartItem.CheckedChanged += (_, _) =>
        {
            if (SettingsManager.Current.AutoStart == autoStartItem.Checked) return;
            SettingsManager.SetAutoStart(autoStartItem.Checked);
        };

        // Refresh check states from settings every time the tray menu opens so changes
        // made via the Settings window don't leave the tray showing stale state.
        _contextMenu.Opening += (_, _) =>
        {
            enableItem.Checked = SettingsManager.Current.Enabled;
            autoStartItem.Checked = SettingsManager.Current.AutoStart;
        };

        var exitItem = new ToolStripMenuItem("退出");
        exitItem.Click += (_, _) => Application.Current.Shutdown();

        _contextMenu.Items.Add(enableItem);
        _contextMenu.Items.Add(autoStartItem);
        _contextMenu.Items.Add(new ToolStripSeparator());
        _contextMenu.Items.Add(settingsItem);
        _contextMenu.Items.Add(new ToolStripSeparator());
        _contextMenu.Items.Add(exitItem);

        _trayIcon = new NotifyIcon
        {
            Text = "SnapActions",
            Icon = CreateDefaultIcon(),
            Visible = true,
            ContextMenuStrip = _contextMenu
        };

        _trayIcon.DoubleClick += (_, _) => ShowSettings();
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

    public void ShowReadyNotification()
    {
        if (_trayIcon == null) return;
        try
        {
            _trayIcon.ShowBalloonTip(2000, "SnapActions", "准备就绪", ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            Log.Warn($"Tray ready notification failed: {ex.Message}");
        }
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