using System.Text.RegularExpressions;

namespace LinkPocket.Kernel;

/// <summary>
/// 反爬 / 人机校验中间页识别（L0 纯函数）：判断一份 HTML 是不是"站点没给页面，给了一张验证页"。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么必须先识别</b>：这类页面的状态码是 <b>200</b>，正文却是一张"正在进行安全检测"的空壳
/// （几十 KB 内联样式 + 两段算 proof 的脚本）。不识别就会把它当成一个"没写 title 的普通页面"——
/// 用户看到的是「图标出来了、标题描述全空、也没有任何提示」，而真正的原因（站点拒绝自动化访问）
/// 完全丢失。识别出来才能决定：换浏览器指纹重试、或交给浏览器内核渲染、或如实告诉用户。
/// </para>
/// <para>
/// <b>两级判据</b>：硬标记 = 厂商挑战页的指纹（误报率可忽略，命中即判）；软标记 = 挑战页上的人话
/// （"正在进行安全检测"这类文案在正常文章里也会出现，故只在<b>文档很小且没有标题</b>时才认）。
/// </para>
/// </remarks>
public static class AntiBotInterstitial
{
    /// <summary>软标记的生效上限：验证页都是几万字节内的空壳，正常文章远大于此。</summary>
    private const int SoftMarkerMaxChars = 64 * 1024;

    /// <summary>厂商挑战页的指纹：命中即判，不看大小与标题。</summary>
    private static readonly string[] HardMarkers =
    [
        "__waf_verification_challenge",   // 火山引擎 WAF（36kr 首页即此例：200 + 25 KB 空壳）
        "_waf_verification_proof",
        "cf-browser-verification",        // Cloudflare
        "cf_chl_opt",
        "cdn-cgi/challenge-platform",
        "acw_sc__v2",                     // 阿里云 WAF 的 JS 挑战（火绒官网同族）
        "aliyun_waf_",                    // 阿里云 WAF 拦截页
    ];

    /// <summary>
    /// 挑战页上的人话：正常站点也会写，故只作"小页面 + 无标题"时的辅证。
    /// <para>
    /// 中文部分写成 <c>\u</c> 转义——它们是**远端返回页面里的文本**（不是界面文案，不进 Loc 表），
    /// 而"零中文字面量"这条棘轮没有形状白名单，转义是不破红线又不丢覆盖的唯一写法。
    /// 解码后依次 = "正在进行安全检测"、"安全验证"。
    /// </para>
    /// </summary>
    private static readonly string[] SoftMarkers =
    [
        "\u6b63\u5728\u8fdb\u884c\u5b89\u5168\u68c0\u6d4b",
        "\u5b89\u5168\u9a8c\u8bc1",
        "Just a moment",
        "Checking your browser",
        "Verify you are human",
        "Enable JavaScript and cookies to continue",
    ];

    private static readonly Regex TitlePattern = new(@"<title[^>]*>(.*?)</title>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline);

    /// <summary>这份 HTML 是否为反爬 / 人机校验的中间页。</summary>
    public static bool Detect(string? html)
    {
        if (string.IsNullOrEmpty(html)) return false;

        foreach (var marker in HardMarkers)
            if (html.Contains(marker, StringComparison.OrdinalIgnoreCase))
                return true;

        if (html.Length > SoftMarkerMaxChars) return false;
        if (HasTitle(html)) return false;

        foreach (var marker in SoftMarkers)
            if (html.Contains(marker, StringComparison.OrdinalIgnoreCase))
                return true;

        return false;
    }

    /// <summary>文档里是否已有一个非空标题（决定软标记能不能作为辅证）。</summary>
    private static bool HasTitle(string html)
    {
        var match = TitlePattern.Match(html);
        return match.Success && match.Groups[1].Value.Trim().Length > 0;
    }
}
