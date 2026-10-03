using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace SnapActions.Services;

/// <summary>
/// JS 沙箱的通用 HTTP 桥。仅在脚本动作勾选「允许访问网络」时由 <see cref="Actions.UserActions.JsScriptRunner"/>
/// 注入。宿主函数刻意做成「一个 JSON 字符串进、一个 JSON 字符串出」：Jint 在 Interop 关闭时能可靠
/// 投递/回收字符串，而把 CLR 对象放进沙箱会把对象成员暴露给脚本（spike [F] 实测 Interop 关闭也不拦）。
/// 安全边界：scheme 白名单、危险请求头黑名单、回环/内网地址拒绝、响应 ≤256KB、单请求 ≤8s、
/// 单次运行 ≤<see cref="MaxRequestsPerRun"/> 个请求且总耗时 ≤<see cref="TotalBudget"/>、
/// 不自动重定向、不带默认凭据。所有宿主函数都是纯逻辑，不碰 UI（await 续体实测跑在线程池线程）。
/// </summary>
internal sealed class ScriptHttpBridge
{
    internal const int MaxRequestsPerRun = 5;
    internal static readonly TimeSpan TotalBudget = TimeSpan.FromSeconds(20);
    private const int MaxUrlChars = 4096;
    private const int MaxBodyChars = 256 * 1024;
    private const int MinTimeoutMs = 100;
    private const int MaxTimeoutMs = 20_000;
    private const int MaxResponseHeaders = 50;

    private static readonly HttpClient SharedHttp = new(new HttpClientHandler
    {
        AllowAutoRedirect = false,       // 3xx 原样交回脚本自行决定
        UseDefaultCredentials = false,   // 绝不自动附带 Windows 凭据
        AutomaticDecompression = DecompressionMethods.None,
    })
    { Timeout = TimeSpan.FromSeconds(8) };

    private static readonly HashSet<string> DeniedHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Host", "Content-Length", "Transfer-Encoding", "Connection", "Proxy-Connection",
        "Proxy-Authorization", "Upgrade", "TE", "Trailer", "Expect",
    };

    private readonly HttpClient _http;
    private readonly CancellationToken _runToken;
    private readonly Stopwatch _elapsed = Stopwatch.StartNew();
    private int _requests;

    internal ScriptHttpBridge(CancellationToken runToken, HttpClient? http = null)
    {
        _runToken = runToken;
        _http = http ?? SharedHttp;
    }

    /// <summary>宿主委托本体。抛出的异常由 Jint 转成 JS 异常（脚本可 try/catch，未捕获则 Runner 报「脚本错误」）。</summary>
    internal Task<string> SendAsync(string requestJson)
    {
        if (++_requests > MaxRequestsPerRun)
            throw new InvalidOperationException($"单次脚本运行最多发起 {MaxRequestsPerRun} 个网络请求");
        if (_elapsed.Elapsed > TotalBudget)
            throw new InvalidOperationException($"单次脚本运行的网络总耗时超过 {TotalBudget.TotalSeconds:0} 秒");
        return SendCoreAsync(requestJson);
    }

    private async Task<string> SendCoreAsync(string requestJson)
    {
        string method, url, body, optionsJson;
        try
        {
            using var doc = JsonDocument.Parse(requestJson);
            var root = doc.RootElement;
            method = ReadString(root, "method").Trim().ToUpperInvariant();
            url = ReadString(root, "url").Trim();
            body = ReadString(root, "body");
            optionsJson = root.TryGetProperty("options", out var o) && o.ValueKind == JsonValueKind.Object
                ? o.GetRawText() : "{}";
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("网络请求参数无法解析：" + ex.Message);
        }

        if (method is not ("GET" or "POST")) throw new InvalidOperationException($"不支持的 HTTP 方法：{method}");
        if (url.Length == 0) throw new InvalidOperationException("URL 为空");
        if (url.Length > MaxUrlChars) throw new InvalidOperationException("URL 过长");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new InvalidOperationException("只允许 http/https 的绝对 URL");
        if (IsBlockedHost(uri)) throw new InvalidOperationException("已阻止回环/内网地址");

        var (headers, timeoutMs) = ParseOptions(optionsJson);
        using var request = new HttpRequestMessage(method == "POST" ? HttpMethod.Post : HttpMethod.Get, uri);
        if (method == "POST")
        {
            if (body.Length > MaxBodyChars) throw new InvalidOperationException("请求体过长");
            var contentType = headers.TryGetValue("Content-Type", out var ctValue) && ctValue.Length > 0
                ? ctValue : "text/plain";
            request.Content = new StringContent(body, new UTF8Encoding(false), contentType);
        }
        foreach (var (name, value) in headers)
        {
            if (DeniedHeaders.Contains(name)) throw new InvalidOperationException($"请求头被拒绝：{name}");
            if (name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)) continue; // 由 content 携带
            if (!request.Headers.TryAddWithoutValidation(name, value))
                throw new InvalidOperationException($"请求头无效：{name}");
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_runToken);
        cts.CancelAfter(timeoutMs);
        var (status, responseHeaders, responseBody) = await BoundedHttp.SendBoundedAsync(_http, request, cts.Token);
        return BuildResponseJson(status, responseHeaders, responseBody);
    }

    private static string BuildResponseJson(HttpStatusCode status, Dictionary<string, string> headers, string body)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("status", (int)status);
            writer.WriteBoolean("ok", (int)status is >= 200 and < 300);
            writer.WritePropertyName("headers");
            writer.WriteStartObject();
            var count = 0;
            foreach (var (name, value) in headers)
            {
                if (count++ >= MaxResponseHeaders) break;
                writer.WriteString(name, value);
            }
            writer.WriteEndObject();
            writer.WriteString("body", body);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static (Dictionary<string, string> Headers, int TimeoutMs) ParseOptions(string optionsJson)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var timeoutMs = MaxTimeoutMs;
        try
        {
            using var doc = JsonDocument.Parse(optionsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return (headers, timeoutMs);
            if (doc.RootElement.TryGetProperty("headers", out var h) && h.ValueKind == JsonValueKind.Object)
                foreach (var prop in h.EnumerateObject())
                {
                    if (prop.Value.ValueKind is not (JsonValueKind.String or JsonValueKind.Number)) continue;
                    var name = prop.Name.Trim();
                    if (name.Length == 0 || name.Length > 200) continue;
                    headers[name] = prop.Value.ValueKind == JsonValueKind.String
                        ? prop.Value.GetString() ?? "" : prop.Value.GetRawText();
                }
            if (doc.RootElement.TryGetProperty("timeoutMs", out var t) && t.ValueKind == JsonValueKind.Number
                && t.TryGetInt32(out var ms))
                timeoutMs = Math.Clamp(ms, MinTimeoutMs, MaxTimeoutMs);
        }
        catch (JsonException)
        {
            // 选项不可解析时按默认处理：脚本传了坏 options 不该让请求失败，交由 URL/scheme 校验兜底。
        }
        return (headers, timeoutMs);
    }

    private static string ReadString(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? "" : "";

    /// <summary>回环/内网地址拒绝（SSRF 加固）。只做字面判定（IP 字面量与常见内网主机名），
    /// 不做 DNS 解析——那既慢又挡不住 TOCTOU，对「用户自己写的脚本」收益不成比例。</summary>
    private static bool IsBlockedHost(Uri uri)
    {
        var host = uri.DnsSafeHost;
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return true;
        if (host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)) return true;
        if (host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)) return true;
        if (!IPAddress.TryParse(host, out var ip)) return false;
        if (IPAddress.IsLoopback(ip)) return true;
        var bytes = ip.GetAddressBytes();
        if (ip.AddressFamily == AddressFamily.InterNetwork)
            return bytes[0] == 0 || bytes[0] == 10 || bytes[0] == 127
                || (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
                || (bytes[0] == 192 && bytes[1] == 168)
                || (bytes[0] == 169 && bytes[1] == 254);
        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
            return ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || (bytes[0] & 0xFE) == 0xFC;
        return false;
    }
}