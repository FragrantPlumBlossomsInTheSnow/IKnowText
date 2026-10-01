using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SnapActions.Services;

/// <summary>
/// Baidu Translate open-platform API client. Uses the <c>nmt</c> (neural machine translation)
/// model. The generic translation endpoint needs no paid API key beyond the free-registration
/// AppID + secret; the request is signed with MD5(appid + q + salt + secret) per Baidu's spec.
/// Only a plain-text result is returned — never page content.
/// </summary>
internal static class BaiduTranslator
{
    private const string Endpoint = "https://fanyi-api.baidu.com/ait/api/aiTextTranslate";
    private const string ModelType = "nmt";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };

    // 500-byte cap matches the established API contract (unit tests pin 500/501 and 250/251
    // multibyte boundaries) — long selections were never the target use case for translate.
    internal static bool CanTranslate(string text) => !string.IsNullOrWhiteSpace(text)
        && Encoding.UTF8.GetByteCount(text) <= 500;

    /// <summary>Maps a SnapActions language code to Baidu's language code. Unknown/smaller
    /// language markers pass through lowercased and surface Baidu's error message if unsupported.</summary>
    internal static string BaiduLanguage(string? code) => code?.ToLowerInvariant() switch
    {
        "ar" => "ara",
        "es" => "spa",
        "fr" => "fra",
        "ja" => "jp",
        "ko" => "kor",
        "pt-br" => "pt",
        "zh-cn" => "zh",
        "zh-tw" => "cht",
        _ => code?.ToLowerInvariant() ?? ""
    };

    /// <summary>Baidu signature: lowercase hex MD5 of <c>appid + q + salt + secret</c>. The plain
    /// <paramref name="q"/> participates un-encoded; only the HTTP form body encodes it.</summary>
    internal static string BuildSign(string appid, string q, string salt, string secret)
    {
        var bytes = MD5.HashData(Encoding.UTF8.GetBytes(appid + q + salt + secret));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    internal static async Task<(string Text, string? Error)> TranslateAsync(
        string text, string source, string target, string appid, string secret, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(appid) || string.IsNullOrEmpty(secret))
            return ("", "No Baidu translation credentials configured. Set them in Settings.");
        var from = string.IsNullOrWhiteSpace(source) ? "auto" : BaiduLanguage(source);
        var to = BaiduLanguage(target) ?? "en";
        if (string.IsNullOrEmpty(to)) return ("", "Choose a target language for Baidu translation.");

        var salt = Random.Shared.NextInt64().ToString();
        var sign = BuildSign(appid, text, salt, secret);
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["q"] = text,
            ["from"] = from,
            ["to"] = to,
            ["appid"] = appid,
            ["salt"] = salt,
            ["sign"] = sign,
            ["model_type"] = ModelType
        });
        string json;
        try
        {
            using var response = await Http.PostAsync(Endpoint, content, ct);
            json = await response.Content.ReadAsStringAsync(ct);
        }
        catch (OperationCanceledException) { return ("", "Translation timed out. Try again."); }
        catch { return ("", "Translation request failed. Check your network."); }

        try
        {
            using var doc = JsonDocument.Parse(json);
            // 百度 error_code 可能是 Number（52000）或 String（"52000"），GetString() 在
            // Number 上会抛 InvalidOperationException；统一两种形态读取，避免未处理异常。
            string? errorCode = null;
            if (doc.RootElement.TryGetProperty("error_code", out var err))
                errorCode = err.ValueKind == JsonValueKind.Number
                    ? err.GetRawText()
                    : err.GetString();
            if (!string.IsNullOrEmpty(errorCode) && errorCode != "52000")
            {
                var msg = doc.RootElement.TryGetProperty("error_msg", out var em)
                          && em.ValueKind == JsonValueKind.String
                    ? em.GetString() : "";
                return ("", $"Baidu error {errorCode}: {msg}");
            }
            if (!doc.RootElement.TryGetProperty("trans_result", out var arr) || arr.GetArrayLength() == 0)
                return ("", "Baidu returned no translation.");
            var sb = new StringBuilder();
            foreach (var item in arr.EnumerateArray())
                if (item.TryGetProperty("dst", out var dst)
                    && dst.ValueKind == JsonValueKind.String
                    && !string.IsNullOrEmpty(dst.GetString()))
                    sb.AppendLine(dst.GetString());
            var result = sb.ToString().TrimEnd('\r', '\n');
            return string.IsNullOrEmpty(result) ? ("", "Baidu returned an empty translation.") : (result, null);
        }
        catch (Exception)
        {
            // 解析边界兜底：任何响应解析异常都转为可读错误，绝不冒泡成未处理异常进全局日志。
            return ("", "Could not parse Baidu's response.");
        }
    }
}