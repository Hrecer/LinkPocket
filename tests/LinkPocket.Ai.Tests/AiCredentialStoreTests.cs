using System.Security.Cryptography;
using System.Text;
using LinkPocket.Contracts;
using LinkPocket.Engine;
using Xunit;

namespace LinkPocket.Ai.Tests;

/// <summary>
/// 凭据库与 DPAPI 实现：明文不落盘 / 掩码口径 / 删除幂等 / 损坏与版本不符**拒绝覆盖** /
/// 解密失败映射 <c>LP.AI.012</c> / 原子写不留临时文件。
/// </summary>
public class AiCredentialStoreTests
{
    /// <summary>内存假 cipher（测试不依赖 OS 加密；密文可反解以便断言"明文未落盘"）。</summary>
    private sealed class FakeCipher : ISecretCipher
    {
        public string Protect(string plaintext) => "enc:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(plaintext));

        public string Unprotect(string protectedBase64)
            => Encoding.UTF8.GetString(Convert.FromBase64String(protectedBase64["enc:".Length..]));
    }

    private sealed class FailingUnprotectCipher : ISecretCipher
    {
        public string Protect(string plaintext) => "enc:broken";

        public string Unprotect(string protectedBase64) => throw new CryptographicException("cannot decrypt");
    }

    private const string ProviderId = "openai";
    private const string ApiKey = "sk-1234567890abcdefghij";

    private static string NewRoot()
    {
        var root = Path.Combine(TempArea.Resolve(), "ai-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void Drop(string root)
    {
        try { Directory.Delete(root, recursive: true); }
        catch (IOException) { /* 尽力清理即可 */ }
        catch (UnauthorizedAccessException) { /* 同上 */ }
    }

    [Fact]
    public void 凭据库_写入后可按提供者读回明文_且落盘文件不含明文()
    {
        var root = NewRoot();
        try
        {
            var store = new AiCredentialStore(root, new FakeCipher());
            store.Set(ProviderId, ApiKey);

            Assert.True(store.Has(ProviderId));
            Assert.Equal(ApiKey, store.TryGetPlaintext(ProviderId));
            Assert.False(File.ReadAllText(store.FilePath).Contains(ApiKey, StringComparison.Ordinal));
            Assert.False(File.Exists(store.FilePath + ".tmp"));
        }
        finally { Drop(root); }
    }

    [Fact]
    public void 凭据库_掩码只露前四后四_短密钥整体打码()
    {
        Assert.Equal("sk-1…ghij", AiCredentialStore.Mask(ApiKey));
        Assert.Equal("*****", AiCredentialStore.Mask("short"));
    }

    [Fact]
    public void 凭据库_删除后不再命中_且重复删除幂等()
    {
        var root = NewRoot();
        try
        {
            var store = new AiCredentialStore(root, new FakeCipher());
            store.Set(ProviderId, ApiKey);
            Assert.Equal("sk-1…ghij", store.MaskedOf(ProviderId));

            store.Remove(ProviderId);
            Assert.False(store.Has(ProviderId));
            Assert.Null(store.TryGetPlaintext(ProviderId));
            Assert.Null(store.MaskedOf(ProviderId));
            store.Remove(ProviderId);
        }
        finally { Drop(root); }
    }

    [Fact]
    public void 凭据库_文件损坏时读取与写入都报LP_AI_012_且拒绝覆盖原文件()
    {
        const string broken = "{ this is not json";
        var root = NewRoot();
        try
        {
            var path = Path.Combine(root, "credentials.json");
            File.WriteAllText(path, broken);
            var store = new AiCredentialStore(root, new FakeCipher());

            var read = Assert.Throws<AiException>(() => store.Has(ProviderId));
            Assert.Equal(AiErrors.CredentialStoreFailed, read.Error.Code);

            var write = Assert.Throws<AiException>(() => store.Set(ProviderId, ApiKey));
            Assert.Equal(AiErrors.CredentialStoreFailed, write.Error.Code);
            Assert.Equal(broken, File.ReadAllText(path));
        }
        finally { Drop(root); }
    }

    [Fact]
    public void 凭据库_版本不符视为损坏()
    {
        var root = NewRoot();
        try
        {
            File.WriteAllText(Path.Combine(root, "credentials.json"), "{\"Version\":9,\"Keys\":{}}");
            var store = new AiCredentialStore(root, new FakeCipher());

            var ex = Assert.Throws<AiException>(() => store.Has(ProviderId));
            Assert.Equal(AiErrors.CredentialStoreFailed, ex.Error.Code);
        }
        finally { Drop(root); }
    }

    [Fact]
    public void 凭据库_解密失败映射为LP_AI_012_不回落原值()
    {
        var root = NewRoot();
        try
        {
            new AiCredentialStore(root, new FakeCipher()).Set(ProviderId, ApiKey);
            var reader = new AiCredentialStore(root, new FailingUnprotectCipher());

            var ex = Assert.Throws<AiException>(() => reader.TryGetPlaintext(ProviderId));
            Assert.Equal(AiErrors.CredentialStoreFailed, ex.Error.Code);
            Assert.Equal(ProviderId, ex.Error.Details!.Value.GetProperty("provider_id").GetString());
        }
        finally { Drop(root); }
    }

    [Fact]
    public void DPAPI实现_往返可解_且密文不暴露明文()
    {
        if (!OperatingSystem.IsWindows()) return;   // DPAPI 仅 Windows（开发机与 CI 均为 Windows）
        var cipher = new DpapiSecretCipher();

        var encrypted = cipher.Protect(ApiKey);
        Assert.False(encrypted.Contains(ApiKey, StringComparison.Ordinal));
        Assert.Equal(ApiKey, cipher.Unprotect(encrypted));
    }

    [Fact]
    public void 原子写_替换已有文件_不留临时文件()
    {
        var root = NewRoot();
        try
        {
            var path = Path.Combine(root, "sub", "value.json");
            AtomicFile.WriteAllText(path, "{\"a\":1}");
            AtomicFile.WriteAllText(path, "{\"a\":2}");

            Assert.Equal("{\"a\":2}", File.ReadAllText(path));
            Assert.False(File.Exists(path + ".tmp"));
        }
        finally { Drop(root); }
    }
}
