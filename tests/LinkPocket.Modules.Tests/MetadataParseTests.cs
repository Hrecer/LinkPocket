using LinkPocket.Kernel;
using Xunit;

namespace LinkPocket.Modules.Tests;

/// <summary>
/// 页面元数据纯解析（L0 <see cref="RegexMetadataParser"/>）的 favicon 回归：
/// 真实站点（如 chat.qwen.ai）用 HTML5 **无引号属性**书写 &lt;link rel=icon href=//cdn/x.png&gt;，
/// 旧正则要求值必须被引号包住 → 整站解析不出图标、回落到站点根 /favicon.ico（多数站点 404）。
/// </summary>
public class MetadataParseTests
{
    private static string? FaviconOf(string html, string pageUrl = "https://chat.qwen.ai/")
        => RegexMetadataParser.Instance.Parse(html, new Uri(pageUrl)).FaviconUrl;

    [Fact]
    public void 无引号属性也能解析出图标_协议相对地址补https()
    {
        var html = "<head><link rel=icon href=//assets.alicdn.com/g/qwenweb/qwen-chat-fe/0.3.16/favicon.png></head>";
        Assert.Equal("https://assets.alicdn.com/g/qwenweb/qwen-chat-fe/0.3.16/favicon.png", FaviconOf(html));
    }

    [Fact]
    public void 属性顺序无关_href写在rel之前也能解析()
    {
        var html = "<link href=\"/static/app.png\" rel=\"icon\">";
        Assert.Equal("https://chat.qwen.ai/static/app.png", FaviconOf(html));
    }

    [Fact]
    public void 单引号与shortcut_icon同样识别()
    {
        var html = "<link rel='shortcut icon' href='https://cdn.test/i.ico'>";
        Assert.Equal("https://cdn.test/i.ico", FaviconOf(html));
    }

    [Fact]
    public void apple_touch_icon不算favicon_回落站点根()
    {
        var html = "<link rel=apple-touch-icon href=https://cdn.test/touch.png>";
        Assert.Equal("https://chat.qwen.ai/favicon.ico", FaviconOf(html));
    }

    [Fact]
    public void 无图标扩展名的地址回落站点根()
    {
        var html = "<link rel=icon href=/handler>";
        Assert.Equal("https://chat.qwen.ai/favicon.ico", FaviconOf(html));
    }

    [Fact]
    public void 没有link标签时回落站点根()
    {
        Assert.Equal("https://chat.qwen.ai/favicon.ico", FaviconOf("<head><title>x</title></head>"));
    }
}