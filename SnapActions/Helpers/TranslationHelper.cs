using System.Text.RegularExpressions;
using SnapActions.Config;

namespace SnapActions.Helpers;

public static partial class TranslationTextHelper
{
    /// <summary>
    ///     Removes the configured "content ignore" regex matches before translation.
    ///     The regex is constructed per call so a changed setting takes effect immediately,
    ///     is guarded against invalid patterns (falls back to the raw text instead of crashing
    ///     the whole translate action) and is time-limited against catastrophic backtracking.
    /// </summary>
    public static string UserPreprocessingRegex(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var pattern = SettingsManager.Current.ExcludeRegex;
        if (string.IsNullOrEmpty(pattern)) return text;
        try
        {
            return new Regex(pattern, RegexOptions.None, TimeSpan.FromMilliseconds(500)).Replace(text, "");
        }
        catch (ArgumentException)
        {
            return text; // 无效正则表达式：忽略该配置，翻译照常进行
        }
        catch (RegexMatchTimeoutException)
        {
            return text; // 正则回溯超时：放弃忽略，避免卡死
        }
    }
    /// <summary>
    ///     把英文标识符风格的单词拆成带空格的普通写法，便于翻译：
    ///     camelCase / PascalCase → "camel Case" / "Pascal Case"，
    ///     snake_case → "snake case"，kebab-case → "kebab case"，
    ///     XMLHttp → "XML Http"，user2Name → "user 2 Name"。
    ///     普通英文句子（无下划线/连字符/驼峰边界）不受影响。
    /// </summary>
    public static string NormalizeEnglishIdentifiers(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;

        var s = text;

        // 1. snake_case：下划线替换为空格（标识符专用符号，无条件拆安全）
        s = s.Replace('_', ' ');

        // 2. kebab-case：仅"字母-字母"的连字符拆为空格，保留 10-20 / 2024-10-06 / COVID-19
        s = KebabBoundary().Replace(s, " ");

        // 3. camelCase / PascalCase：小写或数字 → 大写 的边界
        s = LowerUpperBoundary().Replace(s, " ");

        // 4. 连续大写后接小写：XMLHttp → XML Http
        s = AcronymBoundary().Replace(s, " ");

        // 5. 字母 ↔ 数字边界：user2Name → user 2 Name
        s = LetterDigitBoundary().Replace(s, " ");

        // 6. 合并行内多余空格（保留换行，段落结构不动）
        s = CollapseHorizontalWhitespace().Replace(s, " ");

        return s.Trim();
    }

// 只匹配"字母-字母"的连字符
    [GeneratedRegex(@"(?<=[a-zA-Z])-(?=[a-zA-Z])")]
    private static partial Regex KebabBoundary();

    // 小写/数字 后跟 大写："getUser" → "get User"
    [GeneratedRegex(@"(?<=[a-z0-9])(?=[A-Z])")]
    private static partial Regex LowerUpperBoundary();

    // 大写 后跟 大写+小写："XMLHttp" → "XML Http"
    [GeneratedRegex(@"(?<=[A-Z])(?=[A-Z][a-z])")]
    private static partial Regex AcronymBoundary();

    // 字母↔数字："user2Name" → "user 2 Name"
    [GeneratedRegex(@"(?<=[a-zA-Z])(?=\d)|(?<=\d)(?=[a-zA-Z])")]
    private static partial Regex LetterDigitBoundary();

    // 行内连续空格/Tab → 单空格（不碰 \r\n）
    [GeneratedRegex(@"[ \t]{2,}")]
    private static partial Regex CollapseHorizontalWhitespace();
    
}