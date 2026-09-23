using System.Security.Cryptography;
using System.Text;

namespace LinkPocket.Ai;

/// <summary>
/// DPAPI 实现（<see cref="DataProtectionScope.CurrentUser"/>）：密文绑定**当前 Windows 用户与机器**。
/// 换机器 / 换用户 / 文件损坏一律解不开 → 抛 <see cref="CryptographicException"/>，由凭据库映射成
/// <c>LP.AI.012</c> 并提示重新输入密钥（绝不静默清空）。
/// Entropy 是固定的二级密钥（不是秘密，只是让密文不与其他 DPAPI 用途互通）。
/// </summary>
public sealed class DpapiSecretCipher : ISecretCipher
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("LinkPocket.Ai.Credentials.v1");

    public string Protect(string plaintext)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("DPAPI credential protection requires Windows.");
        return Convert.ToBase64String(ProtectedData.Protect(
            Encoding.UTF8.GetBytes(plaintext), Entropy, DataProtectionScope.CurrentUser));
    }

    public string Unprotect(string protectedBase64)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("DPAPI credential protection requires Windows.");
        return Encoding.UTF8.GetString(ProtectedData.Unprotect(
            Convert.FromBase64String(protectedBase64), Entropy, DataProtectionScope.CurrentUser));
    }
}
