using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace LinkPocket.Views;

/// <summary>
/// 全站共用的 Toast（轻提示）栈：底部居中、每次调用新增一条、各自自动消失。
///
/// <para>
/// <b>样式与行为来源</b>：照搬抹茶泡芙 <c>desktop/src/components/ui/toast.ts</c>——
/// 底部 24px、条间距 8px、全圆角药丸、6px 主色圆点、13px 中等字重、内距 14,8，
/// 停留 1.8 秒后 200ms 淡出。颜色全部走 <c>App.*</c> 语义令牌（架构测试卡死）。
/// </para>
/// <para>
/// <b>堆叠</b>：不做同文合并——每调一次 <see cref="Show"/> 就新增一条，旧条目向上让位，
/// 各自按自己的寿命消失。连续点击按钮时能看到一条条叠起来，而不是被后一条顶掉或一直停在原地。
/// 最多保留 <see cref="MaxVisible"/> 条，超出时淘汰最旧的一条（抹茶泡芙原实现靠 1.8 秒寿命
/// 自然收敛；这里加一个硬上限，避免快速连点把屏幕堆满）。
/// </para>
/// <para>
/// <b>职责边界</b>：控件只管"显示什么、何时消失"；文案由调用方经 i18n 取好
/// （<c>IUiNoticeService</c> 的约定）。
/// </para>
/// </summary>
public partial class NoticeBar : UserControl
{
    /// <summary>每条提示的停留时长（与抹茶泡芙的 toast 一致：1.8 秒）。</summary>
    private static readonly TimeSpan Lifetime = TimeSpan.FromMilliseconds(1800);

    /// <summary>同时可见的最大条数（超出的从最旧一条开始淘汰）。</summary>
    private const int MaxVisible = 3;

    /// <summary>淡出时长（与抹茶泡芙的 toast 一致：200ms）。</summary>
    private static readonly TimeSpan ExitDuration = TimeSpan.FromMilliseconds(200);

    private readonly ObservableCollection<NoticeEntry> _items = new();

    public NoticeBar()
    {
        InitializeComponent();
        ItemsHost.ItemsSource = _items;
    }

    /// <summary>显示一条已取好词的提示（空串不显示）；可从任意线程调用。每次调用都新增一条。</summary>
    public void Show(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => Show(text)));
            return;
        }

        while (_items.Count >= MaxVisible) Remove(_items[0]);

        var entry = new NoticeEntry(text, this);
        _items.Add(entry);
        entry.Start();
    }

    /// <summary>到期后播放淡出并移除；重复调用无害。</summary>
    private void Dismiss(NoticeEntry entry)
    {
        if (entry.IsDismissing) return;
        entry.IsDismissing = true;
        entry.Stop();

        var card = entry.Card;
        if (card is null || !SystemParameters.ClientAreaAnimation)
        {
            Remove(entry);
            return;
        }

        var fade = new DoubleAnimation(card.Opacity, 0, ExitDuration);
        fade.Completed += (_, _) => Remove(entry);
        card.BeginAnimation(UIElement.OpacityProperty, fade);
    }

    private void Remove(NoticeEntry entry)
    {
        entry.Stop();
        entry.Card = null;
        _items.Remove(entry);
    }

    private void Card_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Border { DataContext: NoticeEntry entry } card) return;
        entry.Card = card;

        // 容器生成前就已到期（极端：提示刚加入就被移除）：不再保留
        if (entry.IsDismissing) Remove(entry);
    }

    private void Card_Unloaded(object sender, RoutedEventArgs e)
    {
        if (sender is Border { DataContext: NoticeEntry entry } card && ReferenceEquals(entry.Card, card))
            entry.Card = null;
    }

    /// <summary>控件离开可视树（窗口关闭）：停掉所有计时器，避免回调到已卸载的控件。</summary>
    private void NoticeBar_Unloaded(object sender, RoutedEventArgs e)
    {
        foreach (var entry in _items.ToList())
        {
            entry.Stop();
            entry.Card = null;
        }

        _items.Clear();
    }

    /// <summary>栈里的一条：文案、容器引用与自己的计时器。</summary>
    private sealed class NoticeEntry
    {
        private readonly DispatcherTimer _timer;
        private readonly NoticeBar _owner;

        public NoticeEntry(string text, NoticeBar owner)
        {
            Text = text;
            _owner = owner;
            _timer = new DispatcherTimer { Interval = Lifetime };
            _timer.Tick += (_, _) => _owner.Dismiss(this);
        }

        public string Text { get; }

        public bool IsDismissing { get; set; }

        public Border? Card { get; set; }

        public void Start() => _timer.Start();

        public void Stop() => _timer.Stop();
    }
}
