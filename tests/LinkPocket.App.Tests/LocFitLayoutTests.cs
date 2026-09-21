using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using LinkPocket.I18n;
using LinkPocket.Views;
using Xunit;

namespace LinkPocket.App.Tests;

/// <summary>
/// 自适应在**真实布局**里的行为：文字真的被写进元素、字号真的落值、截断配 ToolTip 全文，
/// 以及本机制唯一的性能/稳定性雷区 —— **布局回环**（收敛后不再改字号）。
/// </summary>
/// <remarks>
/// <para>
/// 用真实 <c>Measure/Arrange</c> 而不是手调 <c>TryFit</c>：本机制的时序正确性
/// （"在布局之后判定、判定用的是真实可用宽"）只有走一遍布局才作数。
/// 度量用真实 <c>FormattedText</c>，不注入假实现——这里的判据是几何，不是算法。
/// </para>
/// <para>
/// ⚠️ 所有 WPF 对象的<b>创建</b>都必须在 STA 线程内（<see cref="StaPump"/> 体内）：
/// 在测试线程上 <c>new Window()</c> 之后再由 STA 线程 <c>UpdateLayout</c>，
/// 会报"调用线程无法访问此对象"（每个 WPF 元素都绑定到创建它的 Dispatcher）。
/// </para>
/// </remarks>
[Collection(LocaleStateCollection.Name)]
public sealed class LocFitLayoutTests : IDisposable
{    public void Dispose()
    {
        LocaleService.Apply(AppLocales.Default);
        LocFit.ClearCache();
    }

    /// <summary>建一个离屏宿主窗口并起一块固定宽的网格（几何 = 冻结面）。</summary>
    private static Window NewHost(double width, out Grid grid)
    {
        grid = new Grid { Width = width };
        var window = new Window
        {
            Width = width + 40,
            Height = 200,
            WindowStyle = WindowStyle.None,
            ShowInTaskbar = false,
            ShowActivated = false,
            Left = -20000,
            Top = 0,
            Content = grid,
        };
        window.Show();
        return window;
    }

    /// <summary>挂一个带自适应的 TextBlock 并跑到布局稳定。</summary>
    private static async Task<TextBlock> AddAsync(Grid grid, double width, LocText text, LocFitMode mode, double fontSize = 13)
    {
        var block = new TextBlock
        {
            Width = width,
            FontSize = fontSize,
            TextWrapping = TextWrapping.NoWrap,
        };
        LocFit.SetMode(block, mode);
        LocFit.SetText(block, text);
        grid.Children.Add(block);
        await SettleAsync(grid);
        return block;
    }

    /// <summary>把布局跑稳（多轮：自适应改字号会再触发一轮布局；必须真泵消息，见 <see cref="StaPump.PumpFor"/>）。</summary>
    private static async Task SettleAsync(Grid grid)
    {
        for (var i = 0; i < 4; i++)
        {
            grid.UpdateLayout();
            StaPump.PumpFor(20);
            await Task.Yield();
        }
    }

    [Fact]
    public async Task 英文长句在冻结几何里自己让位_不撑破元素宽()
    {
        await StaPump.RunAsync(async () =>
        {
            var width = 100.0;
            var window = NewHost(width, out var grid);
            var block = await AddAsync(grid, width, LocText.Of(LocValue.Literal("Delete permanently")), LocFitMode.ShrinkThenEllipsis);

            Assert.Equal(width, block.ActualWidth, 1);                // 几何一寸未动
            Assert.True(block.FontSize < 13, $"长句应当缩字号，实际 FontSize={block.FontSize}");
            Assert.True(block.FontSize >= LocFit.MinFloor(13) - 1e-6);
            window.Close();
        });
    }

    [Fact]
    public async Task 稳态后布局再跑也不再改字号_值不变不写()
    {
        await StaPump.RunAsync(async () =>
        {
            var window = NewHost(200, out var grid);
            var block = await AddAsync(grid, 200, LocText.Of(LocValue.Literal("Delete permanently")), LocFitMode.ShrinkThenEllipsis);

            var settled = block.FontSize;
            var writesAtSettle = LocFit.FontSizeWrites;
            var fitsAtSettle = LocFit.FitEvaluations;

            await SettleAsync(grid);
            await SettleAsync(grid);

            Assert.Equal(settled, block.FontSize);
            Assert.Equal(writesAtSettle, LocFit.FontSizeWrites);   // 回环防线③：稳定后一次都不写
            Assert.Equal(fitsAtSettle, LocFit.FitEvaluations);     // 度量缓存命中，连算都不再算
            window.Close();
        });
    }

    [Fact]
    public async Task 放得下的中文文案一字不动_字号与几何都保持原样()
    {
        await StaPump.RunAsync(async () =>
        {
            var window = NewHost(300, out var grid);
            var block = await AddAsync(grid, 300, LocText.Of(LocValue.Literal("恢复默认外观")), LocFitMode.ShrinkThenEllipsis);

            Assert.Equal(13, block.FontSize);
            Assert.Equal("恢复默认外观", block.Text);
            Assert.False(LocFit.IsTruncated(block));
            window.Close();
        });
    }

    [Fact]
    public async Task 触底才截断_并且截断时一定配全文提示()
    {
        await StaPump.RunAsync(async () =>
        {
            var window = NewHost(90, out var grid);
            const string longText = "Delete permanently and irreversibly";
            var block = await AddAsync(grid, 90, LocText.Of(LocValue.Literal(longText)), LocFitMode.ShrinkThenEllipsis);

            Assert.Equal(LocFit.MinFloor(13), block.FontSize, 3);
            Assert.True(LocFit.IsTruncated(block), "窄到下限也放不下时必须截断");
            Assert.Equal(TextTrimming.CharacterEllipsis, block.TextTrimming);
            Assert.Equal(longText, ToolTipService.GetToolTip(block));   // 截断与全文成对出现
            window.Close();
        });
    }

    [Fact]
    public async Task 短式优先于缩字号_能换短句就不动字号()
    {
        await StaPump.RunAsync(async () =>
        {
            var grid = new Grid();
            var probe = new TextBlock { FontSize = 13 };
            grid.Children.Add(probe);
            var window = new Window
            {
                Width = 600, Height = 200, WindowStyle = WindowStyle.None,
                ShowInTaskbar = false, ShowActivated = false, Left = -20000, Top = 0, Content = grid,
            };
            window.Show();

            // 宽度从真实度量推出来（不写死像素）：取"全长放不下、短式放得下"的区间上端，
            // 这样短式恰好能用、字号一步都不用让 —— 正是降级链第 ② 步该起作用的场景。
            var full = Measure(probe, Loc.T("trash.menu.purge"));
            var shortForm = Measure(probe, Loc.T("trash.menu.purge#short"));
            Assert.True(shortForm < full, "短式必须真的比全长短，否则本用例不成立");
            var width = Math.Floor(full) - 1;

            var block = new TextBlock { Width = width, FontSize = 13, TextWrapping = TextWrapping.NoWrap };
            LocFit.SetMode(block, LocFitMode.ShrinkThenEllipsis);
            LocFit.SetText(block, LocText.Key("trash.menu.purge", "trash.menu.purge#short"));
            grid.Children.Add(block);
            await SettleAsync(grid);

            Assert.True(LocFit.IsShortForm(block), $"应当走短式形态（可用宽 {width:F0}，全长需 {full:F0}，短式需 {shortForm:F0}）");
            Assert.Equal(13, block.FontSize);                    // 短式放得下 ⇒ 字号不让步
            Assert.Equal(Loc.T("trash.menu.purge#short"), block.Text);
            window.Close();
        });
    }

    private static double Measure(TextBlock like, string text)
    {
        var family = like.GetValue(System.Windows.Documents.TextElement.FontFamilyProperty) as System.Windows.Media.FontFamily;
        return LinkPocket.Theming.Fonts.TextWidthProbe.Width(new LinkPocket.Theming.Fonts.TextWidthProbe.MetricsRequest(
            text, family?.Source ?? string.Empty, like.FontSize, like.FontWeight, like.FontStretch, 1.0));
    }

    [Fact]
    public async Task 按钮的文案写进其内容属性_盒子本身仍是冻结几何()
    {
        await StaPump.RunAsync(async () =>
        {
            var grid = new Grid { Width = 200 };
            var button = new Button { Width = 200, Height = 32, Padding = new Thickness(14, 0, 14, 0), FontSize = 13 };
            LocFit.SetMode(button, LocFitMode.ShrinkThenEllipsis);
            LocFit.SetText(button, LocText.Of(LocValue.Literal("Delete permanently")));
            grid.Children.Add(button);
            var window = new Window
            {
                Width = 240, Height = 200, WindowStyle = WindowStyle.None,
                ShowInTaskbar = false, ShowActivated = false, Left = -20000, Top = 0, Content = grid,
            };
            window.Show();
            await SettleAsync(grid);

            Assert.Equal(32, button.ActualHeight, 1);           // 48 处 Height=32 的那一类：高不许变
            Assert.Equal(200, button.ActualWidth, 1);
            Assert.Equal("Delete permanently", button.Content);  // 放得下的内容原样（未截断）
            Assert.True(button.FontSize <= 13);
            window.Close();
        });
    }

    [Fact]
    public async Task 关掉自适应后元素回到完全不受影响的状态()
    {
        await StaPump.RunAsync(async () =>
        {
            var window = NewHost(90, out var grid);
            var block = await AddAsync(grid, 90, LocText.Of(LocValue.Literal("Delete permanently")), LocFitMode.Off);

            Assert.Equal(13, block.FontSize);
            Assert.Equal("", block.Text);                        // Off = 本行为一个字都不写
            Assert.Equal(TextTrimming.None, block.TextTrimming);
            Assert.Null(ToolTipService.GetToolTip(block));
            window.Close();
        });
    }

    /// <summary>切语言后短式/全长跟着换：取词发生在渲染边界（表里两条通道都活着）。</summary>
    [Fact]
    public async Task 切语言后自适应文案跟着换语言()
    {
        await StaPump.RunAsync(async () =>
        {
            var window = NewHost(200, out var grid);
            var block = await AddAsync(grid, 200, LocText.Key("trash.menu.purge", "trash.menu.purge#short"), LocFitMode.ShrinkThenEllipsis);

            var zh = block.Text;
            LocaleService.Apply(AppLocale.En);
            // WPF 的绑定会因语言版本失效而重投 LocText；这里直接重投一次，验的是"取词在渲染边界"
            LocFit.SetText(block, LocText.Key("trash.menu.purge", "trash.menu.purge#short"));
            await SettleAsync(grid);

            Assert.NotEqual(zh, block.Text);
            Assert.DoesNotContain("永久", block.Text, StringComparison.Ordinal);
            window.Close();
        });
    }

    // ── 内距换算（可用宽必须减掉内距与描边，否则"刚好放得下"的文字会被内距挤出去） ──

    [Fact]
    public async Task 可用宽等于实测宽减左右内距与描边()
    {
        await StaPump.RunAsync(() =>
        {
            var button = new Button
            {
                Width = 300,
                Height = 32,
                Padding = new Thickness(16, 0, 14, 0),
                BorderThickness = new Thickness(1, 0, 1, 0),
            };
            button.Measure(new Size(300, 32));
            button.Arrange(new Rect(0, 0, 300, 32));

            Assert.Equal(32.0, LocFit.HorizontalInsets(button));
            Assert.Equal(268.0, LocFit.AvailableWidth(button));
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task 未参与布局的元素可用宽为零_不产生负值()
    {
        await StaPump.RunAsync(() =>
        {
            var button = new Button { Padding = new Thickness(20, 0, 20, 0) };

            Assert.Equal(0, LocFit.AvailableWidth(button));
            return Task.CompletedTask;
        });
    }
}
