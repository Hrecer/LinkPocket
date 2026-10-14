using System.Windows;
using System.Windows.Controls;
using LinkPocket.Services;

namespace LinkPocket.Views;

/// <summary>
/// 「关于」面板（设置页左栏最末项）：应用名称与版本 + 简介 / 主要能力 + 项目仓库地址。
/// 版本号 = 组合根传入的产品版本（<c>IUpdateService.CurrentVersion</c>，与更新面板同源）；
/// 未注入时回退本程序集版本。地址行 = 身份字面量（不翻译、不随语言变）；
/// 「打开仓库」经唯一出口 <see cref="LinkLauncher"/>（只放行 http/https，不自己起进程）。
/// </summary>
public partial class AboutPanel : UserControl
{
    /// <summary>项目仓库地址（按钮打开用完整 https 地址；显示用去协议的短形）。</summary>
    private const string RepositoryUrl = "https://github.com/Hrecer/LinkPocket";

    public AboutPanel()
    {
        InitializeComponent();
        RepoUrlText.Text = RepositoryUrl.Replace("https://", string.Empty);
        VersionText.Text = "v" + (typeof(AboutPanel).Assembly.GetName().Version?.ToString(3) ?? "0.0.0");
    }

    /// <summary>注入产品版本（null / 空白 = 保留构造期的程序集版本回退）。</summary>
    public void SetVersion(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return;
        VersionText.Text = "v" + version.Trim();
    }

    private void OpenRepoButton_Click(object sender, RoutedEventArgs e) => LinkLauncher.Open(RepositoryUrl);
}
