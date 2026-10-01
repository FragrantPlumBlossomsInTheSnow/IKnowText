using System.Text.RegularExpressions;

namespace SnapActions.Actions.UserActions;

/// <summary>
/// 「上下文触发」正则的安全求值。模式为空、非法、或回溯超时一律按「不命中」处理（fail closed）——
/// 与 TranslationTextHelper 的「内容忽略正则」同一套思路：触发不了只是少一个内联按钮，绝不能让
/// 病态正则卡住工具栏弹出。工具栏每次弹出都要判一次，所以给匹配加了时间上界。
/// </summary>
internal static class ContextTriggerRegex
{
    /// <summary>单次匹配的时间上界。</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(200);

    /// <summary>模式是否可用；空模式算「未配置」也算可用。编辑器用它给出即时反馈、保存前校验。</summary>
    internal static bool IsValid(string? pattern)
    {
        if (string.IsNullOrEmpty(pattern)) return true;
        try { _ = new Regex(pattern, RegexOptions.None, Timeout); return true; }
        catch (ArgumentException) { return false; }
    }

    /// <summary>模式非空且命中 text。空模式/非法模式/超时/空文本都返回 false。</summary>
    internal static bool IsMatch(string? pattern, string? text)
    {
        if (string.IsNullOrEmpty(pattern) || string.IsNullOrEmpty(text)) return false;
        try { return Regex.IsMatch(text, pattern, RegexOptions.None, Timeout); }
        catch (ArgumentException) { return false; }          // 非法正则
        catch (RegexMatchTimeoutException) { return false; } // 病态回溯
    }
}
