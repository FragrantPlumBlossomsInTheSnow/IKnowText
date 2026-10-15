using SnapActions.Config;
using Xunit;

namespace SnapActions.Tests;

/// <summary>
/// LanguageOptions：翻译下拉支持的语言码白名单 + IsSupported 校验。
/// IsSupported 是大小写不敏感的（OrdinalIgnoreCase），且只认列表内的确切码。
/// </summary>
public class LanguageOptionsTests
{
    [Theory]
    [InlineData("en")]
    [InlineData("zh-CN")]
    [InlineData("zh-TW")]
    [InlineData("pt-BR")]
    [InlineData("ja")]
    [InlineData("ko")]
    public void IsSupported_AcceptsKnownCodes(string code) =>
        Assert.True(LanguageOptions.IsSupported(code));

    [Theory]
    [InlineData("EN")]      // 全大写
    [InlineData("En")]
    [InlineData("zh-cn")]   // 区域码小写
    [InlineData("ZH-TW")]
    [InlineData("PT-br")]
    public void IsSupported_IsCaseInsensitive(string code) =>
        Assert.True(LanguageOptions.IsSupported(code));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("xx")]
    [InlineData("zh")]      // 无区域码，不在列表
    [InlineData("pt")]      // 列表只有 pt-BR
    [InlineData("en-US")]   // 列表只有 en
    [InlineData("system")]  // "跟随系统"是独立伪码，不是语言码
    public void IsSupported_RejectsUnknownCodes(string? code) =>
        Assert.False(LanguageOptions.IsSupported(code));

    [Fact]
    public void IsSupported_AcceptsEveryListedCode()
    {
        // 列表自洽：All 里的每个码都必须能通过 IsSupported。
        Assert.All(LanguageOptions.All, option => Assert.True(LanguageOptions.IsSupported(option.Code)));
    }

    [Fact]
    public void All_HasNonEmptyUniqueCodesAndNames()
    {
        Assert.NotEmpty(LanguageOptions.All);
        Assert.All(LanguageOptions.All, option =>
        {
            Assert.False(string.IsNullOrWhiteSpace(option.Code));
            Assert.False(string.IsNullOrWhiteSpace(option.Name));
        });

        // 重复语言码会让下拉取值/回写错乱，必须唯一（忽略大小写）。
        var duplicates = LanguageOptions.All
            .GroupBy(o => o.Code, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToArray();
        Assert.Empty(duplicates);
    }
}
