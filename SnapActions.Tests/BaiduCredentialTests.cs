using SnapActions.Config;
using Xunit;

namespace SnapActions.Tests;

/// <summary>
/// 设置窗口的百度凭据没有「保存」按钮：输入框内容即真相，每次设置保存（防抖 tick 或关窗）
/// 都由 <see cref="CredentialCrypto.ReconcileBaidu"/> 把两个输入框并入 blob。
/// 这些用例固定住它的三条语义：值没变不重写、双空清空、解密失败不清掉原凭据。
/// </summary>
public class BaiduCredentialTests
{
    [Fact]
    public void UnchangedValues_KeepTheExistingBlobUntouched()
    {
        var blob = CredentialCrypto.EncryptBaidu("app-id", "secret");
        var result = CredentialCrypto.ReconcileBaidu(blob, "app-id", "secret");
        // DPAPI 每次加密都产生不同密文，所以「同一个字符串」等价于「没有重新加密」。
        Assert.True(ReferenceEquals(blob, result));
    }

    [Fact]
    public void AppIdBoxIsTrimmedBeforeComparing()
    {
        var blob = CredentialCrypto.EncryptBaidu("app-id", "secret");
        Assert.True(ReferenceEquals(blob, CredentialCrypto.ReconcileBaidu(blob, "  app-id ", "secret")));
    }

    [Fact]
    public void ChangedSecret_ReEncrypts()
    {
        var blob = CredentialCrypto.EncryptBaidu("app-id", "old");
        var result = CredentialCrypto.ReconcileBaidu(blob, "app-id", "new");
        Assert.NotEqual(blob, result);
        Assert.Equal(("app-id", "new"), CredentialCrypto.DecryptBaidu(result));
    }

    [Fact]
    public void ChangedAppId_ReEncrypts()
    {
        var blob = CredentialCrypto.EncryptBaidu("old", "secret");
        var result = CredentialCrypto.ReconcileBaidu(blob, "new", "secret");
        Assert.Equal(("new", "secret"), CredentialCrypto.DecryptBaidu(result));
    }

    [Fact]
    public void BothBoxesEmpty_ClearsCredentials()
    {
        var blob = CredentialCrypto.EncryptBaidu("app-id", "secret");
        Assert.Equal("", CredentialCrypto.ReconcileBaidu(blob, "", ""));
        Assert.Equal("", CredentialCrypto.ReconcileBaidu(blob, "   ", null));
    }

    [Fact]
    public void EmptyBlob_WithTypedValues_Encrypts()
    {
        var result = CredentialCrypto.ReconcileBaidu("", "app-id", "secret");
        Assert.NotEqual("", result);
        Assert.Equal(("app-id", "secret"), CredentialCrypto.DecryptBaidu(result));
    }

    [Fact]
    public void UnreadableBlob_WithEmptyBoxes_IsPreserved()
    {
        // 换了 Windows 用户 / blob 损坏：解出来是空，输入框也是空。此时一次无关的设置保存
        // 不能把用户已存的凭据静默擦掉（旧 blob 保持原样，便于用户重新粘贴/排查）。
        const string garbage = "not-a-valid-dpapi-blob";
        Assert.Equal(garbage, CredentialCrypto.ReconcileBaidu(garbage, "", ""));
        Assert.Equal(garbage, CredentialCrypto.ReconcileBaidu(garbage, null, null));
    }

    [Fact]
    public void UnreadableBlob_WithTypedValues_StoresTheNewCredentials()
    {
        var result = CredentialCrypto.ReconcileBaidu("not-a-valid-dpapi-blob", "app-id", "secret");
        Assert.Equal(("app-id", "secret"), CredentialCrypto.DecryptBaidu(result));
    }

    [Fact]
    public void NullBlobAndEmptyBoxes_StaysEmpty()
    {
        Assert.Equal("", CredentialCrypto.ReconcileBaidu(null, null, null));
    }
}
