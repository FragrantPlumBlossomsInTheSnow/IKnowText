using System.Net;
using System.Net.Http;
using System.Text;
using SnapActions.Actions;
using SnapActions.Actions.ContextActions;
using SnapActions.Detection;
using SnapActions.Services;
using Xunit;

namespace SnapActions.Tests;

public class RoadmapRegressionTests
{
    [Theory]
    [InlineData("100 BHD", "BHD")]
    [InlineData("100 QAR", "QAR")]
    [InlineData("100 OMR", "OMR")]
    public async Task AdvertisedCurrencyUsesItsOwnRates(string text, string code)
    {
        var handler = new StubHandler("{\"rates\":{\"EUR\":2}}");
        using var http = new HttpClient(handler);
        Assert.True(new CurrencyConverterAction().CanExecute(text, TextAnalysis.PlainText));
        var result = await new LookupService(http).ConvertCurrency(text, "EUR");
        Assert.Equal(LookupStatus.Success, result.Status);
        Assert.Contains(code, result.Text);
        Assert.EndsWith("/" + code, handler.LastUri!.AbsolutePath);
    }

    [Theory]
    [InlineData("enc_base64_decode", "%%%")]
    [InlineData("enc_hex_decode", "GG")]
    [InlineData("enc_base64_decode", "/w==")]
    [InlineData("enc_hex_decode", "ff")]
    public void InvalidDecodeNeverProducesAnEffect(string id, string text)
    {
        var action = new ActionRegistry().GetAllActionsForCategory(ActionCategory.Encode).Single(a => a.Id == id);
        var result = action.Execute(text, TextAnalysis.PlainText);
        Assert.False(result.Success);
        Assert.Null(result.ResultText);
    }

    [Fact]
    public void TranslationLimitCountsUtf8Bytes()
    {
        // 上限按 UTF-8 字节计（2000）：1000 个两字节字符正好 2000 字节，1001 个越界。
        string within = new('ش', 1000);
        Assert.Equal(2000, Encoding.UTF8.GetByteCount(within));
        Assert.True(new TranslateAction().CanExecute(within, TextAnalysis.PlainText));
        string over = new('ش', 1001);
        Assert.Equal(2002, Encoding.UTF8.GetByteCount(over));
        Assert.False(new TranslateAction().CanExecute(over, TextAnalysis.PlainText));
    }

    internal sealed class StubHandler(string body, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public Uri? LastUri { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }
}
