namespace LinkPocket.Ai;

/// <summary>
/// 密文编解码缝：生产**只装配 DPAPI 实现**（<see cref="DpapiSecretCipher"/>），测试用内存假实现。
/// **不存在"明文降级"分支**——解密失败一律抛，由调用方如实暴露（映射成 <c>LP.AI.012</c>）。
/// </summary>
public interface ISecretCipher
{
    /// <summary>加密 → 可直接落盘的 base64 文本。</summary>
    string Protect(string plaintext);

    /// <summary>解密（失败即抛：不返回空串、不回落原值、不返回 null）。</summary>
    string Unprotect(string protectedBase64);
}
