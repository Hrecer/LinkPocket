using System.Text;
using LinkPocket.Kernel;
using Xunit;

namespace LinkPocket.Modules.Tests;

/// <summary>
/// HTML 字节解码口径：响应头 charset → BOM → 页内 &lt;meta&gt; → UTF-8。
/// 回归 drivergenius.com 这类站点：响应头 <c>text/html</c> 不带 charset，页内写 gb2312。
/// </summary>
public class HtmlTextDecoderTests
{
    static HtmlTextDecoderTests()
        => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    [Fact]
    public void 响应头无charset时按页内meta_gb2312解码()
    {
        const string html = "<html><head>"
                            + "<meta http-equiv=\"Content-Type\" content=\"text/html; charset=gb2312\" />"
                            + "<title>驱动精灵</title></head></html>";
        var bytes = Encoding.GetEncoding("gb2312").GetBytes(html);

        var text = HtmlTextDecoder.Decode(bytes, bytes.Length, headerCharset: null);

        Assert.Contains("<title>驱动精灵</title>", text);
        Assert.DoesNotContain('\uFFFD', text);
    }

    [Fact]
    public void meta_charset直接声明也能识别()
    {
        var bytes = Encoding.GetEncoding("gbk").GetBytes("<meta charset=\"GBK\"><title>中文标题</title>");

        var text = HtmlTextDecoder.Decode(bytes, bytes.Length, headerCharset: null);

        Assert.Contains("中文标题", text);
    }

    [Fact]
    public void 响应头charset优先于页内声明()
    {
        var bytes = Encoding.UTF8.GetBytes("<meta charset=gb2312><title>UTF8 标题</title>");

        var text = HtmlTextDecoder.Decode(bytes, bytes.Length, headerCharset: "utf-8");

        Assert.Contains("UTF8 标题", text);
    }

    [Fact]
    public void 未知charset回落到页内声明()
    {
        var bytes = Encoding.GetEncoding("gb2312").GetBytes("<meta charset=gb2312><title>中文</title>");

        var text = HtmlTextDecoder.Decode(bytes, bytes.Length, headerCharset: "not-a-charset");

        Assert.Contains("中文", text);
    }

    [Fact]
    public void utf8_bom被剥离()
    {
        var payload = Encoding.UTF8.GetBytes("<title>标题</title>");
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(payload).ToArray();

        var text = HtmlTextDecoder.Decode(bytes, bytes.Length, headerCharset: null);

        Assert.StartsWith("<title>", text);
    }
}
