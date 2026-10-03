using SnapActions.Detection;
using SnapActions.Helpers;
using SnapActions.UI;

namespace SnapActions.Actions.ContextActions;

public class TranslateAction : IAction
{
    public string Id => "translate";
    public string Name => "Translate";
    public string IconKey => "";
    public ActionCategory Category => ActionCategory.Context;

    // PlainText only — URLs, JSON, UUIDs, JWTs etc. aren't translatable prose, and offering
    // Translate for every short selection just crowded the toolbar for typed selections.
    // (Dictionary applies the same gate.)
    // 长度门槛只在走百度时生效：选了自定义翻译引擎就交给引擎自己判断（它可能能处理任意长度）。
    public bool CanExecute(string text, TextAnalysis analysis) =>
        !string.IsNullOrWhiteSpace(text)
        && analysis.Type == TextType.PlainText
        && (Services.TranslationEngineService.HasSelectedEngine() || Services.BaiduTranslator.CanTranslate(text));

    public ActionResult Execute(string text, TextAnalysis analysis)
    {
        // 打开工具栏上的翻译弹层（不再创建独立窗口）；工具栏保持可见，翻译完成后由
        // 用户手动关闭（外部点击/Esc/关闭按钮）。
        ToolbarWindow.Current?.ShowTranslate(TranslationTextHelper.PreprocessText(text));
        return new ActionResult(true) { KeepToolbarOpen = true };
    }
}
