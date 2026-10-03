using System.IO;
using System.Net.Http;
using System.Text;

namespace SnapActions.Services;

internal static class BoundedHttp
{
    internal const int MaxResponseBytes = 256 * 1024;

    /// <summary>发送已构造好的请求并读取受限响应体。与 <see cref="GetStringAsync"/> 不同，本方法
    /// <b>不</b>对非 2xx 抛错——状态码原样交回调用方自行处理（脚本桥要把它暴露给脚本）。
    /// 响应体沿用同一套边界：Content-Length 预检 + 读取期 8 秒超时 + 逐块累计上限。
    /// 响应头在返回前物化（响应会被释放，不能把 HttpResponseMessage 交出去）。</summary>
    internal static async Task<(System.Net.HttpStatusCode Status, Dictionary<string, string> Headers, string Body)>
        SendBoundedAsync(HttpClient http, HttpRequestMessage request, CancellationToken ct, int maxBytes = MaxResponseBytes)
    {
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.Content.Headers.ContentLength > maxBytes)
            throw new InvalidDataException("The response is too large to display safely.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
        using var body = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(buffer, timeout.Token)) != 0)
        {
            if (body.Length + count > maxBytes)
                throw new InvalidDataException("The response is too large to display safely.");
            body.Write(buffer, 0, count);
        }
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in response.Headers) headers[h.Key] = string.Join(", ", h.Value);
        foreach (var h in response.Content.Headers) headers[h.Key] = string.Join(", ", h.Value);
        return (response.StatusCode, headers, new UTF8Encoding(false, true).GetString(body.GetBuffer(), 0, (int)body.Length));
    }

    internal static async Task<string> GetStringAsync(HttpClient http, string url, CancellationToken ct,
        int maxBytes = MaxResponseBytes)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > maxBytes)
            throw new InvalidDataException("The response is too large to display safely.");
        // HttpClient.Timeout only covers headers with ResponseHeadersRead; bound the body too.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
        using var body = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(buffer, timeout.Token)) != 0)
        {
            if (body.Length + count > maxBytes)
                throw new InvalidDataException("The response is too large to display safely.");
            body.Write(buffer, 0, count);
        }
        return new UTF8Encoding(false, true).GetString(body.GetBuffer(), 0, (int)body.Length);
    }
}
