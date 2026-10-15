using SnapActions.Config;
using SnapActions.Helpers;
using Xunit;

namespace SnapActions.Tests;

/// <summary>
/// TranslationTextHelper：翻译前的文本预处理。两块行为互相独立——
/// NormalizeEnglishIdentifiers 是纯函数；UserPreprocessingRegex 读取进程级单例
/// SettingsManager.Current.ExcludeRegex，因此本类必须加入禁用并行化的集合。
/// </summary>
[Collection("settings-singleton")]
public class TranslationTextHelperTests
{
    // ── NormalizeEnglishIdentifiers ────────────────────────────────

    [Theory]
    [InlineData("hello_world_foo", "hello world foo")] // snake_case
    [InlineData("hello-world", "hello world")]         // kebab-case（字母-字母）
    [InlineData("helloWorld", "hello World")]          // camelCase
    [InlineData("HelloWorld", "Hello World")]          // PascalCase
    [InlineData("getUserID", "get User ID")]           // 连续大写词尾
    [InlineData("XMLHttpRequest", "XML Http Request")] // 连续大写后接小写
    [InlineData("URLParser", "URL Parser")]
    [InlineData("user2Name", "user 2 Name")]           // 字母 ↔ 数字边界
    [InlineData("hello world", "hello world")]         // 普通句子不受影响
    [InlineData("The quick brown fox.", "The quick brown fox.")]
    [InlineData("COVID-19", "COVID-19")]               // 字母-数字连字符不拆
    [InlineData("2024-10-06", "2024-10-06")]           // 日期不拆
    [InlineData("10-20", "10-20")]                     // 数字区间不拆
    public void Normalize_SplitsIdentifiersAndLeavesProseAlone(string input, string expected) =>
        Assert.Equal(expected, TranslationTextHelper.NormalizeEnglishIdentifiers(input));

    [Theory]
    [InlineData("  hello_world  ", "hello world")] // 去首尾空白
    [InlineData("a\t\tb", "a b")]                  // 行内连续空白/Tab 合并为单空格
    [InlineData("a  \t b", "a b")]
    [InlineData("hello  world", "hello world")]
    public void Normalize_CollapsesHorizontalWhitespace(string input, string expected) =>
        Assert.Equal(expected, TranslationTextHelper.NormalizeEnglishIdentifiers(input));

    [Fact]
    public void Normalize_KeepsLineBreaks()
    {
        // 行内空白合并只作用于 [ \t]，段落换行结构保持不变。
        Assert.Equal("foo\r\nbar\nbaz", TranslationTextHelper.NormalizeEnglishIdentifiers("foo\r\nbar\nbaz"));
    }

    [Fact]
    public void Normalize_NullOrEmpty_ReturnsAsIs()
    {
        Assert.Null(TranslationTextHelper.NormalizeEnglishIdentifiers(null!));
        Assert.Equal("", TranslationTextHelper.NormalizeEnglishIdentifiers(""));
    }

    // ── UserPreprocessingRegex ─────────────────────────────────────

    /// <summary>临时替换 ExcludeRegex 后执行断言，结束恢复原值。</summary>
    private static string WithExcludeRegex(string pattern, Func<string> act)
    {
        var previous = SettingsManager.Current.ExcludeRegex;
        SettingsManager.Current.ExcludeRegex = pattern;
        try { return act(); }
        finally { SettingsManager.Current.ExcludeRegex = previous; }
    }

    [Fact]
    public void Preprocessing_EmptyPattern_ReturnsTextUnchanged() =>
        Assert.Equal("2024-10-06 abc", WithExcludeRegex("",
            () => TranslationTextHelper.UserPreprocessingRegex("2024-10-06 abc")));

    [Fact]
    public void Preprocessing_RemovesEveryMatch() =>
        Assert.Equal("abcdef", WithExcludeRegex(@"\d+",
            () => TranslationTextHelper.UserPreprocessingRegex("abc123def")));

    [Fact]
    public void Preprocessing_ChangedSettingTakesEffectImmediately()
    {
        // 正则每次调用时重建，改设置后下一次调用立即生效（不缓存）。
        Assert.Equal("xy", WithExcludeRegex("[0-9]+",
            () => TranslationTextHelper.UserPreprocessingRegex("x1y2")));
        Assert.Equal("x1y2", WithExcludeRegex("zzz",
            () => TranslationTextHelper.UserPreprocessingRegex("x1y2")));
    }

    [Fact]
    public void Preprocessing_InvalidPattern_FallsBackToRawText()
    {
        // 无效正则不应把整个翻译动作炸掉：忽略该配置，返回原文。
        Assert.Equal("abc (def", WithExcludeRegex("(",
            () => TranslationTextHelper.UserPreprocessingRegex("abc (def")));
    }

    [Fact]
    public void Preprocessing_CatastrophicBacktracking_TimesOutAndFallsBack()
    {
        // (a+)+$ 对不含匹配的长 a 串是指数级回溯；500ms 超时后放弃忽略、返回原文。
        var input = new string('a', 40) + "!";
        Assert.Equal(input, WithExcludeRegex("(a+)+$",
            () => TranslationTextHelper.UserPreprocessingRegex(input)));
    }

    [Fact]
    public void Preprocessing_NullOrEmptyText_ReturnsAsIs()
    {
        Assert.Null(TranslationTextHelper.UserPreprocessingRegex(null!));
        Assert.Equal("", TranslationTextHelper.UserPreprocessingRegex(""));
    }
}
