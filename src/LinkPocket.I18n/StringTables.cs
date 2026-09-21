using System;
using System.Collections.Generic;
using System.Linq;

namespace LinkPocket.I18n;

/// <summary>一条文案：同一键在两种语言里各一份文本（两表键集合必须完全相等，由架构护栏 G4 卡住）。</summary>
public sealed record StringRow(string Key, string Zh, string En);

/// <summary>
/// 字符串表（界面文案的唯一事实来源）。写在代码里而不是 <c>.resx</c>：与
/// <c>Theming/Color/SurfaceAnchors</c> 的行表同族——零文件丢失面、零 satellite assembly，
/// 且护栏可以直接解析本表做"键对称 / 空值 / 重复"断言。
/// </summary>
/// <remarks>
/// 键规范 <c>域.面.角色</c>：段首小写、其后允许 camelCase（restoreToRoot），全 ASCII。复数与短式一律**另立一条完整键**（<c>…#one</c> / <c>…#short</c>），
/// 不许在调用点拼字符串——拼出来的键护栏查不到，等于把"缺键"从编译期推到了运行时。
/// </remarks>
public static class StringTables
{
    /// <summary>行表（新增文案只许从这里进）。</summary>
    public static IReadOnlyList<StringRow> Rows { get; } = new[]
    {
        // 虚根显示名：同时是根级保留名（禁在根级占用），见 Kernel/Services/FolderNaming
        new StringRow("nav.root.bookmarks", "全部书签", "Bookmarks"),
        new StringRow("nav.root.trash", "回收站", "Trash"),

        // 时间哨兵（真实时间戳的格式在 UiClock，不在这里）
        new StringRow("clock.never", "从未", "Never"),

        // 路径哨兵：数据层发 @unknown（断链不伪装成根），这里投影成人话
        new StringRow("path.unknown", "未知目录", "Unknown location"),
        new StringRow("path.copied", "已复制路径（绝对路径，跨语言可粘回）", "Path copied (canonical — pastes in any language)"),

        // ── 跨页共用（导航键与面板标题在多页重复出现，一条键一处译）──
        new StringRow("nav.back.tip", "后退 (Backspace)", "Back (Backspace)"),
        new StringRow("nav.forward.tip", "前进 (Alt+→)", "Forward (Alt+→)"),
        new StringRow("nav.up.tip", "返回上级 (Alt+↑)", "Up one level (Alt+↑)"),
        new StringRow("nav.panel.title", "导航", "Navigation"),
        new StringRow("nav.item.browser", "浏览", "Browse"),
        new StringRow("nav.item.search", "搜索", "Search"),
        new StringRow("nav.item.smartLists", "智能列表", "Smart Lists"),
        new StringRow("nav.item.tools", "工具", "Tools"),
        new StringRow("nav.item.trash", "回收站", "Trash"),
        new StringRow("nav.item.settings", "设置", "Settings"),

        // ── 共享名词：列头与右栏详情行用的是同一批词，一条键一处译（"最后更新"在三页里必须是同一个词）──
        new StringRow("ui.noun.name", "名称", "Name"),
        new StringRow("ui.noun.location", "位置", "Location"),
        new StringRow("ui.noun.locatedIn", "所在位置", "Located in"),
        new StringRow("ui.noun.updatedAt", "最后更新", "Updated"),
        new StringRow("ui.noun.lastVisited", "最后查看", "Last viewed"),
        new StringRow("ui.noun.visitCount", "查看次数", "Views"),
        new StringRow("ui.noun.viewTotal", "累计查看", "Total views"),
        new StringRow("ui.noun.createdAt", "创建时间", "Created"),
        new StringRow("ui.noun.type", "类型", "Type"),
        new StringRow("ui.noun.origin", "原位置", "Original location"),
        new StringRow("ui.noun.deletedAt", "删除时间", "Deleted"),
        new StringRow("ui.noun.url", "网址", "URL"),
        new StringRow("ui.noun.linkCount", "链接数", "Links"),
        new StringRow("ui.noun.duplicateUrl", "重复地址", "Duplicate URL"),
        new StringRow("ui.noun.duplicateCount", "重复数", "Count"),
        new StringRow("ui.noun.id", "ID", "ID"),
        new StringRow("ui.sort.tip", "按{0}排序", "Sort by {0}"),
        new StringRow("common.type.folder", "文件夹", "Folder"),
        new StringRow("common.type.link", "链接", "Link"),
        new StringRow("menu.selectAll", "全选", "Select all"),
        new StringRow("menu.refresh", "刷新", "Refresh"),

        // ── 回收站页（试点：XAML 骨架全量走取词）──
        new StringRow("trash.menu.open", "打开", "Open"),
        new StringRow("trash.menu.copyLink", "复制链接", "Copy link"),
        new StringRow("trash.menu.restore", "还原", "Restore"),
        new StringRow("trash.menu.restoreToRoot", "还原到根目录", "Restore to root"),
        new StringRow("trash.menu.purge", "永久删除", "Delete permanently"),
        new StringRow("trash.selection.count", "已选中 {0} 项", "{0} selected"),
        new StringRow("trash.status.count", "{0} · {1} 项", "{0} · {1} items"),
        new StringRow("trash.btn.restore.tip", "还原选中项到原位置 (Ctrl+R)", "Restore the selection to its original location (Ctrl+R)"),
        new StringRow("trash.btn.restoreToRoot.tip", "还原选中项到根目录 (Ctrl+Shift+R)", "Restore the selection to the root (Ctrl+Shift+R)"),
        new StringRow("trash.btn.purge.tip", "永久删除选中项 (Del)（文件夹将连同内部全部内容删除，不可恢复）", "Delete the selection permanently (Del). Folders take their whole subtree with them; this cannot be undone."),
        new StringRow("trash.empty.title", "回收站是空的", "Trash is empty"),
        new StringRow("trash.empty.hint", "删除的书签和文件夹会出现在这里，并保留删除时的位置",
            "Deleted bookmarks and folders show up here, keeping the location they were removed from."),
        new StringRow("trash.footnote",
            "回收站内容不占用书签计数；双击书签打开详情（只读），双击文件夹进入单元；回收站只读，站内不可搬移",
            "Trash items don't count toward your bookmarks. Double-click a bookmark for its read-only details, or a folder to enter it. The trash itself is read-only — nothing can be moved inside it."),
    };

    /// <summary>全部键（护栏核对"引用点都存在"与"两表对称"用）。</summary>
    public static IEnumerable<string> Keys => Rows.Select(r => r.Key);

    /// <summary>按语言取一份字典。重复键即抛——对称性在源码层就守住，不留到运行时。</summary>
    public static IReadOnlyDictionary<string, string> For(AppLocale locale)
    {
        var map = new Dictionary<string, string>(Rows.Count, StringComparer.Ordinal);
        foreach (var row in Rows)
        {
            var text = locale == AppLocale.En ? row.En : row.Zh;
            if (string.IsNullOrWhiteSpace(text))
                throw new InvalidOperationException($"文案为空：{row.Key}（{locale.CodeOf()}）");
            if (!map.TryAdd(row.Key, text))
                throw new InvalidOperationException($"字符串表键重复：{row.Key}");
        }
        return map;
    }
}
