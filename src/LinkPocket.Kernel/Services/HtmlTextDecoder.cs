using System.Text;
using System.Text.RegularExpressions;

namespace LinkPocket.Kernel;

/// <summary>
/// HTML 字节 → 文本的解码口径：响应头 charset → BOM → 文档内 &lt;meta&gt; 声明 → UTF-8 兜底。
/// <para>
/// 国内站点常见 <c>Content-Type: text/html</c>（不带 charset）+ 页内 <c>&lt;meta charset=gb2312&gt;</c>，
/// 只按响应头解码会把整页中文读成替换字符（U+FFFD）。GB2312 / GBK 属于代码页，
/// 需要先注册 <see cref="CodePagesEncodingProvider"/> 才可用。
/// </para>
/// </summary>
public static class HtmlTextDecoder
{
    /// <summary>页内 charset 声明的扫描范围（HTML 规范只预扫描文档开头，实际声明都在这里）。</summary>
    private const int CharsetScanBytes = 8 * 1024;

    static HtmlTextDecoder() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    private static readonly Regex MetaCharsetPattern = new(
        @"<meta\b[^>]*?\bcharset\s*=\s*[""']?\s*([A-Za-z0-9._-]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>解码 HTML 字节。</summary>
    /// <param name="bytes">原始字节（可含未使用的尾部容量）。</param>
    /// <param name="length">有效字节数。</param>
    /// <param name="headerCharset">响应头 <c>Content-Type</c> 的 charset；没有则传 <c>null</c>。</param>
    public static string Decode(byte[] bytes, int length, string? headerCharset)
    {
        if (length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return Encoding.UTF8.GetString(bytes, 3, length - 3);
        if (length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return Encoding.Unicode.GetString(bytes, 2, length - 2);
        if (length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return Encoding.BigEndianUnicode.GetString(bytes, 2, length - 2);

        var encoding = Resolve(headerCharset, bytes.AsSpan(0, Math.Min(length, CharsetScanBytes)));
        return encoding.GetString(bytes, 0, length);
    }

    private static Encoding Resolve(string? headerCharset, ReadOnlySpan<byte> head)
    {
        if (TryGet(headerCharset, out var fromHeader)) return fromHeader;

        var declared = MetaCharsetPattern.Match(Encoding.Latin1.GetString(head));
        if (declared.Success && TryGet(declared.Groups[1].Value, out var fromMeta)) return fromMeta;

        return Encoding.UTF8;
    }

    private static bool TryGet(string? name, out Encoding encoding)
    {
        encoding = Encoding.UTF8;
        if (string.IsNullOrWhiteSpace(name)) return false;

        try
        {
            encoding = Encoding.GetEncoding(name.Trim().Trim('"', '\''));
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
    }
}
