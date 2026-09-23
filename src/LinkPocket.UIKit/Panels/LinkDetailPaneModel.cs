using System;
using System.Collections.Generic;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using LinkPocket.I18n;

namespace LinkPocket.ViewModels;

/// <summary>
/// 「链接详情」共享页模型（<c>Views.LinkDetailPane</c> 的唯一数据源）：
/// **浏览页详情页与回收站只读详情页共用同一份界面与同一份模型**——各页只填数据（标题 / URL / 图标 /
/// 信息行 / 描述）并在动作面上声明自己的入口，绝不复制界面或逻辑。
/// 信息卡 = 数据驱动行集合（复用侧栏同一行类型 <see cref="DetailSidebarRow"/>）。
/// </summary>
public class LinkDetailPaneModel : ActionSurfaceModel
{
    /// <summary>标题<b>用户数据</b>（书签标题 / 文件夹名）。</summary>
    public string TitleData { get; protected set; } = "";

    /// <summary>标题<b>文案</b>（数据缺失时的中性兜底，如「无名称」）。</summary>
    public LocValue TitleCopy { get; protected set; }
    public string Url { get; protected set; } = string.Empty;
    public BitmapImage? Favicon { get; protected set; }
    public bool HasFavicon => Favicon != null;

    /// <summary>描述（Markdown 渲染；空 = 整卡隐藏）。</summary>
    public string Description { get; protected set; } = string.Empty;
    public bool HasDescription => !string.IsNullOrWhiteSpace(Description);

    /// <summary>信息卡行（顺序即显示顺序；图标 + 标签 + 值 + 可选复制）。</summary>
    public IReadOnlyList<DetailSidebarRow> Rows { get; protected set; } = Array.Empty<DetailSidebarRow>();

    /// <summary>返回（左上浮动圆钮）。</summary>
    public ICommand? BackCommand { get; set; }

    /// <summary>复制 URL（URL 卡右上角小图标按钮）。</summary>
    public ICommand? CopyUrlCommand { get; set; }

    /// <summary>填充一屏数据（各页写自己的投影；界面只做绑定）。</summary>
    protected void SetContent(string titleData, LocValue titleCopy, string url, BitmapImage? favicon, string description,
        IReadOnlyList<DetailSidebarRow> rows)
    {
        TitleData = titleData ?? "";
        TitleCopy = titleCopy;
        Url = url ?? string.Empty;
        Favicon = favicon;
        Description = description ?? string.Empty;
        Rows = rows ?? Array.Empty<DetailSidebarRow>();
        OnPropertyChanged(nameof(TitleData));
        OnPropertyChanged(nameof(TitleCopy));
        OnPropertyChanged(nameof(Url));
        OnPropertyChanged(nameof(Favicon));
        OnPropertyChanged(nameof(HasFavicon));
        OnPropertyChanged(nameof(Description));
        OnPropertyChanged(nameof(HasDescription));
        OnPropertyChanged(nameof(Rows));
        RaiseActionChanged();
    }
}
