using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using iNKORE.UI.WPF.Modern.Controls.Primitives;
using SnapActions.Actions;
using SnapActions.Actions.UserActions;
using SnapActions.Config;
using SnapActions.Helpers;
using Brushes = System.Windows.Media.Brushes;
using CheckBox = System.Windows.Controls.CheckBox;
using ComboBox = System.Windows.Controls.ComboBox;
using Cursors = System.Windows.Input.Cursors;
using FluentWindow = Wpf.Ui.Controls.FluentWindow;
using FontFamily = System.Windows.Media.FontFamily;
using ListBox = System.Windows.Controls.ListBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using TextBlock = System.Windows.Controls.TextBlock;

namespace SnapActions.UI;

public partial class SettingsWindow : FluentWindow
{
    private readonly DispatcherTimer _saveDebounce;
    private bool _loading = true;
    private Brush? _secondaryBrush;
    private Brush? _textBrush;

    public SettingsWindow()
    {
        InitializeComponent();
        _textBrush = (Brush)FindResource("TextFillColorPrimaryBrush");
        _secondaryBrush = (Brush)FindResource("TextFillColorSecondaryBrush");

        // Show the version pulled from the assembly. ToString(3) drops the .0 build-revision
        // component so "1.6.13.0" displays as "v1.6.13" — matches the csproj <Version> and
        // the GitHub release tag.
        var v = Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = v != null ? $"v{v.ToString(3)}" : "";

        // Debounce auto-save so a fast typer in the excluded-apps box doesn't trigger 30 disk writes.
        // Save synchronously on the UI thread — settings are tiny (kilobytes) and the previous
        // Task.Run could race with a UI mutation (List.Add etc.), throwing inside JsonSerializer
        // and silently losing the save.
        _saveDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _saveDebounce.Tick += (_, _) =>
        {
            _saveDebounce.Stop();
            FlushPendingTextEdits();
            SaveWithStatus();
        };

        // Flush any pending change immediately on close so users don't lose edits.
        // Save synchronously so it can't lose the race against process exit if the user
        // closes Settings and then immediately Exits from the tray menu.
        Closing += (_, e) =>
        {
            if (_saveDebounce.IsEnabled || SettingsManager.LastSaveError != null)
            {
                _saveDebounce.Stop();
                FlushPendingTextEdits();
                e.Cancel = !SaveWithStatus();
            }
        };

        // Defer heavy loading to after render, use dispatcher idle priority
        ContentRendered += (_, _) =>
        {
            Dispatcher.InvokeAsync(() =>
            {
                LoadSettings();
                _loading = false;
            }, DispatcherPriority.ApplicationIdle);
        };
    }

    private ActionRegistry? Registry { get; set; }

    private void QueueSave()
    {
        if (_loading) return;
        _saveDebounce.Stop();
        _saveDebounce.Start();
        SaveStatusText.Text = "正在保存…";
    }

    internal void LoadSettings()
    {
        var s = SettingsManager.Current;
        var actuallyRegistered = AutoStartTask.IsRegistered();
        if (s.AutoStart != actuallyRegistered)
            s.AutoStart = actuallyRegistered;
        AutoStartCheck.IsChecked = s.AutoStart;
        EnabledCheck.IsChecked = s.Enabled;
        SelectCopyCheck.IsChecked = s.SelectCopy;
        HoverOpenCheck.IsChecked = s.HoverOpen;
        AutoStartCheck.IsChecked = s.AutoStart;
        // ReplaceSelectionCheck.IsChecked = s.ReplaceSelectionOnTransform;
        RestoreClipboardCheck.IsChecked = s.RestoreClipboardAfterAction;
        OnlineLookupsCheck.IsChecked = s.AllowOnlineLookups;
        // CaptureOnMouseSelectionCheck.IsChecked = s.CaptureOnMouseSelection;
        CaptureOnCtrlCCheck.IsChecked = s.CaptureOnCtrlC;
        UseSyntheticKeys.IsChecked = s.UseSyntheticKeys;

        SelectComboByTag(DismissTimeCombo, s.ToolbarDismissTimeout.ToString(), 2);
        SelectComboByTag(ShowDelayCombo, s.ToolbarShowDelay.ToString(), 0);
        SelectComboByTag(MultiClickCombo, s.MultiClickDelay.ToString(), 2);
        // SelectComboByTag(PasteModeCombo, PasteModeTagFor(s.PasteModeTrigger), 0);
        // SelectComboByTag(LongPressCombo, s.LongPressDuration.ToString(), 1);
        SelectComboByTag(MaxInlineCombo, s.MaxInlineContextActions.ToString(), 3);
        // SelectComboByTag(LanguageCombo, s.SearchLanguage, 0);
        // SelectComboByTag(CurrencyCombo, s.TargetCurrency, 0);

        ShowPasteCheck.IsChecked = s.ShowPasteActions;
        ShowTranslateCheck.IsChecked = s.ShowTranslateActions;
        ShowTransformCheck.IsChecked = s.ShowTransformActions;
        ShowEncodeCheck.IsChecked = s.ShowEncodeActions;
        ShowSearchCheck.IsChecked = s.ShowSearchActions;

        BuildPinnedAppsList();
        BuildSearchEnginesList();
        BuildAppProfilesList();
        LoadAdditionalSettings();
        ExcludedAppsBox.Text = string.Join("\n", s.ExcludedApps);
    }

    private static void SelectComboByTag(ComboBox combo, string tag, int fallback)
    {
        for (var i = 0; i < combo.Items.Count; i++)
            if (combo.Items[i] is ComboBoxItem item && item.Tag?.ToString() == tag)
            {
                combo.SelectedIndex = i;
                return;
            }

        combo.SelectedIndex = fallback;
    }

    private void BuildSearchEnginesList()
    {
        SearchEnginesPanel.Children.Clear();
        foreach (var engine in SettingsManager.Current.SearchEngines)
        {
            // var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 4) };

            var cb = new CheckBox
            {
                Content = engine.Name,
                IsChecked = engine.Enabled,
                Style = (Style)FindResource("SettingCheckBoxStyle"),
                Tag = engine.Id
            };
            cb.Checked += EngineToggle_Changed;
            cb.Unchecked += EngineToggle_Changed;
            SearchEnginesPanel.Children.Add(cb);

            // "lang" checkbox: apply global language filter to this engine
            // var langCb = new CheckBox
            // {
            //     Content = "lang", IsChecked = engine.UseLanguageFilter,
            //     Foreground = _secondaryBrush, FontSize = 10, Tag = engine.Id,
            //     VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0),
            //     ToolTip = "Apply the selected Search Language to this engine"
            // };
            // langCb.Checked += LangToggle_Changed;
            // langCb.Unchecked += LangToggle_Changed;
            // row.Children.Add(langCb);

            if (!engine.IsBuiltIn)
            {
                SearchEnginesPanel.Children.Add(new TextBlock
                {
                    Text = engine.UrlTemplate.Length > 40 ? engine.UrlTemplate[..40] + "..." : engine.UrlTemplate,
                    FontSize = 10, Foreground = _secondaryBrush,
                    VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0)
                });

                var delBtn = new Button
                {
                    Content = "X", Width = 24, Height = 24, FontSize = 10,
                    Padding = new Thickness(0),
                    Margin = new Thickness(6, 0, 0, 0),
                    Background = Brushes.Transparent, Foreground = _secondaryBrush,
                    BorderThickness = new Thickness(0), Tag = engine.Id,
                    Cursor = Cursors.Hand
                };
                delBtn.Click += DeleteEngine_Click;
                SearchEnginesPanel.Children.Add(delBtn);
            }

            // Windows 设置风格：引擎行之间用 1px 分隔线隔开
            if (engine != SettingsManager.Current.SearchEngines.Last())
                SearchEnginesPanel.Children.Add(new Border
                {
                    Style = (Style)FindResource("SettingDivider")
                });
        }
    }

    private void BuildPinnedAppsList()
    {
        PinnedActions.Children.Clear();
        Registry ??= new ActionRegistry();
        // 全局固定动作由 PinnedActionIds 单一决定；translate 是工具栏写死的 XAML 按钮，不在此列表管理。
        var byId = Enum.GetValues<ActionCategory>()
            .SelectMany(Registry.GetAllActionsForCategory)
            .ToDictionary(a => a.Id);
        var pinned = SettingsManager.Current.PinnedActionIds
            .Where(id => id != "translate")
            .Select(id => byId.TryGetValue(id, out var action) ? action : null)
            .OfType<IAction>()
            .ToList();
        if (pinned.Count == 0)
        {
            PinnedActions.Children.Add(new TextBlock
            {
                Text = "暂无固定动作", Foreground = _secondaryBrush
            });
            return;
        }

        foreach (var action in pinned)
        {
            var dP = new DockPanel
            {
                Style = (Style)FindResource("SettingRowStyle")
            };
            // JS 脚本动作在工具栏上与内置文本转换共用同一个图标（IconTransform），光看名字
            // 分不出哪个是自定义脚本，所以在名字前加 JS 徽标（与工具栏固定区/子菜单同款，见 JsBadge）。
            if (action is UserScriptAction)
                dP.Children.Add(JsBadge.CreateListItem());
            dP.Children.Add(new TextBlock
            {
                Style = (Style)FindResource("WpfUiTextBlockStyle"),
                Text = action.Name
            });
            DockPanel.SetDock(dP, Dock.Left);
            
            var b = new Wpf.Ui.Controls.Button
            {
                Style = (Style)FindResource("WpfUiButtonStyle"),
                Content = "取消固定",
                Tag = action.Id,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            AutomationProperties.SetName(b, "取消固定 " + action.Name);
            DockPanel.SetDock(b, Dock.Right);
            b.Click += UnpinAction_Click;
            dP.Children.Add(b);

            PinnedActions.Children.Add(dP);
            if (action != pinned.Last())
                PinnedActions.Children.Add(new Border
                {
                    Style = (Style)FindResource("SettingDivider")
                });
        }
    }

    private void UnpinAction_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string id }) return;
        SettingsManager.Current.PinnedActionIds.RemoveAll(x => x == id);
        BuildPinnedAppsList();
        QueueSave();
    }

    private void BuildAppProfilesList()
    {
        // AppProfilesPanel.Children.Clear();
        foreach (var (app, ids) in SettingsManager.Current.AppHiddenActions)
        {
            if (ids.Count == 0) continue;
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
            row.Children.Add(new TextBlock
            {
                Text = $"{app} — {ids.Count} hidden", Foreground = _textBrush,
                VerticalAlignment = VerticalAlignment.Center, Width = 300
            });
            var appKey = app; // capture for the lambda
            var delBtn = new Button
            {
                Content = "X", Width = 22, Height = 22, FontSize = 10, Margin = new Thickness(6, 0, 0, 0),
                Background = Brushes.Transparent, Foreground = _secondaryBrush,
                BorderThickness = new Thickness(0), Cursor = Cursors.Hand
            };
            delBtn.Click += (_, _) =>
            {
                SettingsManager.Current.AppHiddenActions.Remove(appKey);
                BuildAppProfilesList();
                QueueSave();
            };
            row.Children.Add(delBtn);
            // AppProfilesPanel.Children.Add(row);
        }
    }

    private static void ToggleHiddenAction(string? app, string actionId, bool hide)
    {
        if (string.IsNullOrEmpty(app)) return;
        var map = SettingsManager.Current.AppHiddenActions;
        if (!map.TryGetValue(app, out var list))
        {
            list = new List<string>();
            map[app] = list;
        }

        if (hide)
        {
            if (!list.Contains(actionId)) list.Add(actionId);
        }
        else
        {
            list.Remove(actionId);
        }

        if (list.Count == 0) map.Remove(app);
    }

    private void ConfigureAppProfiles_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Window
        {
            Title = "应用配置文件 — 按应用隐藏操作",
            Width = 380, Height = 480,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = this,
            ResizeMode = ResizeMode.CanResize,
            Background = (Brush)FindResource("ApplicationBackgroundBrush")
        };

        // Apps to choose from: existing profiles + currently-running apps with a visible window.
        var appNames = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var k in SettingsManager.Current.AppHiddenActions.Keys) appNames.Add(k);
        try
        {
            var own = Environment.ProcessId;
            foreach (var p in Process.GetProcesses())
                try
                {
                    if (p.Id == own || p.MainWindowHandle == IntPtr.Zero) continue;
                    if (!string.IsNullOrEmpty(p.ProcessName) &&
                        !p.ProcessName.Equals("SnapActions", StringComparison.OrdinalIgnoreCase))
                        appNames.Add(p.ProcessName);
                }
                catch
                {
                }
                finally
                {
                    p.Dispose();
                }
        }
        catch (Exception ex)
        {
            Log.Warn($"Process enumeration failed: {ex.Message}");
        }

        var appCombo = new ComboBox { Margin = new Thickness(8) };
        foreach (var n in appNames) appCombo.Items.Add(n);

        var descriptors = new ActionRegistry().AllActionDescriptors()
            .OrderBy(d => d.Category).ThenBy(d => d.Name).ToList();

        var listPanel = new StackPanel { Margin = new Thickness(8) };

        void RefreshChecks()
        {
            listPanel.Children.Clear();
            var app = appCombo.SelectedItem as string;
            var hidden = app != null && SettingsManager.Current.AppHiddenActions.TryGetValue(app, out var h)
                ? new HashSet<string>(h, StringComparer.Ordinal)
                : new HashSet<string>();
            foreach (var d in descriptors)
            {
                var c = new CheckBox
                {
                    Content = $"{d.Name}  ({d.Category})",
                    IsChecked = hidden.Contains(d.Id),
                    Foreground = _textBrush, Margin = new Thickness(0, 2, 0, 2)
                };
                var actionId = d.Id; // capture
                c.Checked += (_, _) => ToggleHiddenAction(appCombo.SelectedItem as string, actionId, true);
                c.Unchecked += (_, _) => ToggleHiddenAction(appCombo.SelectedItem as string, actionId, false);
                listPanel.Children.Add(c);
            }
        }

        appCombo.SelectionChanged += (_, _) => RefreshChecks();
        if (appCombo.Items.Count > 0) appCombo.SelectedIndex = 0;

        var closeBtn = new Button
        {
            Content = "关闭", Padding = new Thickness(16, 4, 16, 4), Margin = new Thickness(8),
            HorizontalAlignment = HorizontalAlignment.Right,
            Background = (Brush)FindResource("SystemFillColorAttentionBrush"),
            Foreground = (Brush)FindResource("ApplicationBackgroundBrush"),
            BorderThickness = new Thickness(0)
        };
        closeBtn.Click += (_, _) => dlg.Close();

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var scroll = new ScrollViewer
        {
            Content = listPanel,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        var topPanel = new StackPanel();
        topPanel.Children.Add(appCombo);
        var presetRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 0, 8, 8) };
        var presets = new ComboBox { Width = 150 };
        var presetKeys = new (string Name, string Key)[]
            { ("阅读", "Reading"), ("写作", "Writing"), ("开发", "Development") };
        foreach (var (name, key) in presetKeys)
            presets.Items.Add(new ComboBoxItem { Content = name, Tag = key });
        presets.SelectedIndex = 0;
        AutomationProperties.SetName(presets, "App profile preset");
        var applyPreset = new Button
            { Content = "应用预设", Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(8, 3, 8, 3) };
        applyPreset.Click += (_, _) =>
        {
            if (appCombo.SelectedItem is not string app || presets.SelectedItem is not ComboBoxItem item) return;
            foreach (var id in AppProfilePresets.HiddenActions(item.Tag?.ToString() ?? "", new ActionRegistry()))
                ToggleHiddenAction(app, id, true);
            RefreshChecks();
        };
        presetRow.Children.Add(presets);
        presetRow.Children.Add(applyPreset);
        topPanel.Children.Add(presetRow);
        Grid.SetRow(topPanel, 0);
        Grid.SetRow(scroll, 1);
        Grid.SetRow(closeBtn, 2);
        grid.Children.Add(topPanel);
        grid.Children.Add(scroll);
        grid.Children.Add(closeBtn);
        dlg.Content = grid;
        dlg.ShowDialog();

        BuildAppProfilesList();
        QueueSave();
    }

    private void Setting_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var s = SettingsManager.Current;
        s.Enabled = EnabledCheck.IsChecked == true;
        s.SelectCopy = SelectCopyCheck.IsChecked == true;
        s.HoverOpen = HoverOpenCheck.IsChecked == true;
        // s.ReplaceSelectionOnTransform = ReplaceSelectionCheck.IsChecked == true;
        s.RestoreClipboardAfterAction = RestoreClipboardCheck.IsChecked == true;
        s.AllowOnlineLookups = OnlineLookupsCheck.IsChecked == true;
        // s.CaptureOnMouseSelection = CaptureOnMouseSelectionCheck.IsChecked == true;
        s.CaptureOnCtrlC = CaptureOnCtrlCCheck.IsChecked == true;
        s.UseSyntheticKeys = UseSyntheticKeys.IsChecked == true;
        s.ShowPasteActions = ShowPasteCheck.IsChecked == true;
        s.ShowTranslateActions = ShowTranslateCheck.IsChecked == true;
        s.ShowTransformActions = ShowTransformCheck.IsChecked == true;
        s.ShowEncodeActions = ShowEncodeCheck.IsChecked == true;
        s.ShowSearchActions = ShowSearchCheck.IsChecked == true;
        QueueSave();
    }

    private void AutoStart_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;

        var enable = AutoStartCheck.IsChecked == true;

        if (enable)
        {
            var r = MessageBox.Show(
                "勾选后将在「任务计划程序」创建任务（名称：SnapActions），\n" +
                "为了静默启动，该任务会以最高权限运行\n\n" +
                "确定开启吗？",
                "开机自启",
                MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (r != MessageBoxResult.Yes)
            {
                _loading = true;
                AutoStartCheck.IsChecked = false;
                _loading = false;
                return;
            }
        }

        var ok = SettingsManager.SetAutoStart(enable);

        _loading = true;
        AutoStartCheck.IsChecked = SettingsManager.Current.AutoStart;
        _loading = false;

        SaveStatusText.Text = ok ? "已保存"
            : enable ? "未能注册开机自启（任务计划程序操作失败）。"
            : "未能取消开机自启。";
    }

    private void ShowDelay_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (ShowDelayCombo.SelectedItem is ComboBoxItem item &&
            int.TryParse(item.Tag?.ToString(), out var ms))
            SettingsManager.Current.ToolbarShowDelay = ms;
        QueueSave();
    }

    private void MultiClick_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (MultiClickCombo.SelectedItem is ComboBoxItem item &&
            int.TryParse(item.Tag?.ToString(), out var ms))
            SettingsManager.Current.MultiClickDelay = ms;
        QueueSave();
    }

    /*private void LongPress_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (LongPressCombo.SelectedItem is ComboBoxItem item &&
            int.TryParse(item.Tag?.ToString(), out var ms))
            SettingsManager.Current.LongPressDuration = ms;
        QueueSave();
    }*/

    // Match the tag values declared in SettingsWindow.xaml (also the lowercase form
    // PasteModeTriggerJsonConverter writes to settings.json — keeps both spellings in sync).
    private static string PasteModeTagFor(PasteModeTrigger trigger)
    {
        return trigger switch
        {
            PasteModeTrigger.DoubleClick => "doubleclick",
            PasteModeTrigger.Off => "off",
            _ => "longpress"
        };
    }

    /*private void PasteMode_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (PasteModeCombo.SelectedItem is ComboBoxItem item)
            SettingsManager.Current.PasteModeTrigger = item.Tag?.ToString() switch
            {
                "doubleclick" => PasteModeTrigger.DoubleClick,
                "off" => PasteModeTrigger.Off,
                _ => PasteModeTrigger.LongPress
            };
        QueueSave();
    }*/

    private void MaxInline_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (MaxInlineCombo.SelectedItem is ComboBoxItem item &&
            int.TryParse(item.Tag?.ToString(), out var n))
            SettingsManager.Current.MaxInlineContextActions = n;
        QueueSave();
    }

    private void DismissTime_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (DismissTimeCombo.SelectedItem is ComboBoxItem item &&
            int.TryParse(item.Tag?.ToString(), out var ms))
            SettingsManager.Current.ToolbarDismissTimeout = ms;
        QueueSave();
    }

    // private void Language_Changed(object sender, SelectionChangedEventArgs e)
    // {
    //     if (_loading) return;
    //     if (LanguageCombo.SelectedItem is ComboBoxItem item)
    //         SettingsManager.Current.SearchLanguage = item.Tag?.ToString() ?? "";
    //     QueueSave();
    // }

    /*private void Currency_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (CurrencyCombo.SelectedItem is ComboBoxItem item)
            SettingsManager.Current.TargetCurrency = item.Tag?.ToString() ?? "USD";
        QueueSave();
    }*/

    private void EngineToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading || sender is not CheckBox { Tag: string id }) return;
        var engine = SettingsManager.Current.SearchEngines.FirstOrDefault(en => en.Id == id);
        if (engine != null) engine.Enabled = ((CheckBox)sender).IsChecked == true;
        QueueSave();
    }

    private void LangToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading || sender is not CheckBox { Tag: string id }) return;
        var engine = SettingsManager.Current.SearchEngines.FirstOrDefault(en => en.Id == id);
        if (engine != null) engine.UseLanguageFilter = ((CheckBox)sender).IsChecked == true;
        QueueSave();
    }

    private void DeleteEngine_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string id }) return;
        SettingsManager.Current.SearchEngines.RemoveAll(en => en.Id == id);
        BuildSearchEnginesList();
        QueueSave();
    }

    private void AddCustomEngine_Click(object sender, RoutedEventArgs e)
    {
        var name = CustomNameBox.Text.Trim();
        var url = CustomUrlBox.Text.Trim();
        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(url)) return;

        // Validate URL — only http/https allowed, must be a parseable absolute URL.
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show("自定义搜索引擎 URL 必须以 http:// 或 https:// 开头",
                "URL 无效", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // {0} placeholder is the URL-encoded query — accept it as a placeholder during validation.
        var probeUrl = url.Replace("{0}", "test").Replace("{1}", "en");
        if (!Uri.TryCreate(probeUrl, UriKind.Absolute, out _))
        {
            MessageBox.Show("自定义搜索引擎 URL 无效。",
                "URL 无效", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!url.Contains("{0}"))
            url += (url.Contains('?') ? "&" : "?") + "q={0}";

        var baseId = "custom_" + name.ToLowerInvariant().Replace(' ', '_');
        var id = baseId;
        if (SettingsManager.Current.SearchEngines.Any(en => en.Id == id))
            id = baseId + "_" + Guid.NewGuid().ToString("N")[..8];

        SettingsManager.Current.SearchEngines.Add(new SearchEngine
        {
            Id = id, Name = name, UrlTemplate = url,
            Enabled = true, IsBuiltIn = false
        });

        CustomNameBox.Text = "";
        CustomUrlBox.Text = "";
        BuildSearchEnginesList();
        QueueSave();
    }

    private void ExcludedApps_Changed(object sender, TextChangedEventArgs e)
    {
        if (_loading) return;
        // Don't mutate Current.ExcludedApps per-keystroke; defer to the debounce tick so we only
        // build one new list per save cycle. FlushPendingTextEdits does the actual assignment.
        QueueSave();
    }

    /// <summary>
    ///     Push any text-box-backed settings (ExcludedApps, 百度凭据) into SettingsManager.Current just
    ///     before a Save. Called from both the debounce tick and the window-close handler so a
    ///     close-before-debounce-fires path still persists the user's edits.
    /// </summary>
    private void FlushPendingTextEdits()
    {
        SettingsManager.Current.ExcludedApps = ExcludedAppsBox.Text
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(a => a.Trim()).Where(a => a.Length > 0).ToList();
        // 百度凭据没有保存按钮，两个输入框就是唯一真相；值没变时 ReconcileBaidu 原样返回旧 blob。
        SettingsManager.Current.BaiduCredentialsBlob = CredentialCrypto.ReconcileBaidu(
            SettingsManager.Current.BaiduCredentialsBlob, BaiduAppIdBox.Text, BaiduSecretBox.Password);
    }

    // Settings auto-save on every change; this button is now an explicit "save now" if the user
    // wants to force-flush before any debounce timer fires. Synchronous so the user can be sure
    // it's persisted by the time the click handler returns.
    private void Save_Click(object sender, RoutedEventArgs e)
    {
        _saveDebounce.Stop();
        FlushPendingTextEdits();
        SaveWithStatus();
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }

    // 左侧导航选中：ListBox 天然单选，无需互斥逻辑，按选中项的 Tag 切换右侧页面可见性。
    // 注意 _loading 保护：XAML 中 ListBoxItem IsSelected="True" 在 InitializeComponent 解析
    // 中途即触发本事件，此时右侧页面字段尚未赋值，访问会抛空引用，故加载期内直接跳过。
    private void SettingsNav_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        var item = SettingsNavList.SelectedItem as FrameworkElement;
        var key = item?.Tag as string;
        bool general = key == "general",
            actions = key == "actions",
            language = key == "language",
            custom = key == "custom";
        GeneralPage.Visibility = general ? Visibility.Visible : Visibility.Collapsed;
        ActionsPage.Visibility = actions ? Visibility.Visible : Visibility.Collapsed;
        LanguagePage.Visibility = language ? Visibility.Visible : Visibility.Collapsed;
        CustomPage.Visibility = custom ? Visibility.Visible : Visibility.Collapsed;
        AppsPage.Visibility = !general && !actions && !language && !custom
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void AddRunningApp_Click(object sender, RoutedEventArgs e)
    {
        // List currently-running processes that have a visible main window — typing process
        // names into the textbox by hand is error-prone (case, spelling, .exe vs not).
        var picker = new Window
        {
            Title = "选择要排除的应用",
            Width = 320, Height = 420,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            ResizeMode = ResizeMode.CanResize,
            Background = (Brush)FindResource("ApplicationBackgroundBrush")
        };
        var list = new ListBox
        {
            Background = (Brush)FindResource("CardBackgroundFillColorSecondaryBrush"),
            Foreground = _textBrush,
            BorderBrush = (Brush)FindResource("DividerStrokeColorDefaultBrush"),
            FontFamily = new FontFamily("Consolas"),
            Margin = new Thickness(8)
        };

        try
        {
            var ownPid = Environment.ProcessId;
            var existing = new HashSet<string>(SettingsManager.Current.ExcludedApps,
                StringComparer.OrdinalIgnoreCase);
            var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in Process.GetProcesses())
                try
                {
                    if (p.Id == ownPid) continue;
                    if (p.MainWindowHandle == IntPtr.Zero) continue;
                    var name = p.ProcessName; // already without .exe
                    if (string.IsNullOrEmpty(name)) continue;
                    if (existing.Contains(name)) continue;
                    if (name.Equals("SnapActions", StringComparison.OrdinalIgnoreCase)) continue;
                    names.Add(name);
                }
                catch
                {
                    /* access denied on system processes — skip */
                }
                finally
                {
                    p.Dispose();
                }

            foreach (var n in names) list.Items.Add(n);
        }
        catch (Exception ex)
        {
            Log.Warn($"Process enumeration failed: {ex.Message}");
        }

        list.MouseDoubleClick += (_, _) =>
        {
            if (list.SelectedItem is string s) AddExcludedAppName(s);
            picker.Close();
        };

        var addBtn = new Button
        {
            Content = "添加", Padding = new Thickness(16, 4, 16, 4),
            Background = (Brush)FindResource("SystemFillColorAttentionBrush"),
            Foreground = (Brush)FindResource("ApplicationBackgroundBrush"),
            BorderThickness = new Thickness(0),
            Margin = new Thickness(0, 0, 8, 0)
        };
        addBtn.Click += (_, _) =>
        {
            if (list.SelectedItem is string s) AddExcludedAppName(s);
            picker.Close();
        };
        var cancelBtn = new Button
        {
            Content = "取消", Padding = new Thickness(16, 4, 16, 4),
            Background = (Brush)FindResource("CardBackgroundFillColorSecondaryBrush"),
            Foreground = _textBrush,
            BorderBrush = (Brush)FindResource("DividerStrokeColorDefaultBrush")
        };
        cancelBtn.Click += (_, _) => picker.Close();

        var buttonRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(8, 0, 8, 8)
        };
        buttonRow.Children.Add(addBtn);
        buttonRow.Children.Add(cancelBtn);

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(list, 0);
        Grid.SetRow(buttonRow, 1);
        grid.Children.Add(list);
        grid.Children.Add(buttonRow);
        picker.Content = grid;
        picker.ShowDialog();
    }

    private void AddExcludedAppName(string name)
    {
        var current = ExcludedAppsBox.Text;
        // Already-newline-terminated content (either \n or \r\n) doesn't need a leading separator.
        // The TextChanged handler splits on '\n', so CRLF endings would otherwise leave a trailing \r
        // attached to the previous entry.
        var endsWithNewline = current.EndsWith('\n') || current.EndsWith("\r\n", StringComparison.Ordinal);
        var sep = string.IsNullOrEmpty(current) || endsWithNewline ? "" : "\n";
        ExcludedAppsBox.Text = current + sep + name;
        // The TextChanged handler will pick this up and queue a save.
    }
}