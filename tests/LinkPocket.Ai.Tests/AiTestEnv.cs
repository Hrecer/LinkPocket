using System.Security.Cryptography;
using System.Text;
using LinkPocket.Engine;

namespace LinkPocket.Ai.Tests;

/// <summary>AI 测试共享夹具：临时数据根（用完自清）+ 内存假 cipher（测试不依赖 OS 加密）。</summary>
internal static class AiTestEnv
{
    /// <summary>内存假 cipher（密文可反解，便于断言"明文未落盘"）。</summary>
    internal sealed class FakeCipher : ISecretCipher
    {
        public string Protect(string plaintext) => "enc:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(plaintext));

        public string Unprotect(string protectedBase64)
            => Encoding.UTF8.GetString(Convert.FromBase64String(protectedBase64["enc:".Length..]));
    }

    /// <summary>解密必抛（模拟换机器 / 换用户 / 密文损坏）。</summary>
    internal sealed class FailingUnprotectCipher : ISecretCipher
    {
        public string Protect(string plaintext) => "enc:broken";

        public string Unprotect(string protectedBase64) => throw new CryptographicException("cannot decrypt");
    }

    internal static string NewRoot()
    {
        var root = Path.Combine(TempArea.Resolve(), "ai-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    internal static void Drop(string root)
    {
        try { Directory.Delete(root, recursive: true); }
        catch (IOException) { /* 尽力清理即可 */ }
        catch (UnauthorizedAccessException) { /* 同上 */ }
    }
}
