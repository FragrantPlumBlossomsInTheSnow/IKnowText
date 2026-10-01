using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using SnapActions.Actions.UserActions;
using SnapActions.Config;
using Brushes = System.Windows.Media.Brushes;

namespace SnapActions.UI;

/// <summary>
///     JS 脚本动作编辑器：名称 + JSAction(text) 脚本 + 「上下文触发」正则 + 即时试跑预览/日志。
///     界面在 UserScriptEditor.xaml，本文件只放行为：控件事件 → Preview()/UpdateTriggerHint()/保存校验。
///     试跑用独立 JsScriptRunner 实例，不碰生产共享缓存；沙箱 console 的输出只在这里显示
///     （动作实际执行时静默），方便用户调试脚本。
/// </summary>
public partial class UserScriptEditor : Window
{
    private readonly JsScriptRunner _runner = new();

    internal UserScriptEditor(UserAction? source)
    {
        // 编辑时从独立脚本文件读回源码（ScriptFile 优先），旧数据回退内嵌 Code。
        var sourceCode = source == null
            ? "function JSAction(text) {\n\tconsole.log('日志输出：', text); // 试跑时显示在下方「console 输出」\n\treturn text;\n}"
            : ScriptActionStorage.LoadCode(source) ?? "";
        Action = new UserAction
        {
            Id = source?.Id ?? Guid.NewGuid().ToString("N"),
            Name = source?.Name ?? "",
            Code = sourceCode,
            ScriptFile = source?.ScriptFile ?? "",
            UrlTemplate = source?.UrlTemplate ?? "",
            Kind = source?.Kind ?? UserActionKind.OpenUrl,
            AppliesToType = source?.AppliesToType ?? "",
            JsonField = source?.JsonField ?? "",
            ContextRegex = source?.ContextRegex ?? "",
            Enabled = source?.Enabled ?? true
        };

        InitializeComponent();
        Title = source == null ? "创建 JS 脚本动作" : "编辑 JS 脚本动作";
        NameBox.Text = Action.Name;
        // 默认试跑文本放在这里而不是 XAML：XAML 里设 Text 会在 InitializeComponent 过程中触发
        // Sample_Changed，此时后面的 TriggerBox/TriggerHintText 还没创建。下面几行都在控件齐了之后赋值。
        SampleBox.Text = "Hello, world!";
        CodeBox.Text = Action.Code; // 触发 Code_Changed → Preview()
        TriggerBox.Text = Action.ContextRegex;
        Preview();
    }

    internal UserAction Action { get; }

    private void Code_Changed(object sender, TextChangedEventArgs e)
    {
        Preview();
    }

    private void Sample_Changed(object sender, TextChangedEventArgs e)
    {
        Preview();
    }

    private void Trigger_Changed(object sender, TextChangedEventArgs e)
    {
        UpdateTriggerHint();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        StatusText.Foreground = Brushes.OrangeRed;
        if (string.IsNullOrWhiteSpace(NameBox.Text))
        {
            StatusText.Text = "请输入动作名称";
            return;
        }

        if (string.IsNullOrWhiteSpace(CodeBox.Text))
        {
            StatusText.Text = "请输入脚本代码";
            return;
        }

        if (!ContextTriggerRegex.IsValid(TriggerBox.Text))
        {
            StatusText.Text = "「上下文触发」正则无效，请修正或清空后再保存";
            return;
        }

        var sample = string.IsNullOrEmpty(SampleBox.Text) ? "test" : SampleBox.Text;
        if (!_runner.Run(CodeBox.Text, sample, out _, out var error))
        {
            StatusText.Text = error ?? "试跑失败";
            return;
        }

        Action.Name = NameBox.Text.Trim();
        Action.ContextRegex = TriggerBox.Text.Trim();
        // 代码写入数据目录 scripts\ 下的独立 .js 文件，settings.json 只存文件名（ScriptFile），
        // 避免多行代码/引号在 JSON 中转义出错；内嵌 Code 清空，防止双份拷贝不同步。
        var fileName = ScriptActionStorage.SaveCode(Action, CodeBox.Text);
        if (fileName == null)
        {
            StatusText.Text = "脚本文件写入失败，请检查数据目录权限";
            return;
        }

        Action.ScriptFile = fileName;
        Action.Code = "";
        DialogResult = true;
    }

    private void Preview()
    {
        UpdateTriggerHint();
        if (string.IsNullOrWhiteSpace(CodeBox.Text))
        {
            PreviewBox.Text = "";
            LogsBox.Text = "";
            StatusText.Text = "";
        }

        var logLines = new List<string>();
        if (_runner.Run(CodeBox.Text, SampleBox.Text, out var result, out var error, logLines))
        {
            // 试跑用独立 runner（独立引擎缓存），不碰生产共享实例的状态。
            PreviewBox.Text = result ?? "";
            StatusText.Foreground = (Brush)FindResource("TextFillColorPrimaryBrush");
            StatusText.Text = "试跑成功";
        }
        else
        {
            PreviewBox.Text = "";
            StatusText.Foreground = Brushes.OrangeRed;
            StatusText.Text = error ?? "试跑失败";
        }

        // 失败时也保留抛错前的 console 输出，便于定位问题。
        LogsBox.Text = string.Join(Environment.NewLine, logLines);
    }

    /// <summary>
    ///     「上下文触发」正则的即时反馈：是否有效、以及相对「试跑文本」会不会命中 —— 命中与否直接决定
    ///     该动作会不会出现在工具栏上下文区，靠读代码猜不出来，所以在编辑器里当场给出结论。
    /// </summary>
    private void UpdateTriggerHint()
    {
        var pattern = TriggerBox.Text;
        if (string.IsNullOrWhiteSpace(pattern))
        {
            TriggerHintText.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
            TriggerHintText.Text = "留空：不进入上下文区，只作为文本转换动作";
            return;
        }

        if (!ContextTriggerRegex.IsValid(pattern))
        {
            TriggerHintText.SetResourceReference(TextBlock.ForegroundProperty, "SystemFillColorCautionBrush");
            TriggerHintText.Text = "正则无效，请修正";
            return;
        }

        var matches = ContextTriggerRegex.IsMatch(pattern, SampleBox.Text);
        TriggerHintText.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        TriggerHintText.Foreground = (Brush)FindResource("TextFillColorSecondaryBrush");
        TriggerHintText.Text = matches
            ? "正则有效；「试跑文本」命中 → 工具栏会推送该动作"
            : "正则有效；「试跑文本」未命中 → 工具栏不会推送该动作";
    }

    private void GenerateJSAction_Click(object sender, RoutedEventArgs e)
    {
        var reg = FunctionMatch();
        if (reg.IsMatch(CodeBox.Text)) return;
        var code = "function JSAction(text) {\n\tconsole.log(\"日志输出：\", text);\n\treturn text;\n}\n" + CodeBox.Text;
        CodeBox.Text = code;
    }

    [GeneratedRegex(@"\s*function\s+JSAction\s*\([\S\s]\)*?\{[\S\s]*?\}\s*")]
    private static partial Regex FunctionMatch();
}