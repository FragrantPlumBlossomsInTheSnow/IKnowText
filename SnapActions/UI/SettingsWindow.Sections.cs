using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SnapActions.Actions.UserActions;
using SnapActions.Config;
using SnapActions.Core;
using SnapActions.Helpers;
using SnapActions.Services;
using ComboBox = System.Windows.Controls.ComboBox;
using CheckBox = System.Windows.Controls.CheckBox;

namespace SnapActions.UI;

public partial class SettingsWindow
{
    private void LoadAdditionalSettings()
    {
        var s = SettingsManager.Current;
        SelectComboByTag(ThemeCombo, s.Theme, 0);
        TranslationSourceCombo.ItemsSource = new[]
        {
            new LanguageOption("", "检测语言"),
            new LanguageOption("system", "Windows显示语言"),
        }.Concat(LanguageOptions.All);
        TranslationTargetCombo.ItemsSource = new[] { new LanguageOption("", "Windows显示语言") }.Concat(LanguageOptions.All);
        TranslationSourceCombo.SelectedValue = s.TranslationSourceFollowSystem ? "system" : s.TranslationSourceLanguage;
        TranslationTargetCombo.SelectedValue = s.TranslationTargetFollowSystem ? "" : s.TranslationTargetLanguage;
        SystemTargetCombo.ItemsSource = LanguageOptions.All;
        SystemTargetCombo.SelectedValue = s.TranslationSystemTargetLanguage;
        ExclusionTextRegex.Text = s.ExcludeRegex;
        var (appId, secret) = CredentialCrypto.DecryptBaidu(s.BaiduCredentialsBlob);
        BaiduAppIdBox.Text = appId;
        BaiduSecretBox.Password = secret;
        AutoStartCheck.IsEnabled = !RuntimePaths.IsIsolated;
        BuildRecipesList();
        BuildScriptActionsList();
        // RefreshBrowserHealth();
    }

    private bool SaveWithStatus()
    {
        bool saved = SettingsManager.Save();
        SaveStatusText.Text = saved ? "已保存" : SettingsManager.LastSaveError;
        RetrySaveButton.Visibility = saved ? Visibility.Collapsed : Visibility.Visible;
        return saved;
    }

    // TextChanged (not TextInput): fires for paste / IME composition / clear too, so any way the
    // user modifies the box is synced into settings and persisted when the debounce tick runs.
    private void ExcludeRegex_Changed(object sender, TextChangedEventArgs e)
    {
        SettingsManager.Current.ExcludeRegex = ExclusionTextRegex.Text;
        QueueSave();
    }

    // 百度凭据没有「保存」按钮：任一输入框变化都排一次防抖自动保存（与窗口里其它设置一致）。
    // 这里只排队，不在每次按键时加密写盘 —— 取值范围与加密统一放在 FlushPendingTextEdits，
    // 这样防抖 tick 与「关窗早于 tick」两条路径都落到同一份逻辑上。
    private void BaiduAppId_Changed(object sender, TextChangedEventArgs e) => QueueSave();

    private void BaiduSecret_Changed(object sender, RoutedEventArgs e) => QueueSave();

    private void LookupLanguage_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        var source = TranslationSourceCombo.SelectedValue as string ?? "";
        SettingsManager.Current.TranslationSourceFollowSystem = source == "system";
        if (source != "system") SettingsManager.Current.TranslationSourceLanguage = source;
        var target = TranslationTargetCombo.SelectedValue as string ?? "";
        SettingsManager.Current.TranslationTargetFollowSystem = string.IsNullOrEmpty(target);
        if (!string.IsNullOrEmpty(target)) SettingsManager.Current.TranslationTargetLanguage = target;
        if (ReferenceEquals(sender, SystemTargetCombo))
            SettingsManager.Current.TranslationSystemTargetLanguage = SystemTargetCombo.SelectedValue as string ?? "en";
        QueueSave();
    }

    private void Theme_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || ThemeCombo.SelectedItem is not ComboBoxItem item) return;
        SettingsManager.Current.Theme = item.Tag?.ToString() ?? "system";
        ThemeManager.Apply();
        _textBrush = (System.Windows.Media.Brush)FindResource("TextFillColorPrimaryBrush");
        _secondaryBrush = (System.Windows.Media.Brush)FindResource("TextFillColorSecondaryBrush");
        _loading = true;
        BuildSearchEnginesList(); BuildAppProfilesList(); BuildRecipesList(); BuildPinnedAppsList(); BuildScriptActionsList();
        _loading = false;
        QueueSave();
    }

    /*private void SettingsSearch_Changed(object sender, TextChangedEventArgs e)
    {
        if (SettingsTabs == null) return;
        var terms = SettingsSearchBox.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        int matches = 0;
        foreach (TabItem tab in SettingsTabs.Items)
        {
            var content = tab.Header + " " + SearchableText((DependencyObject)tab.Content);
            bool match = terms.All(t => content.Contains(t, StringComparison.OrdinalIgnoreCase));
            tab.Visibility = match ? Visibility.Visible : Visibility.Collapsed;
            if (match) matches++;
        }
        if (SettingsTabs.SelectedItem is not TabItem { Visibility: Visibility.Visible })
            SettingsTabs.SelectedItem = SettingsTabs.Items.Cast<TabItem>().FirstOrDefault(t => t.Visibility == Visibility.Visible);
        SearchStatusText.Text = terms.Length == 0 ? "" : matches == 0 ? "No matching settings" : $"{matches} section(s)";
    }*/

    private static string SearchableText(DependencyObject node)
    {
        string text = node switch
        {
            TextBlock block => block.Text,
            ContentControl { Content: string content } => content,
            _ => ""
        };
        foreach (var child in LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>()) text += " " + SearchableText(child);
        return text;
    }

    // private string SelectedBrowser => (BrowserCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Brave";
    // private void Browser_Changed(object sender, SelectionChangedEventArgs e) { if (!_loading) RefreshBrowserHealth(); }
    // private void RefreshBrowser_Click(object sender, RoutedEventArgs e) => RefreshBrowserHealth();
    // private void RefreshBrowserHealth()
    // {
    //     BrowserStatusText.Text = BrowserSetupService.Status(SelectedBrowser);
    //     DiagnosticsBox.Text = CaptureDiagnostics.Summary();
    // }
    // private void RegisterBrowser_Click(object sender, RoutedEventArgs e)
    // {
    //     try { BrowserSetupService.Register(SelectedBrowser); RefreshBrowserHealth(); }
    //     catch (Exception ex) { BrowserStatusText.Text = "Registration failed: " + ex.Message; }
    // }
    // private void OpenExtension_Click(object sender, RoutedEventArgs e) => OpenSetupPath(BrowserSetupService.ExtensionDirectory);
    // private void OpenBrowserSample_Click(object sender, RoutedEventArgs e) => OpenSetupPath(System.IO.Path.Combine(BrowserSetupService.ExtensionDirectory, "selection-sample.html"));
    // private void OpenSetupPath(string path)
    // {
    //     if (!System.IO.File.Exists(path) && !System.IO.Directory.Exists(path))
    //     { BrowserStatusText.Text = "The companion files are missing. Extract the complete release package."; return; }
    //     var result = ProcessHelper.TryOpenLocalPath(path, "Opened");
    //     if (!result.Success) BrowserStatusText.Text = result.Message;
    // }

    private void BuildRecipesList()
    {
        RecipesPanel.Children.Clear();
        foreach (var recipe in SettingsManager.Current.TextRecipes)
        {
            var row = new DockPanel { Margin = new Thickness(0, 4, 0, 4) };
            var remove = new Button { Content = "删除", Padding = new Thickness(8, 3, 8, 3) };
            System.Windows.Automation.AutomationProperties.SetName(remove, "Delete recipe " + recipe.Name);
            remove.Click += (_, _) => { SettingsManager.Current.TextRecipes.Remove(recipe); BuildRecipesList(); QueueSave(); };
            DockPanel.SetDock(remove, Dock.Right); row.Children.Add(remove);
            var edit = new Button { Content = "编辑", Margin = new Thickness(8, 0, 8, 0), Padding = new Thickness(8, 3, 8, 3) };
            System.Windows.Automation.AutomationProperties.SetName(edit, "Edit recipe " + recipe.Name);
            edit.Click += (_, _) => EditRecipe(recipe);
            DockPanel.SetDock(edit, Dock.Right); row.Children.Add(edit);
            var enabled = new CheckBox { Content = recipe.Name, IsChecked = recipe.Enabled, VerticalAlignment = VerticalAlignment.Center };
            enabled.Checked += (_, _) => { recipe.Enabled = true; QueueSave(); };
            enabled.Unchecked += (_, _) => { recipe.Enabled = false; QueueSave(); };
            row.Children.Add(enabled); RecipesPanel.Children.Add(row);
        }
    }
    private void CreateRecipe_Click(object sender, RoutedEventArgs e) => EditRecipe(null);
    private void EditRecipe(TextRecipeDefinition? recipe)
    {
        var editor = new TextRecipeEditor(recipe) { Owner = this };
        if (editor.ShowDialog() != true) return;
        if (recipe == null) SettingsManager.Current.TextRecipes.Add(editor.Recipe);
        else { recipe.Name = editor.Recipe.Name; recipe.Steps = editor.Recipe.Steps; }
        BuildRecipesList(); QueueSave();
    }

    private void BuildScriptActionsList()
    {
        ScriptActionsPanel.Children.Clear();
        // 仅列出脚本条目（独立 .js 文件或旧的内嵌 Code）；URL 模板条目不显示但仍可作为动作运行。
        foreach (var action in SettingsManager.Current.UserActions
            .Where(a => !string.IsNullOrWhiteSpace(a.Code) || !string.IsNullOrWhiteSpace(a.ScriptFile)).ToList())
        {
            var row = new DockPanel { Margin = new Thickness(0, 4, 0, 4) };
            var remove = new Button { Content = "删除", Padding = new Thickness(8, 3, 8, 3) };
            System.Windows.Automation.AutomationProperties.SetName(remove, "Delete script action " + action.Name);
            remove.Click += (_, _) =>
            {
                if(MessageBoxResult.No != MessageBox.Show("删除 " + action.Name, "SpanActions", MessageBoxButton.YesNo))
                    return;
                ScriptActionStorage.Delete(action);
                SettingsManager.Current.UserActions.Remove(action);
                BuildScriptActionsList(); QueueSave();
            };
            DockPanel.SetDock(remove, Dock.Right); row.Children.Add(remove);
            var edit = new Button { Content = "编辑", Margin = new Thickness(8, 0, 8, 0), Padding = new Thickness(8, 3, 8, 3) };
            System.Windows.Automation.AutomationProperties.SetName(edit, "Edit script action " + action.Name);
            edit.Click += (_, _) => EditScriptAction(action);
            DockPanel.SetDock(edit, Dock.Right); row.Children.Add(edit);
            var enabled = new CheckBox
            {
                Content = action.Name,
                IsChecked = action.Enabled,
                VerticalAlignment = VerticalAlignment.Center
            };
            enabled.Checked += (_, _) => { action.Enabled = true; QueueSave(); };
            enabled.Unchecked += (_, _) => { action.Enabled = false; QueueSave(); };
            row.Children.Add(enabled);
            ScriptActionsPanel.Children.Add(row);
        }
    }

    private void AddScriptAction_Click(object sender, RoutedEventArgs e) => EditScriptAction(null);

    private void EditScriptAction(UserAction? action)
    {
        var editor = new UserScriptEditor(action) { Owner = this };
        if (editor.ShowDialog() != true) return;
        var existing = SettingsManager.Current.UserActions.FirstOrDefault(a => a.Id == editor.Action.Id);
        if (existing == null) SettingsManager.Current.UserActions.Add(editor.Action);
        else { existing.Name = editor.Action.Name; existing.Code = editor.Action.Code; existing.ContextRegex = editor.Action.ContextRegex; }
        BuildScriptActionsList(); QueueSave();
    }

}
