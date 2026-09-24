using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using LinkPocket.Services;
using MdXaml;

namespace LinkPocket.Views;

/// <summary>
/// 共享 Markdown 渲染件（唯一实现）：把 Markdown 文档的渲染口径固定在一处，供详情页描述卡与 AI 消息共用。
/// 三条固定口径：
/// ① 视觉跟随语义令牌（字体 / 前景 / 无内边距、无内滚动——滚动交给外层容器）；
/// ② <b>链接不由渲染库打开</b>：库自带的点击动作显式关闭，链接改接 <see cref="LinkLauncher"/> 这唯一出口
///    （协议白名单在那一条链上）；
/// ③ <b>图片一律不渲染</b>：Markdown 里的图片是外部可控内容，联网抓图既是新的网络出口、也是提示注入的
///    信标通道；文档里出现图片元素时整体移除（不加载、不占位）。
/// </summary>
public sealed class MarkdownText : MarkdownScrollViewer
{
    static MarkdownText()
    {
        // Markdown 变更 → MdXaml 同步产出新 FlowDocument：每次换文档都过一遍净化（见类注释 ②③）
        DocumentProperty.OverrideMetadata(typeof(MarkdownText),
            new FrameworkPropertyMetadata(OnDocumentChanged));
    }

    public MarkdownText()
    {
        VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        IsSelectionEnabled = true;
        IsToolBarVisible = false;
        Padding = new Thickness(0);
        ClickAction = ClickAction.None;
        SetResourceReference(FontFamilyProperty, "App.Font.Ui");
        SetResourceReference(ForegroundProperty, "App.Text.Primary");
        SetResourceReference(MarkdownStyleProperty, "MarkdownDocumentStyle");
    }

    private static void OnDocumentChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is FlowDocument document) Neutralize(document);
    }

    /// <summary>文档级净化：链接改接唯一出口、图片元素整体摘除（逻辑树递归，覆盖段落 / 列表 / 表格 / 引用）。</summary>
    private static void Neutralize(DependencyObject root)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>().ToArray())
        {
            switch (child)
            {
                case Hyperlink link:
                    Retarget(link);
                    break;
                case InlineUIContainer { Child: Image } inline:
                    DetachImage(inline);
                    continue;
                case BlockUIContainer { Child: Image } block:
                    DetachImage(block);
                    continue;
            }
            Neutralize(child);
        }
    }

    /// <summary>链接：清掉 WPF 自带的导航 URI（那是"点了就按系统关联执行"的默认行为），改接唯一出口。</summary>
    private static void Retarget(Hyperlink link)
    {
        var url = link.NavigateUri?.ToString();
        link.NavigateUri = null;
        if (string.IsNullOrEmpty(url)) return;
        link.Click += (_, _) => LinkLauncher.Open(url);
    }

    private static void DetachImage(InlineUIContainer inline)
    {
        if (inline.Child is Image image) image.Source = null;
        inline.SiblingInlines?.Remove(inline);
    }

    private static void DetachImage(BlockUIContainer block)
    {
        if (block.Child is Image image) image.Source = null;
        block.SiblingBlocks?.Remove(block);
    }
}
