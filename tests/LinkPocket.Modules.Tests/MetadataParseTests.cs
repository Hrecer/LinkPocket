using LinkPocket.Kernel;
using Xunit;

namespace LinkPocket.Modules.Tests;

/// <summary>
/// 页面元数据纯解析（L0 <see cref="RegexMetadataParser"/>）回归：
/// favicon 的真实站点书写差异（如 chat.qwen.ai 用 HTML5 **无引号属性**书写
/// &lt;link rel=icon href=//cdn/x.png&gt;），以及 SPA 空壳页
/// （work.n.cn 的 &lt;title&gt; 为空、只有站点描述）的标题回落。
/// </summary>
public class MetadataParseTests
{
    private static string? FaviconOf(string html, string pageUrl = "https://chat.qwen.ai/")
        => RegexMetadataParser.Instance.Parse(html, new Uri(pageUrl)).FaviconUrl;

    private static PageMetadata MetaOf(string html, string pageUrl = "https://work.n.cn/")
        => RegexMetadataParser.Instance.Parse(html, new Uri(pageUrl));

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
    public void 无图标扩展名的声明地址原样采用_不回落站点根()
    {
        var html = "<link rel=icon href=/handler>";
        Assert.Equal("https://chat.qwen.ai/handler", FaviconOf(html));
    }

    [Fact]
    public void 带查询串的无扩展名图标地址原样采用()
    {
        // blog.siitake.cn：<link rel="icon" href="https://i2.wp.com/.../avatar/<hash>?d=blank">
        // 头像 / 图片服务的地址没有图标扩展名，旧实现按扩展名白名单误判并回落站点根（该站根路径 404）
        var html = "<link rel=\"icon\" href=\"https://i2.wp.com/gravatar.loli.net/avatar/2fbd223c8ccde74c2e2ac9f9662249f6?d=blank\">";
        Assert.Equal(
            "https://i2.wp.com/gravatar.loli.net/avatar/2fbd223c8ccde74c2e2ac9f9662249f6?d=blank",
            FaviconOf(html));
    }

    [Fact]
    public void 没有link标签时回落站点根()
    {
        Assert.Equal("https://chat.qwen.ai/favicon.ico", FaviconOf("<head><title>x</title></head>"));
    }

    [Fact]
    public void 空title时回落到页面描述_SPA空壳也能解析出标题()
    {
        // work.n.cn 首页：<title></title> 由前端脚本稍后写入，HTML 里只有站点描述
        var html = "<head><title></title><meta name=\"keywords\" content=\"纳米Work\">"
                   + "<meta name=\"description\" content=\"纳米Work\"></head>";
        var meta = MetaOf(html);
        Assert.Equal("纳米Work", meta.Title);
        Assert.Equal("纳米Work", meta.Description);
    }

    [Fact]
    public void 空title时优先回落到og_site_name()
    {
        var html = "<head><title></title><meta property=\"og:site_name\" content=\"站点名\">"
                   + "<meta name=\"description\" content=\"一段更长的描述\"></head>";
        Assert.Equal("站点名", MetaOf(html).Title);
    }

    [Fact]
    public void 空title时application_name先于描述()
    {
        var html = "<head><title>   </title><meta name=\"application-name\" content=\"应用名\">"
                   + "<meta name=\"description\" content=\"描述\"></head>";
        Assert.Equal("应用名", MetaOf(html).Title);
    }

    [Fact]
    public void 非空title不被描述覆盖()
    {
        var html = "<head><title>真标题</title><meta name=\"description\" content=\"描述\"></head>";
        Assert.Equal("真标题", MetaOf(html).Title);
    }

    [Fact]
    public void twitter_title的name写法也能覆盖title()
    {
        var html = "<head><title>旧标题</title><meta name=\"twitter:title\" content=\"推文标题\"></head>";
        Assert.Equal("推文标题", MetaOf(html).Title);
    }
}
