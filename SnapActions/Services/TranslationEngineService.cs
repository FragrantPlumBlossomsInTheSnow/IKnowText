using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SnapActions.Actions.UserActions;
using SnapActions.Config;

namespace SnapActions.Services;

/// <summary>
/// 自定义翻译引擎：设置里维护的一个 JS 脚本列表（与「自定义 JS 脚本动作」同构，入口 <c>Translate(text)</c>， 返回值即译文）。
/// 未选中/未启用时由调用方回退百度翻译。
/// 引擎脚本以联网沙箱运行（可用 await http.get/post），
/// 语言上下文通过全局变量 SNAP_SOURCE_LANGUAGE / SNAP_TARGET_LANGUAGE 提供。
/// </summary>
internal static class TranslationEngineService
{
    /// <summary>当前选中的引擎；未选中、已被禁用或找不到时返回 null（= 用百度）。</summary>
    internal static UserAction? SelectedEngine()
    {
        var settings = SettingsManager.Current;
        if (string.IsNullOrEmpty(settings.SelectedTranslationEngineId)) return null;
        return settings.TranslationEngines.FirstOrDefault(
            e => e.Enabled && e.Id == settings.SelectedTranslationEngineId);
    }

    internal static bool HasSelectedEngine() => SelectedEngine() != null;

    /// <summary>执行选中的引擎；没有选中引擎时抛出可读异常（调用方应先判断，以便回退百度）。</summary>
    internal static async Task<string> RunSelectedAsync(string text, string source, string target,
        CancellationToken ct = default)
    {
        var engine = SelectedEngine()
            ?? throw new InvalidOperationException("未选择自定义翻译引擎（设置 → 自定义翻译）");
        return await RunAsync(engine, text, source, target, ct);
    }

    /// <summary>执行指定引擎的 Translate(text)。脚本缺失或执行失败都抛出可读的中文异常。</summary>
    internal static async Task<string> RunAsync(UserAction engine, string text, string source, string target,
        CancellationToken ct = default)
    {
        var code = ScriptActionStorage.LoadCode(engine);
        if (code == null) throw new InvalidOperationException("翻译引擎脚本文件缺失：" + engine.ScriptFile);
        var sandbox = new JsScriptRunner.NetworkSandbox(source, target);
        var run = await JsScriptRunner.Shared.RunAsync(code, text, allowNetwork: true, logs: null, ct, sandbox, entryPoint: "Translate");
        if (!run.Success) throw new InvalidOperationException(run.Error ?? "翻译引擎执行失败");
        return run.Text ?? "";
    }
}