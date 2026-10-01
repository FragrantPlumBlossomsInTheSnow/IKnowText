using SnapActions.Services;
using Xunit;

namespace SnapActions.Tests;

public class BaiduTranslatorTests
{
    [Fact]
    public void SelectionLimitCountsUtf8Bytes()
    {
        Assert.True(BaiduTranslator.CanTranslate(new string('a', 500)));
        Assert.False(BaiduTranslator.CanTranslate(new string('a', 501)));
        Assert.True(BaiduTranslator.CanTranslate(new string('ش', 250)));
        Assert.False(BaiduTranslator.CanTranslate(new string('ش', 251)));
        Assert.False(BaiduTranslator.CanTranslate(" \r\n"));
    }

    [Theory]
    [InlineData("zh-CN", "zh")]
    [InlineData("zh-TW", "cht")]
    [InlineData("en", "en")]
    [InlineData("ar", "ara")]
    [InlineData("pt-BR", "pt")]
    [InlineData("ja", "jp")]
    [InlineData("ko", "kor")]
    public void LanguageCodesMapToBaidu(string snap, string expected) =>
        Assert.Equal(expected, BaiduTranslator.BaiduLanguage(snap));

    [Fact]
    public void UnknownLanguageCodePassesThroughLowercased()
    {
        Assert.Equal("xx", BaiduTranslator.BaiduLanguage("XX"));
        Assert.Equal("", BaiduTranslator.BaiduLanguage(""));
    }

    [Fact]
    public void SignIsLowercaseMd5OfConcat()
    {
        // MD5("abcd") is the well-known e2fc714c... — pins the exact concat order a+b+c+d.
        Assert.Equal("e2fc714c4727ee9395f324cd2e7f331f", BaiduTranslator.BuildSign("a", "b", "c", "d"));
    }
}