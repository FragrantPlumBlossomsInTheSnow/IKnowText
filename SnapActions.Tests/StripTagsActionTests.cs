using SnapActions.Actions;
using SnapActions.Actions.ContextActions;
using SnapActions.Detection;
using Xunit;

namespace SnapActions.Tests;

/// <summary>
/// StripTagsAction：基于正则的标签剥离（非 HTML 解析器）。
/// 只删标签本身、不做实体解码，因此这里同时钉住"保留了哪些"的行为。
/// </summary>
public class StripTagsActionTests
{
    private static ActionResult Run(string text) =>
        new StripTagsAction().Execute(text, new TextClassifier().Classify(text));

    [Fact]
    public void CanExecute_OnlyForXmlHtml()
    {
        var action = new StripTagsAction();
        Assert.True(action.CanExecute("<b>x</b>", new TextClassifier().Classify("<b>x</b>")));
        Assert.False(action.CanExecute("just plain text", TextAnalysis.PlainText));
    }

    [Fact]
    public void Execute_RemovesTagsWithAttributes()
    {
        var result = Run(@"<a href=""https://example.com"">link</a>");
        Assert.True(result.Success);
        Assert.Equal("link", result.ResultText);
        Assert.Equal("Tags stripped", result.Message);
    }

    [Fact]
    public void Execute_KeepsTextOfNestedAndAdjacentTags()
    {
        Assert.Equal("bold and italic", Run("<div><b>bold</b> and <i>italic</i></div>").ResultText);
    }

    [Fact]
    public void Execute_CollapsesWhitespaceAndTrim()
    {
        // 行内/换行空白统一压成单空格，整体 trim（换行被吃掉是既有行为）。
        Assert.Equal("a b c", Run("<p>a</p>\n  <p>b</p>\r\n<p>c</p>").ResultText);
        Assert.Equal("x", Run("<b>  x  </b>").ResultText);
    }

    [Fact]
    public void Execute_EmptyWhenInputIsAllTags()
    {
        Assert.Equal("", Run("<br/><hr/>").ResultText);
        Assert.Equal("", Run("<div></div>").ResultText);
    }

    [Fact]
    public void Execute_DoesNotDecodeHtmlEntities()
    {
        // 正则只删标签，不解析实体：&amp; / &lt; 原样保留。
        Assert.Equal("a &amp; b", Run("<b>a &amp; b</b>").ResultText);
        Assert.Equal("1 &lt; 2", Run("<p>1 &lt; 2</p>").ResultText);
    }

    [Fact]
    public void Execute_KeepsScriptAndStyleBodyText()
    {
        // 只剥标签、不删内容：script/style 的正文会留下（与 HTML 解析器行为不同）。
        Assert.Equal("alert(1)", Run("<script>alert(1)</script>").ResultText);
        Assert.Equal("p { color: red }", Run("<style>p { color: red }</style>").ResultText);
    }
}
