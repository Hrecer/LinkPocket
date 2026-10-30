using LinkPocket.Kernel;
using Xunit;

namespace LinkPocket.Modules.Tests;

/// <summary>
/// 反爬 / 人机校验中间页的识别（<see cref="AntiBotInterstitial"/>）：这类页面状态码是 <b>200</b>，
/// 正文却是"正在进行安全检测"的空壳，解析器在它上面只能拿到空标题与空描述——
/// 识别不出来就会被当成"普通页面没写标题"，界面也就说不出真实原因。
/// </summary>
public class AntiBotInterstitialTests
{
    /// <summary>36kr 首页实测挑战页的裁剪样本（火山引擎 WAF：JSON 挑战 + 内联 proof 脚本 + 空 data URI 图标）。</summary>
    private const string VolcengineChallenge = """
        <!DOCTYPE html><html lang="en"><head>
        <meta charset="UTF-8" />
        <link rel="icon" href="data:;base64,=" />
        <script type="application/json" id="__waf_verification_challenge">{"v":1,"cid":"d1fGrvHuFnb8WmFc6Z3wWw","probe_ids":[101,102]}</script>
        <script>!function(e){"use strict";var n="_waf_verification_proof";}(window);</script>
        </head><body><main class="page"><section class="card">
        <div class="brand"><span>火山引擎</span></div>
        <p class="title"><span>正在进行安全检测...</span></p>
        <p class="desc">为保障您的访问安全，系统正在检测当前网络环境，该过程通常需要几秒钟</p>
        </section></main></body></html>
        """;

    [Fact]
    public void 火山引擎挑战页识别为拦截页()
        => Assert.True(AntiBotInterstitial.Detect(VolcengineChallenge));

    [Fact]
    public void Cloudflare挑战页识别为拦截页()
        => Assert.True(AntiBotInterstitial.Detect(
            "<html><head><script src=\"/cdn-cgi/challenge-platform/h/b/orchestrate/jsch/v1\"></script></head></html>"));

    [Fact]
    public void 阿里云WAF挑战页识别为拦截页()
        => Assert.True(AntiBotInterstitial.Detect(
            "<html><head><script>var acw_sc__v2='6570c1a2'</script></head></html>"));

    [Fact]
    public void 只有软标记且页面很大时不算拦截页()
    {
        // "安全检测" 是正常文章里会出现的词：文档一大（正文级）就不该判成挑战页
        var html = "<head><title>某公司发布安全检测产品</title></head><body>"
                   + new string('x', 100_000) + "正在进行安全检测</body>";

        Assert.False(AntiBotInterstitial.Detect(html));
    }

    [Fact]
    public void 小页面无标题且带软标记算拦截页()
        => Assert.True(AntiBotInterstitial.Detect(
            "<html><head></head><body><p>正在进行安全检测，请稍候</p></body></html>"));

    [Fact]
    public void 正常页面不算拦截页()
        => Assert.False(AntiBotInterstitial.Detect(
            "<html><head><title>36氪_让一部分人先看到未来</title>"
            + "<meta name=\"description\" content=\"36氪通过全面，独家的视角\"></head><body>…</body></html>"));

    [Fact]
    public void 空文档不算拦截页()
    {
        Assert.False(AntiBotInterstitial.Detect(""));
        Assert.False(AntiBotInterstitial.Detect(null));
    }

    /// <summary>
    /// 端到端症状钉：挑战页在解析器眼里就是"标题与描述皆空、只剩图标"——正是用户看到的
    /// 「只解析到图标」。识别位（<c>BlockedByVerification</c>）负责把这份空白翻成真实原因。
    /// </summary>
    [Fact]
    public void 挑战页解析出的正是只有图标()
    {
        var metadata = RegexMetadataParser.Instance.Parse(VolcengineChallenge, new Uri("https://www.36kr.com/"));

        Assert.Null(metadata.Title);
        Assert.Null(metadata.Description);
        Assert.Equal("https://www.36kr.com/favicon.ico", metadata.FaviconUrl);   // data URI 无扩展名 → 回落站点根
    }
}
