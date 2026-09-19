using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Effects;
using LinkPocket.ViewModels;
using Material3.Wpf;

namespace LinkPocket.Views.Browser;

/// <summary>
/// 拖拽浮层（复刻 Windows 11 资源管理器的拖拽手感）：
/// <list type="bullet">
/// <item>跟随指针的**半透明行快照**（图标 + 名称）；多选时下方叠出"多层"观感并挂「N 个项目」计数徽标；</item>
/// <item>指针右下方的「移动到「X」」提示（内容随落点实时变化；无落点/非法落点时整条隐藏）。</item>
/// </list>
///
/// <para>为什么自绘而不是调 OLE 原生拖拽图像：原生图像需要 shell <c>IDataObject</c> + <c>IDragSourceHelper</c>
/// （P/Invoke + COM 互操作），观感与环境相关问题都难控制、探针也无法断言；自绘浮层挂在浏览页的 AdornerLayer 上，
/// 主栏 / 左栏树 / 跨栏拖拽天然是**同一套实现**（这正是本功能要的一致性）。</para>
///
/// <para>⚠️ 必须 <c>IsHitTestVisible = false</c>：否则浮层会吃掉指针下方的 DragOver/Drop，
/// 落点判定与光标会全部失效。位置更新走宿主在 <c>GiveFeedback</c> 里读屏幕坐标（拖拽期间 WPF 不再派发 MouseMove）。</para>
/// </summary>
internal sealed class DragVisualAdorner : Adorner
{
    /// <summary>浮层相对指针的偏移（与 Windows 的手感一致：图像落在指针右下，不遮住指针本身）。</summary>
    private const double PointerOffsetX = 12;
    private const double PointerOffsetY = 14;

    /// <summary>名称列最大宽度（超出省略号；浮层不该盖住半个屏幕）。</summary>
    private const double MaxNameWidth = 220;

    private readonly Border _card;
    private readonly TextBlock _name;
    private readonly M3Icon _icon;
    private readonly Border _stackBack2;
    private readonly Border _stackBack1;
    private readonly Border _badge;
    private readonly TextBlock _badgeText;
    private readonly Border _hint;
    private readonly TextBlock _hintText;

    private Point _offset;

    private DragVisualAdorner(FrameworkElement owner) : base(owner)
    {
        IsHitTestVisible = false;   // 关键：浮层绝不能参与命中测试

        var surface = Brush(owner, "SurfaceContainerHigh", Colors.WhiteSmoke);
        var outline = Brush(owner, "SurfaceContainerHighest", Colors.Gainsboro);
        var onSurface = Brush(owner, "OnSurface", Colors.Black);
        var primary = Brush(owner, "Primary", Colors.MediumPurple);
        var badgeBg = Brush(owner, "SecondaryContainer", Colors.LightGray);
        var badgeFg = Brush(owner, "OnSecondaryContainer", Colors.Black);

        // —— 堆叠感（多选时露出的两层"背板"）——
        _stackBack2 = StackLayer(surface, 4);
        _stackBack1 = StackLayer(surface, 2);

        _icon = new M3Icon { Width = 16, Height = 16, VerticalAlignment = VerticalAlignment.Center, Foreground = primary };

        _name = new TextBlock
        {
            Margin = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 12,
            Foreground = onSurface,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = MaxNameWidth,
        };

        _badgeText = new TextBlock
        {
            FontSize = 11,
            FontWeight = FontWeights.Medium,
            Foreground = badgeFg,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _badge = new Border
        {
            Background = badgeBg,
            CornerRadius = new CornerRadius(9),
            MinHeight = 18,
            Padding = new Thickness(8, 0, 8, 0),
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = _badgeText,
            Visibility = Visibility.Collapsed,
        };

        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(_icon);
        content.Children.Add(_name);
        content.Children.Add(_badge);

        _card = new Border
        {
            Background = surface,
            BorderBrush = outline,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 5, 10, 5),
            Opacity = 0.85,          // 半透明：与资源管理器一样能看见下面的落点
            Child = content,
            Effect = new DropShadowEffect { BlurRadius = 12, ShadowDepth = 2, Opacity = 0.25, Color = Colors.Black },
        };

        // —— 「移动到「X」」提示（独立小条，落在浮层下方）——
        _hintText = new TextBlock { FontSize = 11.5, Foreground = onSurface, VerticalAlignment = VerticalAlignment.Center };
        _hint = new Border
        {
            Background = surface,
            BorderBrush = outline,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8, 3, 8, 3),
            Opacity = 0.95,
            Visibility = Visibility.Collapsed,
            Child = _hintText,
            Effect = new DropShadowEffect { BlurRadius = 10, ShadowDepth = 2, Opacity = 0.2, Color = Colors.Black },
        };
    }

    /// <summary>在宿主所在的可视树上挂一个浮层（同一宿主只挂一个）。</summary>
    internal static DragVisualAdorner? Attach(FrameworkElement owner)
    {
        var layer = AdornerLayer.GetAdornerLayer(owner);
        if (layer == null) return null;
        var adorner = new DragVisualAdorner(owner);
        layer.Add(adorner);
        return adorner;
    }

    /// <summary>设置本次拖动的内容（单拖 = 行快照；多选 = 首项快照 + 「N 个项目」徽标 + 堆叠观感）。</summary>
    internal void Show(IReadOnlyList<DragItem> items)
    {
        var first = items.FirstOrDefault();
        if (first == null) return;

        _icon.Kind = first.IsFolder ? "folder" : "link-variant";
        _name.Text = first.Name;

        var multiple = items.Count > 1;
        _badgeText.Text = $"{items.Count} 个项目";
        _badge.Visibility = multiple ? Visibility.Visible : Visibility.Collapsed;
        _stackBack1.Visibility = multiple ? Visibility.Visible : Visibility.Collapsed;
        _stackBack2.Visibility = multiple ? Visibility.Visible : Visibility.Collapsed;

        InvalidateArrange();
    }

    /// <summary>更新「移动到 X」提示（空串 = 隐藏整条）。</summary>
    internal void UpdateHint(string hintText)
    {
        var show = !string.IsNullOrEmpty(hintText);
        if (show) _hintText.Text = hintText;
        _hint.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        InvalidateArrange();
    }

    /// <summary>更新位置（宿主给的坐标须相对被装饰元素）。</summary>
    internal void UpdatePosition(Point ownerPoint)
    {
        _offset = ownerPoint;
        InvalidateArrange();
    }

    /// <summary>从可视树摘掉浮层（拖拽结束 / 页面卸载）。</summary>
    internal void Detach() => (Parent as AdornerLayer)?.Remove(this);

    protected override Size MeasureOverride(Size constraint)
    {
        _card.Measure(constraint);
        _hint.Measure(constraint);
        var w = Math.Max(_card.DesiredSize.Width, _hint.DesiredSize.Width) + 8;
        var h = _card.DesiredSize.Height + _hint.DesiredSize.Height + 6;
        return new Size(w, h);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        // 浮层左上角 = 指针 + 偏移；堆叠背板向左上各让 2/4px；提示条在浮层正下方
        _stackBack2.Arrange(new Rect(4, 0, _card.DesiredSize.Width, _card.DesiredSize.Height));
        _stackBack1.Arrange(new Rect(2, 2, _card.DesiredSize.Width, _card.DesiredSize.Height));
        _card.Arrange(new Rect(4, 4, _card.DesiredSize.Width, _card.DesiredSize.Height));
        var hintTop = 4 + _card.DesiredSize.Height + 4;
        _hint.Arrange(new Rect(6, hintTop, Math.Max(_hint.DesiredSize.Width, 0), _hint.DesiredSize.Height));
        return finalSize;
    }

    protected override Visual GetVisualChild(int index)
        => index switch
        {
            0 => _stackBack2,
            1 => _stackBack1,
            2 => _card,
            _ => _hint,
        };

    protected override int VisualChildrenCount => 4;

    public override GeneralTransform GetDesiredTransform(GeneralTransform transform)
    {
        var result = new GeneralTransformGroup();
        result.Children.Add(base.GetDesiredTransform(transform));
        result.Children.Add(new TranslateTransform(_offset.X + PointerOffsetX, _offset.Y + PointerOffsetY));
        return result;
    }

    private static Border StackLayer(Brush background, double inset) => new()
    {
        Background = background,
        CornerRadius = new CornerRadius(8),
        Opacity = 0.5,
        Margin = new Thickness(inset, inset, 0, 0),
        Visibility = Visibility.Collapsed,
        IsHitTestVisible = false,
    };

    private static Brush Brush(FrameworkElement owner, string key, Color fallback)
        => owner.TryFindResource(key) as Brush ?? new SolidColorBrush(fallback);
}
