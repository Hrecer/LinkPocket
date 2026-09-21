using System;
using System.Collections.Generic;
using LinkPocket.Models;
using Xunit;

namespace LinkPocket.App.Tests;

/// <summary>
/// 结果集「渲染等价」判定（<see cref="LinkItem.SameSequence"/>）：VM 在静默刷新后据此决定
/// **要不要通知视图整表重建**——工厂模式重建行（N 行 × 单元格）是同步主线程重活，内容未变时
/// 重建纯属白烧（低性能设备切到搜索页偶发明显卡顿）。
/// 判定必须"宁可重建不可漏更新"：只有**全部展示字段与顺序都一致**才算等价。
/// </summary>
public class LinkItemTests
{
    private static LinkItem Row(string id, string title = "标题", int visits = 0, string? listId = null) => new()
    {
        LinkId = id,
        Url = "https://example.com/" + id,
        Title = title,
        Description = "描述",
        FaviconUrl = "https://example.com/f.ico",
        ListId = listId,
        VisitCount = visits,
        IsImportant = false,
        LastVisitedAt = null,
        CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        UpdatedAt = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc),
    };

    [Fact]
    public void 全字段一致_不同实例_视为等价()
    {
        var a = new List<LinkItem> { Row("L1"), Row("L2", visits: 3) };
        var b = new List<LinkItem> { Row("L1"), Row("L2", visits: 3) };

        Assert.True(LinkItem.SameSequence(a, b));
    }

    [Fact]
    public void 顺序不同_不等价()
    {
        var a = new List<LinkItem> { Row("L1"), Row("L2") };
        var b = new List<LinkItem> { Row("L2"), Row("L1") };

        Assert.False(LinkItem.SameSequence(a, b));
    }

    [Fact]
    public void 任一展示字段变化_不等价()
    {
        var baseline = new List<LinkItem> { Row("L1") };

        Assert.False(LinkItem.SameSequence(baseline, new List<LinkItem> { Row("L1", title: "改过的标题") }));
        Assert.False(LinkItem.SameSequence(baseline, new List<LinkItem> { Row("L1", visits: 9) }));

        var moved = Row("L1");
        moved.Url = "https://example.com/moved";
        Assert.False(LinkItem.SameSequence(baseline, new List<LinkItem> { moved }));
    }

    [Fact]
    public void 数量不同或空集_不等价()
    {
        var one = new List<LinkItem> { Row("L1") };
        var two = new List<LinkItem> { Row("L1"), Row("L2") };

        Assert.False(LinkItem.SameSequence(one, two));
        Assert.False(LinkItem.SameSequence(null, one));       // null（无行）与有行不等价
        Assert.False(LinkItem.SameSequence(one, null));
        Assert.True(LinkItem.SameSequence(null, null));        // 两边都无行 = 等价（不必重建）
    }
}
