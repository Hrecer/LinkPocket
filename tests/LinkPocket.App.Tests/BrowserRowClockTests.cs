using System;
using System.Threading.Tasks;
using LinkPocket.I18n;
using LinkPocket.ViewModels;
using Xunit;

namespace LinkPocket.App.Tests;

/// <summary>
/// 浏览页时间列口径的回归闸：**存 UTC、画本地**（全库唯一时间出口 <see cref="UiClock"/> 的入参必须是本地时间）。
/// </summary>
/// <remarks>
/// <para>
/// 症状来源（实测截图）：主栏列表的「最后更新 / 最后查看 / 创建时间」比右栏详情**慢整整一个时区差**
/// （东八区 = 8 小时，跨日时连日期一起错），看着就是"列表不更新 / 两边数据不同步"。
/// 数据其实每次都刷出来了（见 <c>BrowserViewModelTests.后台刷新_原地写回行时…</c>），
/// 只是列表把库里的 UTC 原值直接画了出去，而右栏 / 详情页 / 搜索 / 智能列表 / 回收站 / 去重明细
/// 一律先 <c>ToLocalTime()</c>。
/// </para>
/// <para>
/// 断言口径：列表格画出的串 = 右栏同一时刻画出的串（<c>UiClock.Format(x.ToLocalTime())</c>），
/// 并且值身份里承载的<b>时刻本身</b>就是本地时刻——后者与时区偏移无关，换机器也不会假绿。
/// </para>
/// </remarks>
public class BrowserRowClockTests
{
    [Fact]
    public async Task 链接行_三列时间_画本地时间_与右栏补拉同一串字()
    {
        var (client, _, dbPath) = AppTestEnv.Create();
        try
        {
            var link = (await client.LinkCreateAsync("https://clock.example", title: "时钟",
                autoFetchMetadata: false)).Data!;
            await client.LinkVisitRecordAsync(link.LinkId);

            var vm = new BrowserViewModel(client);
            await vm.LoadAsync(null);
            var row = Assert.Single(vm.Rows);

            // 右栏（BrowserDetailsViewModel）经 links.get 补拉后画的正是这条公式——同一个时刻，同一串字
            var dto = await client.LinkGetAsync(link.LinkId);
            Assert.NotNull(dto.LastVisitedAt);

            Assert.Equal(UiClock.Format(dto.LastVisitedAt!.Value.ToLocalTime()), row.LastViewedAdaptive.Resolve());
            Assert.Equal(UiClock.Format(dto.LastVisitedAt!.Value.ToLocalTime()), row.LastViewedText.Resolve());
            Assert.Equal(UiClock.Format(dto.UpdatedAt.ToLocalTime()), row.ModifiedText.Resolve());
            Assert.Equal(UiClock.Format(dto.CreatedAt.ToLocalTime()), row.CreatedText.Resolve());
        }
        finally
        {
            AppTestEnv.Delete(dbPath);
        }
    }

    [Fact]
    public async Task 时间列_值身份里承载的是本地时刻_不是库里的UTC原值()
    {
        var (client, _, dbPath) = AppTestEnv.Create();
        try
        {
            var link = (await client.LinkCreateAsync("https://clock.example", title: "时钟",
                autoFetchMetadata: false)).Data!;
            await client.LinkVisitRecordAsync(link.LinkId);

            var vm = new BrowserViewModel(client);
            await vm.LoadAsync(null);
            var row = Assert.Single(vm.Rows);
            var dto = await client.LinkGetAsync(link.LinkId);

            // LocText 存的是"身份"（时刻 + 形态），取词发生在渲染边界 ⇒ 这里读出来的必须是**本地时刻**。
            // 这条断言与时区偏移无关：偏移为 0 的机器上两值本就相等，非 0 时漏换算立刻红。
            Assert.Equal(dto.LastVisitedAt!.Value.ToLocalTime(), ClockOf(row.LastViewedAdaptive));
            Assert.Equal(dto.UpdatedAt.ToLocalTime(), ClockOf(row.ModifiedText));
            Assert.Equal(dto.CreatedAt.ToLocalTime(), ClockOf(row.CreatedText));

            // 负向对照：本机时区偏移非 0 时，列表画出的串**绝不能等于**未换算的 UTC 原值
            // —— 等于即"漏了 ToLocalTime"，正是截图里"列表比右栏慢 8 小时"那一格。
            if (TimeZoneInfo.Local.GetUtcOffset(dto.LastVisitedAt!.Value) != TimeSpan.Zero)
            {
                Assert.NotEqual(UiClock.Format(dto.LastVisitedAt!.Value), row.LastViewedAdaptive.Resolve());
                Assert.NotEqual(UiClock.Format(dto.UpdatedAt), row.ModifiedText.Resolve());
            }
        }
        finally
        {
            AppTestEnv.Delete(dbPath);
        }
    }

    [Fact]
    public async Task 文件夹行_父链刷新的最后查看_同样画本地时间()
    {
        var (client, _, dbPath) = AppTestEnv.Create();
        try
        {
            var folder = (await client.FolderCreateAsync("F")).Data!;
            var link = (await client.LinkCreateAsync("https://child.example", title: "子链接",
                listId: folder.FolderId, autoFetchMetadata: false)).Data!;
            await client.LinkVisitRecordAsync(link.LinkId);   // 父链：文件夹的「最后查看 / 查看次数」同步刷新

            var vm = new BrowserViewModel(client);
            await vm.LoadAsync(null);
            var row = Assert.Single(vm.Rows);                 // 根目录只有 F
            Assert.True(row.IsFolder);
            Assert.NotNull(row.LastViewedAt);

            // 文件夹分支的右栏取的就是行上的值（无二次补拉），口径与链接行一致
            Assert.Equal(row.LastViewedAt!.Value.ToLocalTime(), ClockOf(row.LastViewedAdaptive));
            Assert.Equal(UiClock.Format(row.LastViewedAt!.Value.ToLocalTime()), row.LastViewedAdaptive.Resolve());
            Assert.Equal(UiClock.Format(row.CreatedAt.ToLocalTime()), row.CreatedText.Resolve());
        }
        finally
        {
            AppTestEnv.Delete(dbPath);
        }
    }

    [Fact]
    public async Task 从未查看的行_最后查看列画从未_不画时间()
    {
        var (client, _, dbPath) = AppTestEnv.Create();
        try
        {
            await client.LinkCreateAsync("https://never.example", title: "从未", autoFetchMetadata: false);

            var vm = new BrowserViewModel(client);
            await vm.LoadAsync(null);
            var row = Assert.Single(vm.Rows);

            Assert.Null(row.LastViewedAt);
            Assert.Equal(UiClock.Never, row.LastViewedAdaptive.Resolve());   // 哨兵句，不是 0001-01-01
        }
        finally
        {
            AppTestEnv.Delete(dbPath);
        }
    }

    /// <summary>
    /// 时间列值身份里承载的时刻（<c>@clock</c> 通道）——顺带守住"不要在构造期把时刻烤成成品文本"
    /// （烤成文本 = 冻结在取词那一刻的语言上，见 <see cref="LocValue.Clock"/> 的类型注释）。
    /// </summary>
    private static DateTime ClockOf(LocText text)
    {
        Assert.True(text.Full.IsClock, "时间列必须走时钟通道（@clock），不能是烤好的成品文本");
        return text.Full.Args.Span[0] is DateTime t
            ? t
            : throw new InvalidOperationException("时间列的值身份里没有时刻");
    }
}
