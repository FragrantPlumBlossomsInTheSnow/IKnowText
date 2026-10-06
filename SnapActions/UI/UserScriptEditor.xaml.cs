using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SnapActions.Actions.UserActions;
using SnapActions.Config;
using Brushes = System.Windows.Media.Brushes;

namespace SnapActions.UI;

/// <summary>
///     JS 脚本动作编辑器：名称 + JSAction(text) 脚本 + 「上下文触发」正则 + 即时试跑预览/日志。
///     界面在 UserScriptEditor.xaml，本文件只放行为：控件事件 → Preview()/UpdateTriggerHint()/保存校验。
///     试跑用独立 JsScriptRunner 实例，不碰生产共享缓存；沙箱 console 的输出只在这里显示
///     （动作实际执行时静默），方便用户调试脚本。
///     勾选「允许访问网络」后试跑会真的发请求，因此该状态下试跑做防抖（避免每次按键都发一次），
///     并且需要先通过在线查询同意门；未授权时自动试跑直接跳过（不再反复弹窗），保存前会再问一次。
///     自定义翻译引擎有自己的编辑器 TranslationEngineEditor（引擎必然联网）。
/// </summary>
public partial class UserScriptEditor : Window
{
    private readonly JsScriptRunner _runner = new();

    /// <summary>联网试跑的防抖：只对「允许访问网络」的脚本生效，避免敲键即发请求。</summary>
    private readonly DispatcherTimer _previewDebounce = new() { Interval = TimeSpan.FromMilliseconds(600) };

    internal UserScriptEditor(UserAction? source)
    {
        // 编辑时从独立脚本文件读回源码（ScriptFile 优先），旧数据回退内嵌 Code。
        var sourceCode = source == null
            ? "function JSAction(text) {\n\tconsole.log('日志输出：', text); // 试跑时显示在下方「console 输出」\n\treturn 'IKnowText：' + text;\n}"
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
            AllowNetwork = source?.AllowNetwork ?? false,
            Enabled = source?.Enabled ?? true
        };

        InitializeComponent();
        Title = source == null ? "创建 JS 脚本动作" : "编辑 JS 脚本动作";
        // 联网试跑防抖：控件齐了之后再挂钩，避免构造过程中的赋值触发试跑。
        _previewDebounce.Tick += (_, _) => { _previewDebounce.Stop(); _ = PreviewAsync(); };
        Closed += (_, _) => _previewDebounce.Stop();
        NameBox.Text = Action.Name;
        // 默认试跑文本放在这里而不是 XAML：XAML 里设 Text 会在 InitializeComponent 过程中触发
        // Sample_Changed，此时后面的 TriggerBox/TriggerHintText 还没创建。下面几行都在控件齐了之后赋值。
        SampleBox.Text = "Hello, world!";
        AllowNetworkCheck.IsChecked = Action.AllowNetwork; // 先定网络开关，后面 Preview 才用对状态
        UpdateSandboxHint();
        CodeBox.Text = Action.Code; // 触发 Code_Changed → Preview()
        TriggerBox.Text = Action.ContextRegex;
        _loading = false;
        Preview();
    }

    /// <summary>构造期间屏蔽事件触发的试跑（此时控件还没赋值完）。</summary>
    private bool _loading = true;

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

    private async void Save_Click(object sender, RoutedEventArgs e)
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

        var allowNetwork = AllowNetworkCheck.IsChecked == true;
        // 保存前的试跑会真的发请求，所以先过同意门（用户显式动作，问一次是应该的）。
        if (allowNetwork && !ResultPopup.EnsureOnlineLookupConsent())
        {
            StatusText.Text = "已拒绝在线查询授权，无法保存联网脚本";
            return;
        }

        var sample = string.IsNullOrEmpty(SampleBox.Text) ? "test" : SampleBox.Text;
        using var cts = new CancellationTokenSource(JsScriptRunner.NetworkTimeout);
        var run = await _runner.RunAsync(CodeBox.Text, sample, allowNetwork, null, cts.Token);
        if (!run.Success)
        {
            StatusText.Text = run.Error ?? "试跑失败";
            return;
        }

        Action.AllowNetwork = allowNetwork;
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
        if (_loading) return;
        if (AllowNetworkCheck.IsChecked == true)
        {
            // 联网试跑防抖：否则每敲一个字符都会真的发一次请求。
            _previewDebounce.Stop();
            _previewDebounce.Start();
            return;
        }
        _previewDebounce.Stop();
        _ = PreviewAsync();
    }

    /// <summary>勾选/取消「允许访问网络」：刷新提示文案，并在勾选时先问一次在线查询授权。</summary>
    private void AllowNetwork_Changed(object sender, RoutedEventArgs e)
    {
        UpdateSandboxHint();
        if (_loading) return; // 构造期的赋值不弹同意框
        if (AllowNetworkCheck.IsChecked == true && !SettingsManager.Current.AllowOnlineLookups)
            ResultPopup.EnsureOnlineLookupConsent(); // 用户显式开启，问一次；拒绝后自动试跑会静默跳过
        Preview();
    }

    private void UpdateSandboxHint()
    {
        var network = AllowNetworkCheck.IsChecked == true;
        NetworkHintText.Visibility = network ? Visibility.Visible : Visibility.Collapsed;
        NetworkHintText.Text =  "勾选后沙箱会注入 await http.get/post(url, options)（返回 {status, ok, headers, body}）。网络请求受白名单、大小与次数限制：单请求 ≤8 秒、响应 ≤256KB、单次运行 ≤5 个请求。";
        SandboxHintText.Text = network
            ? "已允许网络：脚本可用 http 发起请求（受白名单、大小与次数限制），仍无法访问文件/剪贴板。"
            : "注意：脚本无法访问文件/网络/剪贴板；单次执行限时 2 秒，输出上限 128K 字符。";
    }

    private async Task PreviewAsync()
    {
        UpdateTriggerHint();
        if (string.IsNullOrWhiteSpace(CodeBox.Text))
        {
            PreviewBox.Text = "";
            LogsBox.Text = "";
            StatusText.Text = "";
            return;
        }

        var allowNetwork = AllowNetworkCheck.IsChecked == true;
        if (allowNetwork && !SettingsManager.Current.AllowOnlineLookups)
        {
            // 未授权在线查询：自动试跑直接跳过，避免每个防抖周期都弹一次同意框（保存前会再问一次）。
            PreviewBox.Text = "";
            LogsBox.Text = "";
            StatusText.Foreground = Brushes.OrangeRed;
            StatusText.Text = "未授权在线查询，已跳过联网试跑（保存时会再询问）";
            return;
        }

        var logLines = new List<string>();
        using var cts = new CancellationTokenSource(JsScriptRunner.NetworkTimeout);
        // 试跑用独立 runner（独立引擎缓存），不碰生产共享实例的状态；沙箱按运行时同一套规则注入。
        var run = await _runner.RunAsync(CodeBox.Text, SampleBox.Text, allowNetwork, logLines, cts.Token);
        if (run.Success)
        {
            PreviewBox.Text = run.Text ?? "";
            StatusText.Foreground = (Brush)FindResource("TextFillColorPrimaryBrush");
            StatusText.Text = "试跑成功";
        }
        else
        {
            PreviewBox.Text = "";
            StatusText.Foreground = Brushes.OrangeRed;
            StatusText.Text = run.Error ?? "试跑失败";
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

    [GeneratedRegex(@"function\s*JSAction\s*\([\s\S]*\)\s*\{[\s\S]*\}")]
    private static partial Regex FunctionMatch();
}