using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace LinkPocket.Views;

/// <summary>
/// 全站共用的 Toast（轻提示）栈：底部居中、多条纵向堆叠、每条自动消失。
///
/// <para>
/// <b>样式来源</b>：照搬抹茶泡芙 <c>desktop/src/components/ui/toast.ts</c> 的规格——
/// 底部 24px、条间距 8px、全圆角药丸、6px 主色圆点、浮层底 + 细边框 + 大阴影、中等字重，
/// 停留 1.8 秒后 200ms 淡出。字号按 LinkPocket 的正文字号放大到 16。
/// 颜色全部走 <c>App.*</c> 语义令牌，不写颜色字面量（架构测试卡死）。
/// </para>
/// <para>
/// <b>多条</b>：新提示在底部、旧提示向上让位，形成从上到下的药丸列；同文重复只顺延，
/// 不堆重复条；容量满时先移除最旧的一条。
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

    /// <summary>显示一条已取好词的提示（空串不显示）；可从任意线程调用。</summary>
    public void Show(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => Show(text)));
            return;
        }

        // 连续同一条（如反复点同一个被拒绝的跳转）：顺延底部那条的寿命，不堆重复条
        var last = _items.LastOrDefault();
        if (last is { IsDismissing: false } && string.Equals(last.Text, text, StringComparison.Ordinal))
        {
            last.Restart();
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

        // 容器生成前就已到期（极端：提示刚加入就被淘汰）：不再保留
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

        public void Restart()
        {
            _timer.Stop();
            _timer.Start();
        }

        public void Stop() => _timer.Stop();
    }
}
