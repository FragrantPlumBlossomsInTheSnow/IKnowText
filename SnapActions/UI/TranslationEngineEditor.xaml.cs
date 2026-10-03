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
///     自定义翻译引擎编辑器（独立于「JS 脚本动作」编辑器 UserScriptEditor）：名称 + JSAction(text) 脚本 +
///     即时试跑预览/日志。引擎与脚本动作的差别只有两点，所以这里不再靠开关复用那个编辑器：
///     1) 引擎必然联网（翻译引擎的意义就是请求外部服务），没有「允许访问网络」勾选项，
///        试跑一律防抖 + 先过「允许在线查询」同意门；
///     2) 引擎沙箱不注入 <c>Translation</c>（否则引擎调用它会递归回自己），语言上下文由
///        <c>SNAP_SOURCE_LANGUAGE</c> / <c>SNAP_TARGET_LANGUAGE</c> 提供。
///     界面在 TranslationEngineEditor.xaml，本文件只放行为。试跑用独立 JsScriptRunner 实例，
///     不碰生产共享缓存；沙箱 console 的输出只在这里显示（实际翻译时静默）。
/// </summary>
public partial class TranslationEngineEditor : Window
{
    private readonly JsScriptRunner _runner = new();

    /// <summary>试跑一律联网，所以总是防抖，避免敲键即发请求。</summary>
    private readonly DispatcherTimer _previewDebounce = new() { Interval = TimeSpan.FromMilliseconds(600) };

    /// <summary>构造期间屏蔽事件触发的试跑（此时控件还没赋值完）。</summary>
    private bool _loading = true;

    internal TranslationEngineEditor(UserAction? source)
    {
        // 编辑时从独立脚本文件读回源码（ScriptFile 优先），旧数据回退内嵌 Code。
        var sourceCode = source == null ? DefaultEngineScript : ScriptActionStorage.LoadCode(source) ?? "";
        Action = new UserAction
        {
            Id = source?.Id ?? Guid.NewGuid().ToString("N"),
            Name = source?.Name ?? "",
            Code = sourceCode,
            ScriptFile = source?.ScriptFile ?? "",
            ContextRegex = source?.ContextRegex ?? "",
            AllowNetwork = true, // 引擎必然联网（TranslationEngineService 执行时也固定传 allowNetwork: true）
            Enabled = source?.Enabled ?? true
        };

        InitializeComponent();
        Title = source == null ? "创建翻译引擎" : "编辑翻译引擎";
        _previewDebounce.Tick += (_, _) => { _previewDebounce.Stop(); _ = PreviewAsync(); };
        Closed += (_, _) => _previewDebounce.Stop();
        NameBox.Text = Action.Name;
        // 默认试跑文本放在这里而不是 XAML：写在 XAML 会在 InitializeComponent 过程中触发 Sample_Changed。
        SampleBox.Text = "Hello, world!";
        CodeBox.Text = Action.Code; // 触发 Code_Changed → 防抖试跑
        _loading = false;
        Preview();
    }

    internal UserAction Action { get; }

    /// <summary>新建引擎时的起手脚本：调用翻译接口的骨架，默认原样返回，避免开编辑器就发请求。</summary>
    private const string DefaultEngineScript =
        "// 翻译引擎：调用任意翻译接口，返回值即译文。\n" +
        "// 可用：await http.get/post(url, options)\n" +
        "// SNAP_SOURCE_LANGUAGE = 获取设置的源语言\n" +
        "// SNAP_TARGET_LANGUAGE = 获取设置的目标语言。\n" +
        "async function JSAction(text) {\n" +
        "\t// TODO: 在这里调用你的翻译接口，例如：\n" +
        "\t// var resp = await http.post('https://api.example.com/translate',\n" +
        "\t//   JSON.stringify({ q: text, from: SNAP_SOURCE_LANGUAGE, to: SNAP_TARGET_LANGUAGE }),\n" +
        "\t//   { headers: { 'Content-Type': 'application/json' } });\n" +
        "\t// if (!resp.ok) throw new Error('HTTP ' + resp.status);\n" +
        "\t// return JSON.parse(resp.body).result;\n" +
        "\treturn text;\n" +
        "}";

    private void Code_Changed(object sender, TextChangedEventArgs e)
    {
        Preview();
    }

    private void Sample_Changed(object sender, TextChangedEventArgs e)
    {
        Preview();
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
            StatusText.Text = "请输入引擎名称";
            return;
        }

        if (string.IsNullOrWhiteSpace(CodeBox.Text))
        {
            StatusText.Text = "请输入脚本代码";
            return;
        }

        // 保存前会真的试跑一次（可能发请求），所以先过同意门。
        if (!ResultPopup.EnsureOnlineLookupConsent())
        {
            StatusText.Text = "已拒绝在线查询授权，无法保存联网的翻译引擎";
            return;
        }

        var sample = string.IsNullOrEmpty(SampleBox.Text) ? "test" : SampleBox.Text;
        using var cts = new CancellationTokenSource(JsScriptRunner.NetworkTimeout);
        var run = await _runner.RunAsync(CodeBox.Text, sample, allowNetwork: true, logs: null, cts.Token, EngineSandbox);
        if (!run.Success)
        {
            StatusText.Text = run.Error ?? "试跑失败";
            return;
        }

        Action.Name = NameBox.Text.Trim();
        Action.AllowNetwork = true;
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
        _previewDebounce.Stop();
        _previewDebounce.Start();
    }

    /// <summary>引擎沙箱：有 http/语言变量，但没有 Translation（见 TranslationEngineService 的同款设置）。</summary>
    private static JsScriptRunner.NetworkSandbox EngineSandbox => new("", "", ExposeTranslation: false);

    private async Task PreviewAsync()
    {
        if (string.IsNullOrWhiteSpace(CodeBox.Text))
        {
            PreviewBox.Text = "";
            LogsBox.Text = "";
            StatusText.Text = "";
            return;
        }

        if (!SettingsManager.Current.AllowOnlineLookups)
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
        // 试跑用独立 runner（独立引擎缓存），不碰生产共享实例的状态。
        var run = await _runner.RunAsync(CodeBox.Text, SampleBox.Text, allowNetwork: true, logLines, cts.Token, EngineSandbox);
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

    private void GenerateJSAction_Click(object sender, RoutedEventArgs e)
    {
        var reg = FunctionMatch();
        if (reg.IsMatch(CodeBox.Text)) return;
        var code = "async function JSAction(text) {\n\t// TODO: 调用翻译接口并返回译文\n\treturn text;\n}\n" + CodeBox.Text;
        CodeBox.Text = code;
    }

    [GeneratedRegex(@"function\s*JSAction\s*\([\s\S]*\)\s*\{[\s\S]*\}")]
    private static partial Regex FunctionMatch();
}
