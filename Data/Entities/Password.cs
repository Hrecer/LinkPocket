using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Security.Cryptography;

namespace LinkPocket.Data;

[Table("passwords")]
public class Password
{
    [Key]
    public int Id { get; set; }

    [Required]
    [MaxLength(255)]
    public string Title { get; set; } = string.Empty;

    [MaxLength(255)]
    public string? Username { get; set; }

    [Column("encrypted_password")]
    public string EncryptedPassword { get; set; } = string.Empty;

    [MaxLength(2048)]
    public string? Url { get; set; }

    [Column(TypeName = "text")]
    public string? Notes { get; set; }

    [MaxLength(100)]
    public string? Category { get; set; }

    [Column("strength_score")]
    public int StrengthScore { get; set; } = 0;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // AES-256加密
    private static readonly byte[] Key = GenerateKey();
    private static readonly byte[] IV = GenerateIV();

    private static byte[] GenerateKey()
    {
        using var rng = RandomNumberGenerator.Create();
        var key = new byte[32]; // 256 bits
        rng.GetBytes(key);
        return key;
    }

    private static byte[] GenerateIV()
    {
        using var rng = RandomNumberGenerator.Create();
        var iv = new byte[16]; // 128 bits
        rng.GetBytes(iv);
        return iv;
    }

    public void SetPassword(string plainPassword)
    {
        using var aes = Aes.Create();
        aes.Key = Key;
        aes.IV = IV;

        using var encryptor = aes.CreateEncryptor();
        var plainBytes = System.Text.Encoding.UTF8.GetBytes(plainPassword);
        var encryptedBytes = encryptor.TransformFinalBlock(plainBytes, 0, plainBytes.Length);
        
        EncryptedPassword = Convert.ToBase64String(encryptedBytes);
    }

    public string GetDecryptedPassword()
    {
        try
        {
            using var aes = Aes.Create();
            aes.Key = Key;
            aes.IV = IV;

            using var decryptor = aes.CreateDecryptor();
            var encryptedBytes = Convert.FromBase64String(EncryptedPassword);
            var decryptedBytes = decryptor.TransformFinalBlock(encryptedBytes, 0, encryptedBytes.Length);
            
            return System.Text.Encoding.UTF8.GetString(decryptedBytes);
        }
        catch (Exception)
        {
            return "[解密失败]";
        }
    }
}
