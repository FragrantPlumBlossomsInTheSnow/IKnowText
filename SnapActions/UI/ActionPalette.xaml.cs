using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SnapActions.Actions;
using SnapActions.Core;
using SnapActions.Detection;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace SnapActions.UI;

public partial class ActionPalette : Window
{
    private readonly SelectionSnapshot? _selection;
    private readonly ActionRegistry _registry;
    private readonly SelectionOperationSource _manualOperations = new();
    private readonly string? _appName;
    private List<IAction> _actions = [];
    private bool _ready, _running;

    internal ActionPalette(SelectionSnapshot? selection, ActionRegistry registry)
    {
        _selection = selection;
        _registry = registry;
        _appName = ForegroundApp.GetActiveProcessName();
        InitializeComponent();
        SourceBox.Text = selection?.Text ?? "";
        SourceBox.IsReadOnly = selection != null;
        SourceBox.FlowDirection = ToolbarWindow.GetPreviewFlowDirection(SourceBox.Text);
        SourceLabel.Text = selection == null ? "没有选中内容 —— 在这里输入文本" : "选中内容";
        ((ComboBoxItem)DestinationBox.Items[1]).IsEnabled = selection?.CanReplace == true;
        DestinationBox.SelectedIndex = selection?.CanReplace == true && Config.SettingsManager.Current.ReplaceSelectionOnTransform ? 1 : 0;
        _ready = true;
        RebuildActions();
        Loaded += (_, _) => { if (_selection == null) SourceBox.Focus(); else SearchBox.Focus(); };
        Deactivated += (_, _) => { if (!_running) Close(); };
        Closed += (_, _) => { _selection?.Operation.InvalidateIfCurrent(); _manualOperations.Invalidate(); };
    }

    private void RebuildActions()
    {
        string text = _selection?.Text ?? SourceBox.Text;
        var analysis = _selection?.Analysis ?? new TextClassifier().Classify(text);
        _actions = _registry.GetActions(text, analysis, _appName).SelectMany(g => g.Actions)
            .Where(a => a is not IOperationAction || _selection?.CanReplace == true)
            // 同一动作可能同时出现在 Context 与 Transform 两组（JS 脚本动作的「上下文触发」正则命中时），
            // 调色板是一维列表，按 id 去重避免同一个动作列两遍。
            .DistinctBy(a => a.Id, StringComparer.Ordinal).ToList();
        FilterActions();
    }

    private void FilterActions()
    {
        if (!_ready) return;
        var terms = SearchBox.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        ActionsList.ItemsSource = _actions.Where(a => terms.All(t => a.Name.Contains(t, StringComparison.OrdinalIgnoreCase)
            || a.Category.ToString().Contains(t, StringComparison.OrdinalIgnoreCase))).ToList();
        ActionsList.SelectedIndex = ActionsList.Items.Count > 0 ? 0 : -1;
    }

    private void Search_Changed(object sender, TextChangedEventArgs e) => FilterActions();
    private void Source_Changed(object sender, TextChangedEventArgs e) { if (_ready && _selection == null) RebuildActions(); }
    private void Destination_Changed(object sender, SelectionChangedEventArgs e) { if (_ready) UpdatePreview(); }
    private void Action_Changed(object sender, SelectionChangedEventArgs e) { if (_ready) UpdatePreview(); }

    private void UpdatePreview()
    {
        if (ActionsList.SelectedItem is not IAction action) { RunButton.IsEnabled = false; PreviewText.Text = "没有匹配的操作"; return; }
        RunButton.IsEnabled = true;
        if (action.IsPreviewSafe)
        {
            string text = _selection?.Text ?? SourceBox.Text;
            ActionResult result;
            try { result = action.Execute(text, _selection?.Analysis ?? new TextClassifier().Classify(text)); }
            catch { result = new(false, Message: "此选中内容无法预览。"); }
            PreviewText.Text = result.Success ? result.ResultText : result.Message;
            PreviewText.FlowDirection = ToolbarWindow.GetPreviewFlowDirection(PreviewText.Text ?? "");
            RunButton.IsEnabled = result.Success;
            RunButton.Content = DestinationBox.SelectedIndex == 1 ? "替换选中内容" : "复制结果";
            DestinationBox.Visibility = Visibility.Visible;
        }
        else
        {
            PreviewText.Text = action is IOperationAction ? "此操作会更改原始应用程序中的文本。" : $"Run {action.Name}";
            RunButton.Content = action.Name;
            DestinationBox.Visibility = Visibility.Collapsed;
        }
    }

    private void Palette_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { e.Handled = true; Close(); }
        else if (e.Key is Key.Down or Key.Up && !SourceBox.IsKeyboardFocusWithin)
        {
            ActionsList.SelectedIndex = Math.Clamp(ActionsList.SelectedIndex + (e.Key == Key.Down ? 1 : -1), 0, Math.Max(0, ActionsList.Items.Count - 1));
            ActionsList.ScrollIntoView(ActionsList.SelectedItem);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && !SourceBox.IsKeyboardFocusWithin) { e.Handled = true; Run_Click(this, e); }
    }

    private async void Run_Click(object sender, RoutedEventArgs e)
    {
        if (_running || !RunButton.IsEnabled || ActionsList.SelectedItem is not IAction action) return;
        _running = true;
        var selection = _selection ?? new SelectionSnapshot(SourceBox.Text, new TextClassifier().Classify(SourceBox.Text),
            _manualOperations.Begin(default), false, SelectionProviderKind.Manual);
        Hide();
        try
        {
            if (_selection != null && !await GlobalHotkey.ReturnToTargetAsync(selection.Operation))
            {
                ResultPopup.ShowLocalResult("操作不可用", "原始窗口无法聚焦。再次选择文本。");
                Close(); return;
            }
            var result = await ActionRunner.ExecuteAsync(action, selection);
            if (result.Success && result.ResultText != null)
                result = await ActionRunner.ApplyTextAsync(result.ResultText, selection,
                    DestinationBox.SelectedIndex == 1 ? ResultDestination.Replace : ResultDestination.Copy);
            if (!result.Success) ResultPopup.ShowLocalResult("操作不可用", result.Message ?? "操作无法完成");
            Close();
        }
        catch (Exception ex)
        {
            Helpers.Log.Warn($"调色板操作失败 ({ex.GetType().Name})");
            Close();
        }
    }
}
