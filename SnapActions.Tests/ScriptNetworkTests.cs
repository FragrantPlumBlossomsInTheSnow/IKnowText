using System.Net;
using System.Net.Http;
using System.Text.Json;
using SnapActions.Actions.UserActions;
using SnapActions.Config;
using SnapActions.Services;
using Xunit;

namespace SnapActions.Tests;

/// <summary>
/// 联网脚本能力的回归测试：HTTP 桥的安全边界、自定义翻译服务商的模板/取值、以及沙箱注入开关。
/// 全部用 stub HttpMessageHandler，不触真实网络。
/// </summary>
public class ScriptNetworkTests
{
    private static string Request(string method, string url, string body = "", string options = "{}") =>
        JsonSerializer.Serialize(new { method, url, body, options = JsonDocument.Parse(options).RootElement });

    // ── ScriptHttpBridge：安全边界 ───────────────────────────────────────────────

    [Theory]
    [InlineData("file:///c:/windows/win.ini")]
    [InlineData("ftp://example.com/x")]
    [InlineData("not-a-url")]
    public async Task BridgeRejectsNonHttpSchemes(string url)
    {
        var bridge = new ScriptHttpBridge(CancellationToken.None, new HttpClient(new CapturingHandler("x")));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => bridge.SendAsync(Request("GET", url)));
        Assert.Contains("http/https", ex.Message);
    }

    [Theory]
    [InlineData("http://127.0.0.1/x")]
    [InlineData("http://localhost:8080/x")]
    [InlineData("http://10.1.2.3/x")]
    [InlineData("http://192.168.0.9/x")]
    [InlineData("http://169.254.1.1/x")]
    [InlineData("http://172.16.5.4/x")]
    [InlineData("http://[::1]/x")]
    public async Task BridgeRejectsLoopbackAndPrivateHosts(string url)
    {
        var bridge = new ScriptHttpBridge(CancellationToken.None, new HttpClient(new CapturingHandler("x")));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => bridge.SendAsync(Request("GET", url)));
        Assert.Contains("回环/内网", ex.Message);
    }

    [Theory]
    [InlineData("Host")]
    [InlineData("Content-Length")]
    [InlineData("Transfer-Encoding")]
    public async Task BridgeDeniesManagedRequestHeaders(string header)
    {
        var bridge = new ScriptHttpBridge(CancellationToken.None, new HttpClient(new CapturingHandler("x")));
        var options = $"{{\"headers\":{{\"{header}\":\"1\"}}}}";
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => bridge.SendAsync(Request("GET", "https://api.test/x", options: options)));
        Assert.Contains("请求头被拒绝", ex.Message);
    }

    [Fact]
    public async Task BridgeReturnsStatusOkHeadersAndBody()
    {
        var handler = new CapturingHandler("hello", HttpStatusCode.OK);
        var bridge = new ScriptHttpBridge(CancellationToken.None, new HttpClient(handler));
        var json = await bridge.SendAsync(Request("GET", "https://api.test/x"));
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(200, doc.RootElement.GetProperty("status").GetInt32());
        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("hello", doc.RootElement.GetProperty("body").GetString());
        Assert.Equal(HttpMethod.Get, handler.LastMethod);
    }

    [Fact]
    public async Task BridgeSurfacesNonSuccessStatusWithoutThrowing()
    {
        var bridge = new ScriptHttpBridge(CancellationToken.None,
            new HttpClient(new CapturingHandler("nope", HttpStatusCode.NotFound)));
        var json = await bridge.SendAsync(Request("GET", "https://api.test/x"));
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(404, doc.RootElement.GetProperty("status").GetInt32());
        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task BridgeSendsPostBody()
    {
        var handler = new CapturingHandler("ok");
        var bridge = new ScriptHttpBridge(CancellationToken.None, new HttpClient(handler));
        await bridge.SendAsync(Request("POST", "https://api.test/x", body: "{\"q\":\"hi\"}",
            options: "{\"headers\":{\"Content-Type\":\"application/json\"}}"));
        Assert.Equal(HttpMethod.Post, handler.LastMethod);
        Assert.Equal("{\"q\":\"hi\"}", handler.LastBody);
    }

    [Fact]
    public async Task BridgeCapsRequestsPerRun()
    {
        var bridge = new ScriptHttpBridge(CancellationToken.None, new HttpClient(new CapturingHandler("x")));
        for (var i = 0; i < ScriptHttpBridge.MaxRequestsPerRun; i++)
            _ = bridge.SendAsync(Request("GET", "https://api.test/x"));
        // 计数超限是同步抛出的，用 async lambda 把异常收进 Task 以便 ThrowsAsync 捕获。
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await bridge.SendAsync(Request("GET", "https://api.test/x")));
        Assert.Contains("最多发起", ex.Message);
    }

    // ── 自定义翻译引擎（JS 脚本）：校验与默认引擎解析 ─────────────────────────────

    [Fact]
    public void ValidatorClearsSelectionWhenEngineIsMissingOrDisabled()
    {
        var settings = new AppSettings
        {
            TranslationEngines =
            [
                new UserAction { Id = "e1", Name = "E1", Code = "function JSAction(t){return t;}", Enabled = true },
                new UserAction { Id = "e2", Name = "E2", Code = "function JSAction(t){return t;}", Enabled = false },
            ],
            SelectedTranslationEngineId = "e2",
        };
        SettingsValidator.Normalize(settings);
        Assert.Equal("", settings.SelectedTranslationEngineId); // 选中的引擎被禁用 → 清空（回退百度）

        settings.SelectedTranslationEngineId = "e1";
        SettingsValidator.Normalize(settings);
        Assert.Equal("e1", settings.SelectedTranslationEngineId);

        settings.SelectedTranslationEngineId = "missing";
        SettingsValidator.Normalize(settings);
        Assert.Equal("", settings.SelectedTranslationEngineId);
    }

    [Fact]
    public void ValidatorDropsEnginesWithoutAnyScript()
    {
        var settings = new AppSettings
        {
            TranslationEngines =
            [
                new UserAction { Id = "e1", Name = "E1" },                       // 无脚本 → 丢弃
                new UserAction { Id = "e2", Name = "E2", ScriptFile = "e2.js" }, // 有脚本文件 → 保留
            ],
        };
        SettingsValidator.Normalize(settings);
        Assert.Single(settings.TranslationEngines);
        Assert.Equal("e2", settings.TranslationEngines[0].Id);
    }

    // ── JsScriptRunner：沙箱注入开关 ─────────────────────────────────────────────

    [Fact]
    public async Task HttpIsUndefinedWhenNetworkNotAllowed()
    {
        var runner = new JsScriptRunner();
        var run = await runner.RunAsync(
            "function JSAction(text) { return typeof http; }",
            "x", allowNetwork: false);
        Assert.True(run.Success);
        Assert.Equal("undefined", run.Text);
    }

    [Fact]
    public async Task HttpIsInjectedWhenNetworkAllowed()
    {
        var runner = new JsScriptRunner();
        var run = await runner.RunAsync(
            "function JSAction(text) { return typeof http + ':' + typeof Translation; }",
            "x", allowNetwork: true);
        Assert.True(run.Success);
        Assert.Equal("object:undefined", run.Text);   // ← http 注入，Translation 已移除
    }

    [Fact]
    public async Task NetworkScriptStillReturnsTransformResult()
    {
        var runner = new JsScriptRunner();
        var run = await runner.RunAsync(
            "function JSAction(text) { return text.toUpperCase(); }",
            "abc", allowNetwork: true);
        Assert.True(run.Success);
        Assert.Equal("ABC", run.Text);
    }

    [Fact]
    public async Task EngineSandboxExposesHttpAndLanguages()
    {
        var runner = new JsScriptRunner();
        var sandbox = new JsScriptRunner.NetworkSandbox("en", "zh");
        var run = await runner.RunAsync(
            "function Translate(text) { return typeof http + ':' + typeof Translation + ':' + SNAP_SOURCE_LANGUAGE + '>' + SNAP_TARGET_LANGUAGE; }",
            "x", allowNetwork: true, logs: null, ct: CancellationToken.None, sandbox: sandbox, entryPoint: "Translate");
        Assert.True(run.Success);
        Assert.Equal("object:undefined:en>zh", run.Text);
    }

    // ── 翻译引擎服务：设置里的引擎脚本 → 译文 的整条链路（不发真实网络请求） ──────────────

    [Fact]
    public async Task EngineServiceRunsEngineScriptWithLanguageContext()
    {
        var engine = new UserAction
        {
            Id = "engine-svc",
            Name = "E",
            Code = "function Translate(text) { return SNAP_SOURCE_LANGUAGE + '>' + SNAP_TARGET_LANGUAGE + ':' + text.toUpperCase(); }",
        };
        var translated = await TranslationEngineService.RunAsync(engine, "hello", "en", "zh");
        Assert.Equal("en>zh:HELLO", translated);
    }

    [Fact]
    public async Task EngineServiceReportsMissingScriptFile()
    {
        var engine = new UserAction { Id = "engine-missing", Name = "M", ScriptFile = "engine-missing.js" };
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => TranslationEngineService.RunAsync(engine, "hello", "en", "zh"));
        Assert.Contains("脚本文件缺失", ex.Message);
    }

    internal sealed class CapturingHandler(string body, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public Uri? LastUri { get; private set; }
        public HttpMethod? LastMethod { get; private set; }
        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastUri = request.RequestUri;
            LastMethod = request.Method;
            if (request.Content != null) LastBody = await request.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(status) { Content = new StringContent(body) };
        }
    }
}