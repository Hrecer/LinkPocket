using System;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using LinkPocket.I18n;
using LinkPocket.Views;
using Xunit;

namespace LinkPocket.Tests;

/// <summary>
/// 承重假设的回归闸：<b>语言一变，自适应通道必须自己把显示文字换掉</b>——不靠任何外部驱动。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么必须有这一支</b>：这条通道的失效链曾经是坏的，而坏法极具误导性——绑定确实重算了、
/// 屏幕上的字却一个字不变，于是履历上留下了三件"驱动补丁"（<c>LocaleService.AfterApply</c> 钩子、
/// <c>RefreshAll</c> 遍历、<c>DispatcherPriority</c> 排队）。真相是两件事：
/// </para>
/// <list type="number">
/// <item>
/// 那条 <c>MultiBinding</c> 的第二路指向模型上的<b>普通属性</b>，WPF 重算时会<b>复用它的缓存值、
/// 不再调 getter</b>——所以"版本子绑定"根本没把新语言送进来；补丁再怎么驱动也拿不到新文案。
/// 修法：由 <see cref="FitValueResolver"/> 把版本<b>烙进产出值</b>，属性系统的变更推送自己会传到底。
/// </item>
/// <item>
/// 日期曾经在构造期就被格式化成文本（<c>LocValue.Literal</c>），等于<b>冻结</b>在取词那一刻的语言上。
/// 修法：<see cref="UiClock.Text"/> 改用 <see cref="LocValue.Clock"/>——存"时刻 + 形态"，渲染边界才取词。
/// </item>
/// </list>
/// <para>
/// 本用例把这两条钉住：模型一个字都不改，只切语言，显示文字必须跟着换、并且能换回来。
/// 谁要是把"版本子绑定"或"构造期格式化"改回去，这里会红。
/// </para>
/// </remarks>
public sealed class LocFitChannelTests
{
    /// <summary>与 <c>BrowserRowViewModel</c> 同构：成员是发通知的普通属性，值是活的 <see cref="LocText"/>。</summary>
    private sealed class RowLike
    {
        /// <summary>每次读都现造（与 <c>UiClock.Text(ModifiedAt)</c> / <c>LocText.Key(...)</c> 同构）。</summary>
        public LocText ModifiedText => UiClock.Text(new DateTime(2026, 9, 20, 10, 50, 0, DateTimeKind.Local));

        /// <summary>键通道的活值（与 <c>BrowserRowViewModel.LastViewedAdaptive</c> 同构）。</summary>
        public LocText NeverText => LocText.Key("clock.never", "clock.never#short");
    }

    /// <summary><c>{loc:FitValue X}</c> 的真实形状：见 <see cref="FitValueResolver"/>。</summary>
    private static MultiBinding FitValueBinding(string path)
    {
        var mb = new MultiBinding { Converter = FitValueResolver.Instance, Mode = BindingMode.OneWay };
        mb.Bindings.Add(new Binding(nameof(LocTable.Version))
        {
            Source = LocTable.Instance,
            Mode = BindingMode.OneWay,
        });
        mb.Bindings.Add(new Binding(path) { Mode = BindingMode.OneWay });
        return mb;
    }

    /// <summary>把一段 UI 逻辑放到带 Dispatcher 的 STA 线程上跑（WPF 绑定要有消息泵）。</summary>
    /// <remarks>
    /// <b>跑完必须把那个 Dispatcher 关掉</b>：每个 <c>Dispatcher</c> 都会建一个隐藏窗口（消息泵的宿主），
    /// 线程退出时它不会自己销毁。一个测试漏掉它，后续任何"再建一个 Dispatcher / 再开一个窗口"的代码
    /// 都会失败，而且报的是 <c>Win32Exception(8) "内存资源不足"</c>——<b>报的是内存，实际是窗口句柄耗尽</b>
    /// （实测：本机先后 24 个残留 dotnet 进程时，连 <c>FormattedText</c> 取宽都会抛这个错）。
    /// 所以这里 <c>InvokeShutdown</c> + 泵到 <c>HasShutdownFinished</c>，把句柄还回去。
    /// </remarks>
    private static void OnUiThread(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { failure = ex; }
            finally
            {
                try
                {
                    Dispatcher.CurrentDispatcher.InvokeShutdown();
                    // 泵到关停真正落地，否则隐藏窗口还在，句柄仍被占着
                    while (!Dispatcher.CurrentDispatcher.HasShutdownFinished) Pump();
                }
                catch { /* 关停失败不掩盖真正的失败 */ }
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join(TimeSpan.FromSeconds(30));
        if (failure is not null) throw failure;
    }

    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(new Action(() => frame.Continue = false), DispatcherPriority.Background);
        Dispatcher.PushFrame(frame);
    }

    [Fact]
    public void 切语言_自适应通道自己把显示文字换掉_再切回来也换回来()
    {
        OnUiThread(() =>
        {
            LocaleService.Apply(AppLocale.ZhCn);
            try
            {
                var row = new RowLike();
                var dateCell = new TextBlock { DataContext = row };
                var neverCell = new TextBlock { DataContext = row };
                foreach (var (cell, path) in new[]
                         {
                             (dateCell, nameof(RowLike.ModifiedText)),
                             (neverCell, nameof(RowLike.NeverText)),
                         })
                {
                    LocFit.SetMode(cell, LocFitMode.Shrink);
                    cell.SetBinding(LocFit.TextProperty, FitValueBinding(path));
                    cell.SetBinding(TextBlock.TextProperty, LocFitResolver.BuildChosenBinding());
                }

                // 进真实可视树并跑布局：可用宽 > 0 才会走降级链（否则 Project 走"未布局"分支）。
                var host = new Window
                {
                    Width = 600,
                    Height = 300,
                    ShowActivated = false,
                    Content = new StackPanel { Children = { dateCell, neverCell } },
                };
                host.Show();
                Pump();
                host.UpdateLayout();
                Pump();

                var zhDate = dateCell.Text ?? "";
                var zhNever = neverCell.Text ?? "";

                // 关键：模型一个字都不改，只切语言。
                LocaleService.Apply(AppLocale.En);
                Pump();
                host.UpdateLayout();
                Pump();
                var enDate = dateCell.Text ?? "";
                var enNever = neverCell.Text ?? "";

                LocaleService.Apply(AppLocale.ZhCn);
                Pump();
                host.UpdateLayout();
                Pump();
                var backDate = dateCell.Text ?? "";

                host.Close();

                Assert.Equal("2026-09-20 10:50", zhDate);
                Assert.Equal("从未", zhNever);
                Assert.Equal("09/20/2026 10:50 AM", enDate);
                Assert.Equal("Never", enNever);
                Assert.Equal("2026-09-20 10:50", backDate);
            }
            finally
            {
                LocaleService.Apply(AppLocales.Default);   // 语言是进程级状态，不许漏给下一个用例
            }
        });
    }

    /// <summary>
    /// **`AvailableWidth` 不许把"本机制自己缩小的字号"当成下一轮的上限**（只缩不涨的死循环）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 实测事故（见 <c>WARNINGS</c> 109）：命令栏药丸的"可用宽"被算成**壳宽÷2 − 内距**——
    /// 因为元素实测宽是按**缩过的字号**算的，而 <c>min(元素实得, 壳内容区)</c> 取了它。
    /// 后果是死循环：第一次缩过头之后可用宽跟着字号一起变小，**把壳加宽也救不回来**
    /// （实测把壳从 100 加到 155，字号一点没长）。
    /// </para>
    /// <para>
    /// 本用例把两件事钉住：① 本行写过字号之后，可用宽 = **壳的内容区**（不随字号缩小）；
    /// ② 没写过时仍取 `min`（那条是为了挡"大容器骗人"与"行内组自指"，不能被这条修法带走）。
    /// </para>
    /// </remarks>
    [Fact]
    public void 缩过字号之后可用宽不跟着字号缩小()
    {
        OnUiThread(() =>
        {
            var text = new TextBlock { Text = "Delete permanently", FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
            var group = new StackPanel { Orientation = Orientation.Horizontal };
            group.Children.Add(new Border { Width = 16, Height = 16, VerticalAlignment = VerticalAlignment.Center });
            group.Children.Add(text);

            var shell = new Button
            {
                Width = 200,
                Height = 32,
                Padding = new Thickness(12, 0, 12, 0),
                Content = group,
            };

            var host = new Window
            {
                Width = 600, Height = 200, ShowActivated = false,
                Left = -20000, Top = -20000,
                Content = new StackPanel { Children = { shell } },
            };
            host.Show();
            for (var i = 0; i < 5; i++) { Pump(); host.UpdateLayout(); }

            // 期望的可用宽 = 壳 200 − 内距 24 − 图标 16 = 160（与字号无关；模板内部件另有约 2px 内距，
            // 所以给 ±3 的余量——这里量的是"不随字号变"，不是像素精度）
            var before = LocFit.AvailableWidth(text);

            // 模拟"机制缩过字号"：写一个更小的字号，再跑一轮布局
            LocFit.PlaceFontSize(text, 6.0);
            for (var i = 0; i < 3; i++) { Pump(); host.UpdateLayout(); }
            var after = LocFit.AvailableWidth(text);

            host.Close();

            Assert.InRange(before, 157, 163);   // 壳 200 − 内距 24 − 图标 16 = 160（模板内部件另有约 2px 内距）
            Assert.True(after >= before - 1,
                $"缩过字号之后可用宽不许变小（那会让字号再也长不回来）：缩前 {before:F1} → 缩后 {after:F1}");
            Assert.True(text.ActualWidth < 160,
                $"前提：缩到 6pt 后元素实测宽确实变小了（实际 {text.ActualWidth:F1}）——否则本用例在空跑");
        });
    }
}
