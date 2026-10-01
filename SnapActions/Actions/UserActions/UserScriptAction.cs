using SnapActions.Config;
using SnapActions.Detection;

namespace SnapActions.Actions.UserActions;

/// <summary>
/// 用户 JS 脚本动作：Execute 时用 <see cref="JsScriptRunner"/> 执行脚本的 JSAction(text)，
/// 返回结果文本，走现有 ResultPopup 复制/替换管线。Category=Transform 与内置文本转换一致；
/// IsPreviewSafe=false（脚本不可信，悬停不执行）。
/// 沙箱 console 的输出不传 logs（静默丢弃），需要调试时在编辑器「试跑」里看。
/// </summary>
public sealed class UserScriptAction(UserAction def) : IAction  
{
    public string Id => $"user_{def.Id}";
    public string Name => def.Name;
    public string IconKey => "IconTransform";
    public ActionCategory Category => ActionCategory.Transform;

    public bool CanExecute(string text, TextAnalysis analysis)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        if (string.IsNullOrEmpty(def.AppliesToType)) return true; // applies to any selection
        return Enum.TryParse<TextType>(def.AppliesToType, ignoreCase: true, out var t) && analysis.Type == t;
    }

    /// <summary>
    ///     「上下文触发」：配了正则且命中当前选区时，注册表会把该动作同时放进 Context 组，
    ///     于是它像内置的数学计算/格式化 JSON 一样内联在工具栏 ContextSeparator 后面。
    ///     动作本身仍是 Transform 类别 —— 转换子菜单、固定区、调色板里的行为都不变。
    /// </summary>
    internal bool IsContextTriggered(string text) => ContextTriggerRegex.IsMatch(def.ContextRegex, text);

    public ActionResult Execute(string text, TextAnalysis analysis)
    {
        // 源码优先读独立脚本文件（新格式），文件缺失或未设置时回退内嵌 Code（旧数据兼容）。
        var code = ScriptActionStorage.LoadCode(def);
        if (code == null)
            return new ActionResult(false, Message: "脚本文件缺失：" + def.ScriptFile);
        if (JsScriptRunner.Shared.Run(code, text, out var result, out var error))
            return new ActionResult(true, result, Name);
        return new ActionResult(false, Message: error);
    }
}
