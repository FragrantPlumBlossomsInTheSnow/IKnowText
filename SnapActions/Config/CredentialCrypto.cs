using System.Security.Cryptography;
using System.Text;

namespace SnapActions.Config;

/// <summary>
/// Stores Baidu credentials as a single opaque string. The AppID and secret are joined with a
/// newline and protected with Windows DPAPI scoped to the current user, then stored base64-encoded.
/// At rest the settings file never contains the plaintext secret; it only decrypts for the same
/// Windows user who saved it.
/// </summary>
internal static class CredentialCrypto
{
    internal static string EncryptBaidu(string appId, string secret)
    {
        var raw = Encoding.UTF8.GetBytes($"{appId}\n{secret}");
        return Convert.ToBase64String(ProtectedData.Protect(raw, null, DataProtectionScope.CurrentUser));
    }

    internal static (string AppId, string Secret) DecryptBaidu(string? blob)
    {
        if (string.IsNullOrEmpty(blob)) return ("", "");
        try
        {
            var raw = ProtectedData.Unprotect(Convert.FromBase64String(blob), null, DataProtectionScope.CurrentUser);
            var parts = Encoding.UTF8.GetString(raw).Split('\n', 2);
            return (parts[0], parts.Length > 1 ? parts[1] : "");
        }
        catch
        {
            // Wrong user, garbage blob, or non-Windows — never throw on a settings file.
            return ("", "");
        }
    }

    /// <summary>
    /// 设置窗口的两个凭据输入框 → blob。设置里没有「保存百度凭据」按钮，输入即真相，每次设置保存
    /// （防抖 tick 或关窗）都会走这里：
    /// <list type="bullet">
    /// <item>输入值与已存值相同：原样返回旧 blob，不重新加密（避免无关设置保存时反复 DPAPI 运算，blob 稳定）。</item>
    /// <item>两个框都为空：清空凭据（用户主动删除的唯一途径）。</item>
    /// <item>其余情况：重新加密。</item>
    /// </list>
    /// 解密失败（换了 Windows 用户、blob 损坏）时两个框也是空的，此时保持旧 blob 不动 —— 一次无关的
    /// 设置保存不该把用户已存的凭据静默擦掉。
    /// </summary>
    internal static string ReconcileBaidu(string? currentBlob, string? appIdBox, string? secretBox)
    {
        var appId = appIdBox?.Trim() ?? "";
        var secret = secretBox ?? "";
        var (storedAppId, storedSecret) = DecryptBaidu(currentBlob);
        if (appId == storedAppId && secret == storedSecret) return currentBlob ?? "";
        return appId.Length + secret.Length == 0 ? "" : EncryptBaidu(appId, secret);
    }
}