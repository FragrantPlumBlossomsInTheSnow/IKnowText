using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SnapActions.Actions;
using SnapActions.Config;
using SnapActions.Core;
using SnapActions.Detection;
using SnapActions.Services;
using SnapActions.UI;
using TextBox = System.Windows.Controls.TextBox;
using DragEventArgs = System.Windows.DragEventArgs;

namespace SnapActions.Diagnostics;

/// <summary>Runs only with --self-test and an isolated data directory; never installs hooks or changes the clipboard.</summary>
internal static class PackageSelfTest
{
    internal static async Task<int> RunAsync()
    {
        if (!RuntimePaths.IsIsolated) return 2;
        var checks = new List<string>();
        string? failure = null;
        Directory.CreateDirectory(RuntimePaths.DataDirectory);
        try
        {
            SettingsManager.Load();
            CheckSettingsSaveFailures();
            checks.Add("Settings write/replace failures preserve saved pins, report errors, and recover on retry/reload");

            foreach (var theme in new[] { "dark", "light" })
            {
                SettingsManager.Current.Theme = theme; ThemeManager.Apply();
                var settings = new SettingsWindow();
                settings.LoadSettings();
                // 设置窗口已由 TabControl 改版为 ListBox 侧边导航：右侧是五个 ScrollViewer 页面，
                // 通过各自 Visibility 切换（SettingsNav_SelectionChanged）。自测按下/暗两主题逐页渲染。
                var pages = new[] { "GeneralPage", "ActionsPage", "LanguagePage", "CustomPage", "AppsPage" };
                foreach (var page in pages)
                {
                    foreach (var other in pages)
                        ((FrameworkElement)settings.FindName(other)).Visibility =
                            other == page ? Visibility.Visible : Visibility.Collapsed;
                    Render(settings, $"settings-{theme}-{page}", 692, 644);
                }
                var searchBox = settings.FindName("SettingsSearchBox") as TextBox;
                if (searchBox != null)
                {
                    searchBox.Text = "翻译";
                    Require(((ScrollViewer)settings.FindName("LanguagePage")).Visibility == Visibility.Visible, "Settings search lost translation section");
                    searchBox.Text = "";
                }
                foreach (var other in pages)
                    ((FrameworkElement)settings.FindName(other)).Visibility =
                        other == "GeneralPage" ? Visibility.Visible : Visibility.Collapsed;
                Render(settings, $"settings-{theme}-small", 572, 404);
                settings.Close();
                checks.Add(theme + " Settings sections, search, and small-window render");
            }

            // 托盘菜单：资源结构 + 深浅两套外观留档。
            // 菜单是 H.NotifyIcon 的 WPF ContextMenu（外壳圆角/阴影自绘在 UI/TrayMenu.xaml，条目用 WPF-UI 的
            // ui:MenuItem），所以它跟随应用主题 —— 旧的 WinForms ContextMenuStrip 做不到这点。
            // 注：5 个动作组开关（「启用动作」）取代了早期的「启用」勾选项与状态行，直接列在菜单顶层。
            var trayMenu = (System.Windows.Controls.ContextMenu)Application.Current.FindResource("TrayContextMenu");
            var trayResource = (H.NotifyIcon.TaskbarIcon)Application.Current.FindResource("TrayIcon");
            Require(ReferenceEquals(trayResource.ContextMenu, trayMenu), "Tray icon is not bound to the tray context menu");
            // 顶层：5 个动作组开关 + 开机自启 / 更多设置 / 退出（点击回写与字形同步见下面的断言）。
            foreach (string tag in new[]
            {
                "EnablePasteItem", "EnableTranslateItem", "EnableTransformItem", "EnableEncodeItem", "EnableSearchItem",
                "AutoStartItem", "SettingsItem", "ExitItem"
            })
                Require(trayMenu.Items.OfType<System.Windows.Controls.MenuItem>().Any(i => (string?)i.Tag == tag),
                    "Tray menu is missing item: " + tag);
            var enableSearchItem = trayMenu.Items.OfType<System.Windows.Controls.MenuItem>()
                .First(i => (string?)i.Tag == "EnableSearchItem");

            var savedTrayTheme = SettingsManager.Current.Theme;
            var trayManager = new TrayMenu();
            var trayHost = new Window
            {
                Width = 8, Height = 8, WindowStyle = WindowStyle.None, ShowInTaskbar = false,
                AllowsTransparency = true, Opacity = 0.01, ShowActivated = false,
                Content = new System.Windows.Controls.Grid()
            };
            try
            {
                trayManager.Initialize(); // 真建一次托盘图标：覆盖 H.NotifyIcon 的创建路径

                // 「启用动作」开关：打开菜单同步勾选字形 → 点击回写设置 → 字形跟随。
                bool savedSearchActions = SettingsManager.Current.ShowSearchActions;
                try
                {
                    trayMenu.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.ContextMenu.OpenedEvent));
                    Require(TrayIconState.Instance.SearchGlyph == (savedSearchActions ? IconGlyphs.ToggleOn : IconGlyphs.ToggleOff),
                        "Enable-actions glyph did not sync from settings");
                    enableSearchItem.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.MenuItem.ClickEvent));
                    Require(SettingsManager.Current.ShowSearchActions == !savedSearchActions,
                        "Enable-actions toggle did not write settings");
                    Require(TrayIconState.Instance.SearchGlyph == (!savedSearchActions ? IconGlyphs.ToggleOn : IconGlyphs.ToggleOff),
                        "Enable-actions glyph did not follow the toggle");
                }
                finally { SettingsManager.Current.ShowSearchActions = savedSearchActions; }

                trayHost.Show();
                foreach (string theme in new[] { "dark", "light" })
                {
                    SettingsManager.Current.Theme = theme; ThemeManager.Apply();
                    trayMenu.PlacementTarget = trayHost;
                    trayMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
                    trayMenu.IsOpen = true;
                    trayHost.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Loaded);
                    trayMenu.UpdateLayout();
                    RenderElement(trayMenu, "tray-menu-" + theme, 260, 260);
                    trayMenu.IsOpen = false;
                }
            }
            finally
            {
                trayMenu.IsOpen = false;
                trayHost.Close();
                trayManager.Dispose();
                SettingsManager.Current.Theme = savedTrayTheme; ThemeManager.Apply();
            }
            checks.Add("Tray menu (H.NotifyIcon + WPF-UI MenuItems): structure, action-group toggles and theme-following render");

            // 设置-翻译-百度翻译：已无「保存百度凭据」按钮，输入框变化后必须由防抖自动落盘。
            var baiduSettings = new SettingsWindow();
            baiduSettings.LoadSettings();
            // 自测直接调 LoadSettings（不走 ContentRendered 的收尾），手动解除加载期保护，
            // 否则 QueueSave 会被 _loading 挡掉。
            SetField(baiduSettings, "_loading", false);
            // 展开态渲染：确认「保存百度凭据」按钮已移除、自动保存提示在位。直接渲染 Expander 的
            // 内容而不是整窗 —— 窗口未 Show，WPF-UI 的展开动画不会跑，模板里的内容仍是折叠态。
            RenderElement((FrameworkElement)baiduSettings.BaiduExpander.Content, "settings-light-baidu", 460, 220);
            baiduSettings.BaiduAppIdBox.Text = "selftest-appid";
            baiduSettings.BaiduSecretBox.Password = "selftest-secret";
            bool autoSaved = false;
            for (int i = 0; i < 25 && !autoSaved; i++)
            {
                await Task.Delay(100); // 给 400ms 防抖 DispatcherTimer 触发的机会
                var (savedAppId, savedSecret) = CredentialCrypto.DecryptBaidu(SettingsManager.Current.BaiduCredentialsBlob);
                autoSaved = savedAppId == "selftest-appid" && savedSecret == "selftest-secret";
            }
            Require(autoSaved, "Baidu credentials were not auto-saved after typing");
            baiduSettings.Close();
            checks.Add("Baidu credentials auto-save without a save button");

            // 设置-动作-固定在工具栏的动作 + 工具栏固定区 + Transform 子窗口：自定义 JS 脚本动作
            // 与内置转换动作共用 IconTransform 字形，三处都要换成 JS 徽标；配了「上下文触发」正则的
            // 脚本在命中时还要内联进工具栏上下文区（ContextSeparator 后），固定时不能重复渲染。
            var savedPins = SettingsManager.Current.PinnedActionIds.ToList();
            var savedCustomActions = SettingsManager.Current.EnableCustomActions;
            var savedMaxInline = SettingsManager.Current.MaxInlineContextActions;
            var pinnedSettings = new SettingsWindow();
            var pinnedToolbar = new ToolbarWindow { Registry = new ActionRegistry() };
            try
            {
                SettingsManager.Current.EnableCustomActions = true;
                // 默认上限 3 可能把触发动作挤进溢出菜单，自测固定成 8 让内联区必定容纳。
                SettingsManager.Current.MaxInlineContextActions = 8;
                SettingsManager.Current.UserActions.Insert(0, new UserAction
                {
                    Id = "selftest_js", Name = "SELFTEST-JS",
                    Code = "function JSAction(t) { return t; }", ContextRegex = "^SELFTEST",
                });
                SettingsManager.Current.PinnedActionIds = ["case_upper", "user_selftest_js"];
                pinnedSettings.LoadSettings();
                SetField(pinnedSettings, "_loading", false);
                var rows = pinnedSettings.PinnedActions.Children.OfType<DockPanel>().ToList();
                Require(rows.Count == 2, "Pinned actions list did not show both pins");
                var jsRow = rows.Single(r => r.Children.OfType<TextBlock>().Any(t => t.Text == "SELFTEST-JS"));
                Require(HasJsBadge(jsRow), "Pinned JS action is not marked with a JS badge in Settings");
                Require(!rows.Where(r => !ReferenceEquals(r, jsRow)).Any(HasJsBadge),
                    "A non-script pinned action was marked as JS in Settings");
                RenderElement(pinnedSettings.PinnedActions, "settings-light-pinned-actions", 460, 120);

                // 设置里的「取消固定」仍要能把动作从 PinnedActionIds 里摘掉。
                var unpin = jsRow.Children.OfType<Button>().Single(b => (string?)b.Content == "取消固定");
                unpin.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Require(!SettingsManager.Current.PinnedActionIds.Contains("user_selftest_js"),
                    "Unpinning from settings left the action pinned");
                Require(pinnedSettings.PinnedActions.Children.OfType<DockPanel>().Count() == 1,
                    "Pinned actions list did not refresh after unpinning");

                // 工具栏：同一动作固定后在固定区带 JS 徽标；命中正则也不能在上下文区再渲染一遍。
                SettingsManager.Current.PinnedActionIds = ["case_upper", "user_selftest_js"];
                SetField(pinnedToolbar, "_selectedText", "SELFTEST context text");
                SetField(pinnedToolbar, "_appName", "notepad");
                ((Border)pinnedToolbar.FindName("MainBorder")).MaxWidth = 1200;
                pinnedToolbar.RefreshActions();
                var toolbarPins = (StackPanel)pinnedToolbar.FindName("PinnedActionsPanel");
                var toolbarContext = (StackPanel)pinnedToolbar.FindName("ContextActionsPanel");
                var jsPin = toolbarPins.Children.OfType<Button>().Single(b => ((IAction)b.Tag).Id == "user_selftest_js");
                Require(HasJsBadge(jsPin), "Toolbar pinned JS action is not marked with a JS badge");
                Require(!toolbarPins.Children.OfType<Button>().Where(b => ((IAction)b.Tag).Id != "user_selftest_js").Any(HasJsBadge),
                    "A non-script pinned action was marked as JS on the toolbar");
                Require(!toolbarContext.Children.OfType<Button>().Any(b => ((IAction)b.Tag).Id == "user_selftest_js"),
                    "A pinned JS action was rendered twice (pinned area + inline context)");
                RenderElement(toolbarPins, "toolbar-js-pin", 460, 60);

                // 取消固定后命中正则：内联出现在上下文区，且带 JS 徽标。
                SettingsManager.Current.PinnedActionIds.RemoveAll(id => id == "user_selftest_js");
                pinnedToolbar.RefreshActions();
                var inlineJs = toolbarContext.Children.OfType<Button>().Single(b => ((IAction)b.Tag).Id == "user_selftest_js");
                Require(HasJsBadge(inlineJs), "Inline context JS action is not marked with a JS badge");
                RenderElement(toolbarContext, "toolbar-js-context-inline", 460, 60);
                Render(pinnedToolbar, "toolbar-js-context-row", 1200, 70); // 整条工具栏：上下文区（ContextSeparator 后）与固定区

                // 选区不命中正则：上下文区不推送该动作（它仍是转换动作）。
                SetField(pinnedToolbar, "_selectedText", "hello");
                pinnedToolbar.RefreshActions();
                Require(!toolbarContext.Children.OfType<Button>().Any(b => ((IAction)b.Tag).Id == "user_selftest_js"),
                    "Context trigger pushed the JS action for a non-matching selection");

                // Transform 子窗口：同一动作出现在转换子菜单里时同样带 JS 徽标。
                typeof(ToolbarWindow).GetMethod("ShowSubMenu", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                    .Invoke(pinnedToolbar, ["Transform", ActionCategory.Transform]);
                var subMenu = (System.Windows.Controls.Panel)pinnedToolbar.FindName("SubMenuPanel");
                var jsItem = subMenu.Children.OfType<Button>().Single(b => ((IAction)b.Tag).Id == "user_selftest_js");
                Require(HasJsBadge(jsItem), "Transform submenu does not mark the JS script action");
                RenderElement(subMenu, "toolbar-transform-submenu-js", 420, 320);
            }
            finally
            {
                SettingsManager.Current.UserActions.RemoveAll(u => u.Id == "selftest_js");
                SettingsManager.Current.PinnedActionIds = savedPins;
                SettingsManager.Current.EnableCustomActions = savedCustomActions;
                SettingsManager.Current.MaxInlineContextActions = savedMaxInline;
                ((System.Windows.Controls.Primitives.Popup)pinnedToolbar.FindName("SubMenuPopup")).IsOpen = false;
                pinnedToolbar.Close();
                pinnedSettings.Close();
            }
            checks.Add("JS script actions are marked in Settings pins, toolbar pins and the Transform submenu");
            checks.Add("Context-trigger regex pushes the JS script action inline into the toolbar context row");

            // JS 脚本编辑器：沙箱注入的 console.* 必须落到「试跑日志」框里（正常执行路径静默，只此可见）；
            // 「上下文触发」正则要当场给出有效性/是否命中试跑文本的结论。
            var scriptEditor = new UserScriptEditor(null);
            ((TextBox)GetField(scriptEditor, "CodeBox")).Text =
                "function JSAction(text) { console.log('sel:', text, { n: 1 }); return text.toUpperCase(); }";
            ((TextBox)GetField(scriptEditor, "SampleBox")).Text = "hey";
            var scriptLogs = (TextBox)GetField(scriptEditor, "LogsBox");
            Require(scriptLogs.Text.Contains("sel: hey {\"n\":1}"), "Script editor did not capture console output");
            var triggerBox = (TextBox)GetField(scriptEditor, "TriggerBox");
            var triggerHint = (TextBlock)GetField(scriptEditor, "TriggerHintText");
            triggerBox.Text = "^h"; // 命中试跑文本 "hey"
            Require(triggerHint.Text.Contains("命中"), "Context trigger hint did not report a match");
            triggerBox.Text = "([";
            Require(triggerHint.Text.Contains("无效"), "Context trigger hint did not flag an invalid regex");
            triggerBox.Text = "^h"; // 截图停在「有效且命中」状态
            Render(scriptEditor, "script-editor", 620, 860);
            // 「允许此脚本访问网络」开关：勾选态 → 提示文案切换。临时打开在线查询开关，
            // 避免勾选时弹出同意框卡住自检；自检本身不发任何网络请求。
            var allowNetworkCheck = (System.Windows.Controls.CheckBox)GetField(scriptEditor, "AllowNetworkCheck");
            var sandboxHint = (TextBlock)GetField(scriptEditor, "SandboxHintText");
            var networkHint = (TextBlock)GetField(scriptEditor, "NetworkHintText");
            var savedOnlineLookups = SettingsManager.Current.AllowOnlineLookups;
            try
            {
                SettingsManager.Current.AllowOnlineLookups = true;
                allowNetworkCheck.IsChecked = true;
                Require(networkHint.Visibility == Visibility.Visible,
                    "Network hint stayed hidden when network access is on");
                Require(sandboxHint.Text.Contains("允许网络"), "Sandbox hint did not switch to the network wording");
                allowNetworkCheck.IsChecked = false;
                Require(networkHint.Visibility == Visibility.Collapsed,
                    "Network hint stayed visible when network access is off");
                Require(sandboxHint.Text.Contains("无法访问文件/网络"),
                    "Sandbox hint did not switch back after turning network access off");
            }
            finally { SettingsManager.Current.AllowOnlineLookups = savedOnlineLookups; }
            scriptEditor.Close();
            checks.Add("Script editor console capture and context-trigger feedback");

            // 自定义翻译引擎编辑器（与 JS 脚本动作编辑器各自独立的 xaml+cs）：必定是联网沙箱，且沙箱
            // 不注入 Translation（否则引擎会递归调用自己）。试跑一律防抖，这里直接调 PreviewAsync 断言。
            var engineEditor = new TranslationEngineEditor(null);
            // 冻结防抖试跑：构造函数与赋值都会排一次 600ms 后的自动试跑，留着会盖掉断言与截图。
            ((System.Windows.Threading.DispatcherTimer)GetField(engineEditor, "_previewDebounce")).Stop();
            SetField(engineEditor, "_loading", true);
            var enginePreviewBox = (TextBox)GetField(engineEditor, "PreviewBox");
            var engineLookups = SettingsManager.Current.AllowOnlineLookups;
            try
            {
                SettingsManager.Current.AllowOnlineLookups = true;
                ((TextBox)GetField(engineEditor, "CodeBox")).Text =
                    "async function JSAction(text) { return SNAP_SOURCE_LANGUAGE + '>' + SNAP_TARGET_LANGUAGE + ':' + text.toUpperCase() + ':' + typeof Translation; }";
                ((TextBox)GetField(engineEditor, "SampleBox")).Text = "hey";
                var previewTask = (Task)typeof(TranslationEngineEditor)
                    .GetMethod("PreviewAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                    .Invoke(engineEditor, null)!;
                await previewTask;
                Require(enginePreviewBox.Text.Contains("HEY"), "Translation engine preview did not return the script result");
                Require(enginePreviewBox.Text.EndsWith(":undefined", StringComparison.Ordinal),
                    "Translation engine preview sandbox exposed Translation (would recurse into the engine)");
                Require(engineEditor.Title.Contains("翻译引擎"), "Translation engine editor title is wrong");
                Render(engineEditor, "translation-engine-editor", 620, 780);
            }
            finally { SettingsManager.Current.AllowOnlineLookups = engineLookups; }
            engineEditor.Close();
            checks.Add("Translation engine editor (standalone) previews in a network sandbox without Translation");

            var registry = new ActionRegistry();

            var toolbar = new ToolbarWindow { Registry = registry };
            SettingsManager.Current.PinnedActionIds = registry.GetAllActionsForCategory(ActionCategory.Transform).Select(a => a.Id).ToList();
            // Build the production controls without showing a native toolbar or changing focus.
            SetField(toolbar, "_actionGroups", registry.GetActions("text", TextAnalysis.PlainText));
            foreach (double width in new[] { 600d, 400d })
            {
                ((Border)toolbar.FindName("MainBorder")).MaxWidth = width;
                toolbar.RebuildInlineActions();
                var main = (FrameworkElement)toolbar.FindName("MainToolbar");
                main.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                Require(main.DesiredSize.Width <= width, $"Toolbar controls overflow at {width} DIPs: {main.DesiredSize.Width}");
                var more = (Button)toolbar.FindName("MoreButton");
                Require(more.Visibility == Visibility.Visible && more.Tag is List<IAction> { Count: > 0 }, "Pinned actions did not overflow into More");
                Require(System.Windows.Automation.AutomationProperties.GetName(more).Length > 0, "More has no accessible name");
                Render(toolbar, $"toolbar-{width}", width, 60);
            }
            toolbar.Close(); checks.Add("Toolbar width budget and accessible More at 400/600 DIPs");
            CheckToolbarCustomization(registry);
            checks.Add("Live settings refresh for browser/native providers, persistent read-only pins, drag/drop insertion, hide/show and unpin controls");
            await CheckToolbarPreviewAsync(registry);
            checks.Add("Hover preview reopen/leave lifecycle, constrained text layout, light/dark renders, and feedback after customization");

            var popup = new ResultPopup();
            SetField(popup, "_fetch", (Func<CancellationToken, Task<LookupResult>>)(_ => throw new TaskCanceledException()));
            await popup.RunFetchAsync();
            Require(((TextBlock)popup.FindName("LoadingText")).Visibility == Visibility.Collapsed, "Timeout left Loading visible");
            Require(((Button)popup.FindName("RetryButton")).Visibility == Visibility.Visible, "Timeout has no retry");
            Require(((Button)popup.FindName("CopyButton")).Visibility == Visibility.Collapsed, "Timeout can be copied as a result");
            Render(popup, "lookup-timeout", 430, 250);
            var pending = new TaskCompletionSource<LookupResult>();
            SetField(popup, "_fetch", (Func<CancellationToken, Task<LookupResult>>)(_ => pending.Task));
            var oldRequest = popup.RunFetchAsync();
            SetField(popup, "_fetch", (Func<CancellationToken, Task<LookupResult>>)(_ => Task.FromResult(LookupResult.Success("fresh"))));
            ((Button)popup.FindName("RetryButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            pending.SetResult(LookupResult.Success("stale")); await oldRequest;
            Require(((TextBlock)popup.FindName("ResultText")).Text == "fresh", "A stale request overwrote retry");
            var closing = new TaskCompletionSource<LookupResult>();
            CancellationToken closingToken = default;
            SetField(popup, "_fetch", (Func<CancellationToken, Task<LookupResult>>)(ct => { closingToken = ct; return closing.Task; }));
            var closingRequest = popup.RunFetchAsync();
            popup.Close();
            Require(closingToken.IsCancellationRequested, "Closing the popup did not cancel the lookup");
            closing.SetResult(LookupResult.Success("after close")); await closingRequest;
            Require(((TextBlock)popup.FindName("ResultText")).Text == "fresh", "A request rendered after its popup closed");
            checks.Add("Rendered lookup timeout, actual Retry supersession, close cancellation and stale result suppression");

            // 翻译 UI 现内嵌在 ToolbarWindow 的 TranslatePopup 中（纯 XAML，不再创建独立
            // TranslationPopup 窗口，也不使用 WebView2 渲染）。验证语言下拉已填充、控件齐全。
            Require(toolbar.FindName("TranslateSourceCombo") is System.Windows.Controls.ComboBox { Items.Count: > 0 } sc && sc.Items.Count >= LanguageOptions.All.Count,
                "Translation source languages not populated");
            Require(toolbar.FindName("TranslateTargetCombo") is System.Windows.Controls.ComboBox { Items.Count: > 0 }, "Translation target languages not populated");
            Require(toolbar.FindName("TranslateResultBox") is TextBox, "Translation has no result area");
            Require(toolbar.FindName("TranslateCopyButton") is Button, "Translation has no copy control");
            Render(toolbar, "translation", 430, 260);
            toolbar.CloseTranslatePopup();
            checks.Add("Local translation UI renders with languages/result/copy and closes cleanly without network or WebView2 initialization");

            var recipe = new TextRecipeEditor(new() { Name = "Clean", Steps = ["ws_trim", "case_upper"] });
            Render(recipe, "recipe-editor", 530, 600); recipe.Close();
            checks.Add("Recipe editor resources render");
        }
        catch (Exception ex) { failure = ex.ToString(); }
        File.WriteAllText(Path.Combine(RuntimePaths.DataDirectory, "self-test.json"), JsonSerializer.Serialize(new
        {
            passed = failure == null, checks, failure,
            limitations = "Compiled WPF layout checks do not establish physical keyboard/mouse, UIA provider, browser selection, or mixed-monitor behavior."
        }, new JsonSerializerOptions { WriteIndented = true }));
        return failure == null ? 0 : 1;
    }

    private static void CheckSettingsSaveFailures()
    {
        string path = Path.Combine(RuntimePaths.DataDirectory, "settings.json");
        string temp = path + ".tmp";
        byte[]? original = File.Exists(path) ? File.ReadAllBytes(path) : null;
        var registry = new ActionRegistry();
        var delete = registry.GetAllActionsForCategory(ActionCategory.Transform).Single(a => a.Id == "ws_trim");
        var toolbar = new ToolbarWindow { Registry = registry };
        SetField(toolbar, "_selectedText", "saved preferences");
        try
        {
            foreach (bool failTempWrite in new[] { true, false })
            {
                SettingsManager.Current.PinnedActionIds = ["case_upper", "ws_trim", "case_snake"];
                SettingsManager.Current.DisabledActionIds = ["case_snake"];
                Require(SettingsManager.Save(), "Cannot establish saved preference baseline");
                byte[] saved = File.ReadAllBytes(path);
                ToolbarPreferences.Pin(SettingsManager.Current, delete, "case_upper");
                ToolbarPreferences.SetHidden(SettingsManager.Current, delete, true);
                FileStream? locked = null;
                try
                {
                    if (failTempWrite) Directory.CreateDirectory(temp);
                    else locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                    Require(!SettingsManager.Save(), "A blocked settings write reported success");
                    // 保存失败只要求错误被记录下来（设置窗口的保存状态行会显示它）；工具栏提示条已改为
                    // 固定的操作说明文案，不再回显保存错误，因此不再断言工具栏文本。
                    Require(SettingsManager.LastSaveError != null, "Failed settings save has no error");
                    Require(File.ReadAllBytes(path).SequenceEqual(saved), "Failed save changed the last saved settings");
                    Require(SettingsManager.Current.PinnedActionIds.SequenceEqual(new[] { "ws_trim", "case_upper", "case_snake" }),
                        "Failed save lost the pending pin order");
                    Require(SettingsManager.Current.DisabledActionIds.SequenceEqual(new[] { "case_snake", "ws_trim" }),
                        "Failed save lost the pending hidden actions");
                }
                finally
                {
                    locked?.Dispose();
                    if (failTempWrite) Directory.Delete(temp);
                }
                // A reload after a failed save must recover the complete last successful version.
                SettingsManager.Load();
                Require(SettingsManager.Current.PinnedActionIds.SequenceEqual(new[] { "case_upper", "ws_trim", "case_snake" })
                    && SettingsManager.Current.DisabledActionIds.SequenceEqual(new[] { "case_snake" }),
                    "Reload after failure lost saved toolbar preferences");
                ToolbarPreferences.Pin(SettingsManager.Current, delete, "case_upper");
                ToolbarPreferences.SetHidden(SettingsManager.Current, delete, true);
                Require(SettingsManager.Save() && SettingsManager.LastSaveError == null, "Settings did not recover after the lock was removed");
                Require(!File.Exists(temp), "Successful settings retry left a temporary file");
                SettingsManager.Load();
                Require(SettingsManager.Current.PinnedActionIds.SequenceEqual(new[] { "ws_trim", "case_upper", "case_snake" })
                    && SettingsManager.Current.DisabledActionIds.SequenceEqual(new[] { "case_snake", "ws_trim" }),
                    "Retried toolbar preferences did not survive reload");
            }
        }
        finally
        {
            toolbar.Close();
            if (original == null) File.Delete(path); else File.WriteAllBytes(path, original);
            SettingsManager.Load();
        }
    }

    private static void CheckToolbarCustomization(ActionRegistry registry)
    {
        var settings = SettingsManager.Current;
        // 自定义操作默认停用；自测需要它们充当 context 动作，显式打开开关。
        settings.EnableCustomActions = true;
        settings.UserActions = Enumerable.Range(0, 8).Select(i => new UserAction
        { Id = $"toolbar_test_{i}", Name = $"Suggestion {i + 1}", UrlTemplate = "https://example.com/?q={0}" }).ToList();
        settings.PinnedActionIds = ["search_bing", "search_google", "ws_trim", "case_snake"];
        settings.ShowEncodeActions = false;
        var toolbar = new ToolbarWindow { Registry = registry };
        SetField(toolbar, "_selectedText", "one two");
        SetField(toolbar, "_appName", "brave");
        var border = (Border)toolbar.FindName("MainBorder");
        border.MaxWidth = 1200;
        var pins = (StackPanel)toolbar.FindName("PinnedActionsPanel");
        var context = (StackPanel)toolbar.FindName("ContextActionsPanel");
        Button Pin(string id) => pins.Children.OfType<Button>().Single(b => ((IAction)b.Tag).Id == id);
        foreach (var provider in new[] { SelectionProviderKind.Browser, SelectionProviderKind.UiAutomation })
        foreach (bool editable in new[] { true, false })
        {
            SetField(toolbar, "_selectionProvider", provider);
            SetField(toolbar, "_isEditable", editable);
            foreach (int limit in new[] { 4, 8 })
            {
                settings.MaxInlineContextActions = limit;
                Require(SettingsManager.Save(), "Cannot persist toolbar preferences");
                Require(context.Children.Count == limit, $"{provider} kept a stale inline limit: {context.Children.Count} != {limit}");
                Require(pins.Children.Count == 4, "Pinned actions disappeared because of selection capability or the context limit");
                Require(Pin("ws_trim").IsEnabled && Pin("case_snake").IsEnabled, "Read-only pins have incorrect execution capability");
            }
        }
        Require(Pin("ws_trim").Content is System.Windows.FrameworkElement,
            "Pinned action lost its icon/text content");
        Require(!string.IsNullOrEmpty(Pin("ws_trim").ToolTip.ToString()), "Pinned action lost its tooltip");
        Render(toolbar, "toolbar-eight-suggestions-readonly", 1200, 70);
        settings.ShowTransformActions = false; toolbar.RefreshActions();
        Require(pins.Children.Count == 4, "Hiding a category menu also hid its pins");
        settings.AppHiddenActions["BRAVE"] = ["ws_trim"]; toolbar.RefreshActions();
        Require(!pins.Children.OfType<Button>().Any(b => ((IAction)b.Tag).Id == "ws_trim"), "Pin bypassed the current app's hidden actions");
        SetField(toolbar, "_appName", "notepad"); toolbar.RefreshActions();
        Require(pins.Children.Count == 4, "Browser profile leaked into a native app");
        SetField(toolbar, "_appName", "brave"); settings.AppHiddenActions.Clear();
        settings.ShowTransformActions = true; toolbar.RefreshActions();

        // Exercise real WPF drop routes. The internal event constructor is needed because
        // no system mouse input or OLE drag loop is started by the package self-test.
        var dropConstructor = typeof(DragEventArgs).GetConstructors(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).Single();
        void Drop(IAction action, string? targetId = null, bool after = false)
        {
            Render(toolbar, "toolbar-drag", 1200, 70);
            var target = targetId == null ? null : Pin(targetId);
            var point = target == null ? new Point(border.ActualWidth - 6, 20)
                : target.TranslatePoint(new Point(target.ActualWidth * (after ? 0.9 : 0.1), 18), border);
            SetField(toolbar, "_draggingAction", action);
            foreach (var routedEvent in new[] { DragDrop.PreviewDragOverEvent, DragDrop.PreviewDropEvent })
            {
                var args = (DragEventArgs)dropConstructor.Invoke([new DataObject("SnapActions.ToolbarAction", action.Id), DragDropKeyStates.LeftMouseButton, DragDropEffects.Move, border, point]);
                args.RoutedEvent = routedEvent;
                border.RaiseEvent(args);
                Require(args.Effects == DragDropEffects.Move, "Toolbar rejected its own action drag");
                if (routedEvent == DragDrop.PreviewDragOverEvent)
                    Require(((Border)toolbar.FindName("PinDropIndicator")).Visibility == Visibility.Visible, "Drop insertion point is invisible");
            }
            SetField(toolbar, "_draggingAction", null);
            toolbar.GetType().GetMethod("FinishCustomizationInteraction", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(toolbar, null);
        }
        var upper = registry.GetAllActionsForCategory(ActionCategory.Transform).Single(a => a.Id == "case_upper");
        Drop(upper, "ws_trim");
        Require(settings.PinnedActionIds.SequenceEqual(["search_bing", "search_google", "case_upper", "ws_trim", "case_snake"]), "Drop did not pin at the chosen position");
        Drop(upper, "case_snake", true);
        Require(settings.PinnedActionIds.Last() == upper.Id, "Right-half drop did not move after the target");
        Drop(upper, "search_bing");
        Require(settings.PinnedActionIds.First() == upper.Id, "Left-half drop did not move before the target");

        // 隐藏操作已并入编辑模式目录（不再有右键“隐藏操作”菜单项）：直接经
        // ToolbarPreferences.SetHidden 隐藏，验证 pin 消失且顺序保留，再由下方编辑模式目录恢复。
        ToolbarPreferences.SetHidden(SettingsManager.Current, upper, true);
        toolbar.RefreshActions();
        Require(!pins.Children.OfType<Button>().Any(b => ((IAction)b.Tag).Id == upper.Id), "Hide left a stale pinned button");
        Require(settings.PinnedActionIds.Contains(upper.Id), "Hiding lost the pin's order");
        // 新版编辑模式(clipboard->Gear)仅在已打开分类子菜单时生效：先打开 Transform 分类，
        // 再点 GearButton 进入编辑模式，目录用 GetAllActionsForCategory 重建（含被隐藏的 case_upper）。
        ((Button)toolbar.FindName("TransformButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        ((Button)toolbar.FindName("GearButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var catalog = (System.Windows.Controls.Panel)toolbar.FindName("SubMenuPanel");
        var catalogPopup = (System.Windows.Controls.Primitives.Popup)toolbar.FindName("SubMenuPopup");
        var catalogContent = (FrameworkElement)catalogPopup.Child;
        catalogContent.Measure(new Size(640, 420)); catalogContent.Arrange(new Rect(0, 0, 640, 420)); catalogContent.UpdateLayout();
        var catalogBitmap = new RenderTargetBitmap(640, 420, 96, 96, PixelFormats.Pbgra32);
        catalogBitmap.Render(catalogContent);
        var catalogPng = new PngBitmapEncoder(); catalogPng.Frames.Add(BitmapFrame.Create(catalogBitmap));
        using (var image = File.Create(Path.Combine(RuntimePaths.DataDirectory, "toolbar-customization.png"))) catalogPng.Save(image);
        var restore = catalog.Children.OfType<Button>().Single(b => ((IAction)b.Tag).Id == upper.Id);
        restore.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Require(pins.Children.OfType<Button>().Any(b => ((IAction)b.Tag).Id == upper.Id), "Catalog did not restore a hidden pin");
        var unpin = Pin(upper.Id).ContextMenu.Items.OfType<System.Windows.Controls.MenuItem>().Single(i => (string)i.Header == "从工具栏取消固定");
        unpin.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.MenuItem.ClickEvent));
        Require(!settings.PinnedActionIds.Contains(upper.Id), "Unpin failed");
        Require(catalog.Children.OfType<Button>().Any(b => ((IAction)b.Tag).Id == upper.Id), "Unpin hid the action from its menu");
        settings.PinnedActionIds.Clear(); toolbar.RefreshActions(); Drop(upper);
        Require(settings.PinnedActionIds.SequenceEqual([upper.Id]), "Cannot pin the first action onto an empty toolbar");
        toolbar.Close();
    }

    private static async Task CheckToolbarPreviewAsync(ActionRegistry registry)
    {
        SettingsManager.Current.PinnedActionIds = ["case_upper"];
        var toolbar = new ToolbarWindow { Registry = registry };
        try
        {
            SetField(toolbar, "_selectedText", "Hello العربية");
            toolbar.RefreshActions();
            var menu = (Button)toolbar.FindName("TransformButton");
            var popup = (System.Windows.Controls.Primitives.Popup)toolbar.FindName("SubMenuPopup");
            var preview = (Border)toolbar.FindName("PreviewBorder");
            var text = (TextBlock)toolbar.FindName("PreviewText");
            // WPF defers IsOpen on an unloaded popup. Load a transparent, non-activating
            // host so category toggles exercise the real popup lifecycle without taking focus.
            ((FrameworkElement)popup.Child).Opacity = 0;
            ((FrameworkElement)popup.Child).IsHitTestVisible = false;
            toolbar.Left = toolbar.Top = -32000;
            toolbar.Topmost = false;
            toolbar.Show();
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Loaded);
            void Hover(Button button, RoutedEvent route) => button.RaiseEvent(new System.Windows.Input.MouseEventArgs(
                System.Windows.Input.Mouse.PrimaryDevice, 0) { RoutedEvent = route });

            menu.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            menu.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(!popup.IsOpen, "Category toggle did not close the menu");
            menu.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var upper = ((System.Windows.Controls.Panel)toolbar.FindName("SubMenuPanel")).Children.OfType<Button>().Single(b => ((IAction)b.Tag).Id == "case_upper");
            Hover(upper, UIElement.MouseEnterEvent);
            Require(popup.IsOpen && preview.Visibility == Visibility.Visible && text.Opacity == 1,
                $"Hover preview stayed hidden after closing and reopening a category: open={popup.IsOpen}, band={preview.Visibility}, opacity={text.Opacity}, enabled={upper.IsEnabled}, text={text.Text}");
            Require(new System.Windows.Documents.TextRange(text.ContentStart, text.ContentEnd).Text == "HELLO العربية", "Hover preview changed the result text");
            Hover(upper, UIElement.MouseLeaveEvent);
            Require(popup.IsOpen, "Leaving a submenu action closed the action menu");
            menu.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var pin = ((StackPanel)toolbar.FindName("PinnedActionsPanel")).Children.OfType<Button>().Single();
            Hover(pin, UIElement.MouseEnterEvent);
            Require(popup.IsOpen && preview.Visibility == Visibility.Visible && text.Opacity == 1, "Inline hover did not restore the preview");
            Require(((FrameworkElement)toolbar.FindName("SubMenuHeader")).Visibility == Visibility.Collapsed, "Hover-only preview has an empty menu heading");
            Hover(pin, UIElement.MouseLeaveEvent);
            Require(!popup.IsOpen, "Inline hover left an empty popup behind");

            foreach (string theme in new[] { "dark", "light" })
            {
                SettingsManager.Current.Theme = theme; ThemeManager.Apply();
                SetField(toolbar, "_selectedText", string.Concat(Enumerable.Repeat("Hello العربية ", 15)));
                Hover(pin, UIElement.MouseEnterEvent);
                RenderPreview("toolbar-hover-" + theme);
                Require(text.ActualWidth <= preview.ActualWidth - preview.Padding.Left - preview.Padding.Right,
                    "Long preview text escaped its band instead of trimming");
                Hover(pin, UIElement.MouseLeaveEvent);
            }

            menu.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(((FrameworkElement)toolbar.FindName("SubMenuHeader")).Visibility == Visibility.Visible, "Opening a menu after hovering lost its heading");
            Hover(pin, UIElement.MouseEnterEvent);
            Hover(pin, UIElement.MouseLeaveEvent);
            Require(popup.IsOpen, "Leaving an inline action closed a real submenu");
            ((Button)toolbar.FindName("GearButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(preview.Visibility == Visibility.Collapsed, "Customization left an empty preview band");
            popup.IsOpen = false;
            var toast = (Task)toolbar.GetType().GetMethod("ShowCopiedToast", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(toolbar, null)!;
            Require(popup.IsOpen && preview.Visibility == Visibility.Visible && text.Opacity == 1, "Copy feedback stayed hidden after customization");
            await toast;

            void RenderPreview(string name)
            {
                // Capture the popup after closing its transparent native surface.
                popup.IsOpen = false;
                var content = (FrameworkElement)popup.Child;
                content.Opacity = 1;
                content.Measure(new Size(420, 400));
                content.Arrange(new Rect(new Point(), content.DesiredSize)); content.UpdateLayout();
                var bitmap = new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth), (int)Math.Ceiling(content.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(content);
                var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
                using (var image = File.Create(Path.Combine(RuntimePaths.DataDirectory, name + ".png"))) png.Save(image);
                content.Opacity = 0;
            }
        }
        finally { ((System.Windows.Controls.Primitives.Popup)toolbar.FindName("SubMenuPopup")).IsOpen = false; toolbar.Close(); }
    }

    private static void SetField(object instance, string name, object? value) => instance.GetType()
        .GetField(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(instance, value);
    private static object GetField(object instance, string name) => instance.GetType()
        .GetField(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(instance)!;
    /// <summary>元素（含子级逻辑树）里是否含 JS 徽标 —— 设置行、工具栏固定按钮、子菜单项共用同一检查。</summary>
    private static bool HasJsBadge(DependencyObject root) =>
        Tree(root).OfType<Border>().Any(b => (b.Child as TextBlock)?.Text == "JS");
    private static IEnumerable<DependencyObject> Tree(DependencyObject root)
    {
        foreach (var child in System.Windows.LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            yield return child;
            foreach (var nested in Tree(child)) yield return nested;
        }
    }
    private static void Require(bool condition, string failure) { if (!condition) throw new InvalidOperationException(failure); }
    private static void Render(Window window, string name, double width, double height)
    {
        var content = (FrameworkElement)window.Content;
        if (content is System.Windows.Controls.Panel panel) panel.Background = window.Background;
        content.Measure(new Size(width, height)); content.Arrange(new Rect(0, 0, width, height)); content.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)width, (int)height, 96, 96, PixelFormats.Pbgra32);
        var background = new DrawingVisual();
        using (var drawing = background.RenderOpen()) drawing.DrawRectangle(window.Background, null, new Rect(0, 0, width, height));
        bitmap.Render(background); bitmap.Render(content);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(RuntimePaths.DataDirectory, name + ".png")); png.Save(file);
    }

    /// <summary>渲染单个元素（自带尺寸）——用于折叠容器里的内容，例如未展开的 Expander。</summary>
    private static void RenderElement(FrameworkElement element, string name, double width, double height)
    {
        element.Measure(new Size(width, height));
        element.Arrange(new Rect(new Point(), element.DesiredSize));
        element.UpdateLayout();
        var bitmap = new RenderTargetBitmap(
            Math.Max(1, (int)Math.Ceiling(element.ActualWidth)), Math.Max(1, (int)Math.Ceiling(element.ActualHeight)),
            96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(RuntimePaths.DataDirectory, name + ".png")); png.Save(file);
    }
}
