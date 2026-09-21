using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;

namespace LinkPocket.Input;

/// <summary>页面标识（键位总表按页面分组，**一页一组、互不互通**）。</summary>
public enum ShortcutPage
{
    Browser,
    Trash,
    Search,
    SmartLists,
    Tools,
    Settings,
}

/// <summary>动作 id（总表与各页的命令映射共用同一批常量，避免手打错）。</summary>
public static class ShortcutAction
{
    // 浏览页
    public const string BrowserCut = "browser.cut";
    public const string BrowserCopy = "browser.copy";
    public const string BrowserPaste = "browser.paste";
    public const string BrowserSelectAll = "browser.selectAll";
    public const string BrowserDelete = "browser.delete";
    public const string BrowserRename = "browser.rename";
    public const string BrowserOpen = "browser.open";
    public const string BrowserGoBack = "browser.goBack";
    public const string BrowserGoUp = "browser.goUp";
    public const string BrowserGoForward = "browser.goForward";
    public const string BrowserRefresh = "browser.refresh";
    public const string BrowserFocusPath = "browser.focusPath";
    public const string BrowserExpandTreeToCurrent = "browser.expandTreeToCurrent";
    public const string BrowserNewFolder = "browser.newFolder";
    public const string BrowserCopyPath = "browser.copyPath";
    public const string BrowserUndo = "browser.undo";
    public const string BrowserRedo = "browser.redo";
    public const string BrowserContextMenu = "browser.contextMenu";
    public const string BrowserMoveUp = "browser.moveUp";
    public const string BrowserMoveDown = "browser.moveDown";
    public const string BrowserSelectLast = "browser.selectLast";
    public const string BrowserTreeUp = "browser.treeUp";
    public const string BrowserTreeDown = "browser.treeDown";
    public const string BrowserTreeCollapse = "browser.treeCollapse";
    public const string BrowserTreeExpand = "browser.treeExpand";
    public const string BrowserEscape = "browser.escape";
    public const string BrowserNavigateToSearch = "browser.navigateToSearch";

    // 回收站
    public const string TrashGoBack = "trash.goBack";
    public const string TrashGoUp = "trash.goUp";
    public const string TrashGoForward = "trash.goForward";
    public const string TrashRefresh = "trash.refresh";
    public const string TrashSelectAll = "trash.selectAll";
    public const string TrashOpen = "trash.open";
    public const string TrashPurge = "trash.purge";
    public const string TrashRestoreOrigin = "trash.restoreOrigin";
    public const string TrashRestoreRoot = "trash.restoreRoot";
    public const string TrashEscape = "trash.escape";
    public const string TrashContextMenu = "trash.contextMenu";
    public const string TrashMoveUp = "trash.moveUp";
    public const string TrashMoveDown = "trash.moveDown";
    public const string TrashSelectLast = "trash.selectLast";
    public const string TrashTreeUp = "trash.treeUp";
    public const string TrashTreeDown = "trash.treeDown";
    public const string TrashTreeCollapse = "trash.treeCollapse";
    public const string TrashTreeExpand = "trash.treeExpand";
    public const string TrashFocusPath = "trash.focusPath";

    // 搜索页 / 智能列表 / 工具页
    public const string SearchRun = "search.run";
    public const string SearchOpen = "search.open";
    public const string SearchMoveUp = "search.moveUp";
    public const string SearchMoveDown = "search.moveDown";
    public const string SearchSelectLast = "search.selectLast";
    public const string SearchSelectAll = "search.selectAll";
    public const string SearchDelete = "search.delete";
    public const string SearchRefresh = "search.refresh";
    public const string SearchEscape = "search.escape";
    public const string SmartListsBack = "smartlists.back";
    public const string SmartListsOpen = "smartlists.open";
    public const string SmartListsMoveUp = "smartlists.moveUp";
    public const string SmartListsMoveDown = "smartlists.moveDown";
    public const string SmartListsSelectLast = "smartlists.selectLast";
    public const string SmartListsRefresh = "smartlists.refresh";
    public const string ToolsIdJump = "tools.idJump";
    public const string ToolsEscape = "tools.escape";
    public const string ToolsDetailUp = "tools.detailUp";
    public const string ToolsDetailDown = "tools.detailDown";
    public const string ToolsDetailSelectLast = "tools.detailSelectLast";
    public const string ToolsDetailOpen = "tools.detailOpen";
    public const string ToolsDetailRefresh = "tools.detailRefresh";

    // 设置页
    /// <summary>外观面板：取色盘打开时 Esc = 放弃本次取色（不写回色槽）。</summary>
    public const string SettingsEscape = "settings.escape";
}

/// <summary>
/// 一条快捷键声明（**只声明"键位 + 动作"**，命令由页面按动作 id 提供——总表不认识任何页面类型）。
/// <paramref name="ControlName"/> 非空 = **控件锚定绑定**：只在该控件获得焦点时生效
/// （用于输入框内的 Enter 这类"输入框内按键"：搜索框 Enter 执行搜索、ID 框 Enter 执行跳转）。
/// </summary>
public sealed record ShortcutSpec(
    string ActionId,
    Key Key,
    ShortcutScope Scope,
    string Description,
    ModifierKeys Modifiers = ModifierKeys.None,
    object? CommandParameter = null,
    string? ControlName = null,
    string ContextGate = "");

/// <summary>一个页面的键位组（页 ↔ 根作用域 一对一，组与组之间不互通）。</summary>
public sealed record ShortcutPageSpec(
    ShortcutPage Page,
    string Title,
    ShortcutScope RootScope,
    IReadOnlyList<ShortcutSpec> Specs);

/// <summary>
/// **快捷键总表（全站唯一事实源）**：每个界面有哪些快捷键，只在这里声明；
/// 页面代码里**不再出现任何键位**，只提供「动作 id → 命令」映射（<see cref="IShortcutCommands"/>）。
///
/// 隔离规则：
/// ① **一页一组作用域**，每组只由该页装配 —— 页面之间既不共享作用域、也不继承彼此的作用域链，
///    因此"所有界面的快捷键互不影响、互不互通"是结构性的（不靠约定）；
/// ② 只有浏览页注册 <see cref="ShortcutScope.Global"/>（Ctrl+E/F 切搜索页），其余页面的作用域链不含 Global；
/// ③ 控件锚定绑定（<see cref="ShortcutSpec.ControlName"/>）只挂在指定控件上，随该控件焦点进出而生效。
/// </summary>
public static class ShortcutCatalog
{
    private static readonly ShortcutSpec[] BrowserSpecs =
    {
        // —— 编辑类（两栏通用：选中集合是唯一事实源，栏不改变命令语义） ——
        new(ShortcutAction.BrowserCut, Key.X, ShortcutScope.Browser, "剪切", ModifierKeys.Control, ContextGate: "列表上下文（详情页/编辑器打开时禁用）"),
        new(ShortcutAction.BrowserCopy, Key.C, ShortcutScope.Browser, "复制", ModifierKeys.Control, ContextGate: "列表上下文（同上）"),
        new(ShortcutAction.BrowserPaste, Key.V, ShortcutScope.Browser, "粘贴", ModifierKeys.Control, ContextGate: "列表上下文（同上）"),
        new(ShortcutAction.BrowserSelectAll, Key.A, ShortcutScope.Browser, "全选", ModifierKeys.Control, ContextGate: "路径编辑/改名中禁用"),
        new(ShortcutAction.BrowserDelete, Key.Delete, ShortcutScope.Browser, "删除选中项", ContextGate: "列表上下文（同上）"),
        new(ShortcutAction.BrowserRename, Key.F2, ShortcutScope.Browser, "重命名选中项", ContextGate: "单选 + 列表上下文"),
        new(ShortcutAction.BrowserOpen, Key.Enter, ShortcutScope.Browser, "打开选中项", ContextGate: "单选 + 列表上下文"),

        // —— 导航类 ——
        new(ShortcutAction.BrowserGoBack, Key.Back, ShortcutScope.Browser, "后退", ContextGate: "路径编辑/改名中禁用"),
        new(ShortcutAction.BrowserGoUp, Key.Up, ShortcutScope.Browser, "返回上级", ModifierKeys.Alt, ContextGate: "路径编辑/改名中禁用"),
        new(ShortcutAction.BrowserGoBack, Key.Left, ShortcutScope.Browser, "后退", ModifierKeys.Alt, ContextGate: "路径编辑/改名中禁用"),
        new(ShortcutAction.BrowserGoForward, Key.Right, ShortcutScope.Browser, "前进", ModifierKeys.Alt, ContextGate: "路径编辑/改名中禁用"),
        new(ShortcutAction.BrowserRefresh, Key.F5, ShortcutScope.Browser, "刷新当前目录", ContextGate: "—"),
        new(ShortcutAction.BrowserFocusPath, Key.D, ShortcutScope.Browser, "聚焦地址栏", ModifierKeys.Alt, ContextGate: "路径编辑/改名中禁用"),
        new(ShortcutAction.BrowserExpandTreeToCurrent, Key.E, ShortcutScope.Browser, "展开到当前位置", ModifierKeys.Control | ModifierKeys.Shift, ContextGate: "—"),
        new(ShortcutAction.BrowserNewFolder, Key.N, ShortcutScope.Browser, "新建文件夹", ModifierKeys.Control | ModifierKeys.Shift, ContextGate: "列表上下文"),
        new(ShortcutAction.BrowserCopyPath, Key.C, ShortcutScope.Browser, "复制路径", ModifierKeys.Control | ModifierKeys.Shift, ContextGate: "路径编辑/改名中禁用"),
        new(ShortcutAction.BrowserUndo, Key.Z, ShortcutScope.Browser, "撤销", ModifierKeys.Control, ContextGate: "列表上下文 + 引擎栈可用"),
        new(ShortcutAction.BrowserRedo, Key.Y, ShortcutScope.Browser, "重做", ModifierKeys.Control, ContextGate: "列表上下文 + 引擎栈可用"),

        // —— 两栏通用：右键菜单键（当前选中行）——
        new(ShortcutAction.BrowserContextMenu, Key.F10, ShortcutScope.Browser, "右键菜单", ModifierKeys.Shift, ContextGate: "有选中"),
        new(ShortcutAction.BrowserContextMenu, Key.Apps, ShortcutScope.Browser, "右键菜单", ContextGate: "有选中"),

        // —— 主栏：↑/↓ 移动选中（单选）+ 滚入视口；End 到末项（Explorer 口径，边界停住）——
        new(ShortcutAction.BrowserMoveUp, Key.Up, ShortcutScope.BrowserMain, "上移选中", CommandParameter: "up"),
        new(ShortcutAction.BrowserMoveDown, Key.Down, ShortcutScope.BrowserMain, "下移选中", CommandParameter: "down"),
        new(ShortcutAction.BrowserSelectLast, Key.End, ShortcutScope.BrowserMain, "选中末项"),

        // —— 左栏：↑/↓ 按可见视觉顺序移动（与鼠标同语义：文件夹 = 选中 + 进入）；←/→ 折叠展开 ——
        new(ShortcutAction.BrowserTreeUp, Key.Up, ShortcutScope.BrowserTree, "上移树节点", CommandParameter: "up"),
        new(ShortcutAction.BrowserTreeDown, Key.Down, ShortcutScope.BrowserTree, "下移树节点", CommandParameter: "down"),
        new(ShortcutAction.BrowserTreeCollapse, Key.Left, ShortcutScope.BrowserTree, "折叠树节点"),
        new(ShortcutAction.BrowserTreeExpand, Key.Right, ShortcutScope.BrowserTree, "展开树节点"),

        // —— Esc（分层：详情页打开 → 退出详情页；有剪切态 → 取消剪切；否则清空选中）——
        new(ShortcutAction.BrowserEscape, Key.Escape, ShortcutScope.Browser, "退出详情页 / 取消剪切 / 取消选中", ContextGate: "编辑器页打开时不分发（防误触）"),

        // —— 全局（任何页面）：切到搜索页并聚焦搜索框（**只有浏览页注册全局键**）——
        new(ShortcutAction.BrowserNavigateToSearch, Key.E, ShortcutScope.Global, "搜索", ModifierKeys.Control),
        new(ShortcutAction.BrowserNavigateToSearch, Key.F, ShortcutScope.Global, "搜索", ModifierKeys.Control),
    };

    private static readonly ShortcutSpec[] TrashSpecs =
    {
        // —— 导航类（两栏通用） ——
        new(ShortcutAction.TrashGoBack, Key.Back, ShortcutScope.Trash, "后退"),
        new(ShortcutAction.TrashGoUp, Key.Up, ShortcutScope.Trash, "返回上级", ModifierKeys.Alt),
        new(ShortcutAction.TrashGoBack, Key.Left, ShortcutScope.Trash, "后退", ModifierKeys.Alt),
        new(ShortcutAction.TrashGoForward, Key.Right, ShortcutScope.Trash, "前进", ModifierKeys.Alt),
        new(ShortcutAction.TrashRefresh, Key.F5, ShortcutScope.Trash, "刷新回收站"),
        // 审计补齐（与浏览页 Alt+D 对齐）：回收站地址栏同样可点空白即编辑，缺的是键盘入口
        new(ShortcutAction.TrashFocusPath, Key.D, ShortcutScope.Trash, "聚焦地址栏", ModifierKeys.Alt,
            ContextGate: "路径编辑中禁用"),

        // —— 选择 / 打开 / 永久删除（两栏通用） ——
        new(ShortcutAction.TrashSelectAll, Key.A, ShortcutScope.Trash, "全选", ModifierKeys.Control, ContextGate: "路径编辑中禁用"),
        new(ShortcutAction.TrashOpen, Key.Enter, ShortcutScope.Trash, "打开选中项", ContextGate: "路径编辑中禁用"),
        new(ShortcutAction.TrashPurge, Key.Delete, ShortcutScope.Trash, "永久删除选中项", ContextGate: "有选中 + 覆盖层关闭"),
        new(ShortcutAction.TrashRestoreOrigin, Key.R, ShortcutScope.Trash, "还原选中项到原位置", ModifierKeys.Control, ContextGate: "有选中 + 覆盖层关闭"),
        new(ShortcutAction.TrashRestoreRoot, Key.R, ShortcutScope.Trash, "还原选中项到根目录", ModifierKeys.Control | ModifierKeys.Shift, ContextGate: "有选中 + 覆盖层关闭"),
        new(ShortcutAction.TrashEscape, Key.Escape, ShortcutScope.Trash, "退出详情页 / 取消选中", ContextGate: "覆盖层打开 → 退出详情页；否则清空选中"),

        // —— 右键菜单键（当前选中行） ——
        new(ShortcutAction.TrashContextMenu, Key.F10, ShortcutScope.Trash, "右键菜单", ModifierKeys.Shift, ContextGate: "有选中"),
        new(ShortcutAction.TrashContextMenu, Key.Apps, ShortcutScope.Trash, "右键菜单", ContextGate: "有选中"),

        // —— 主栏：↑/↓ 移动选中（单选）+ 滚入视口；End 到末项（边界停住） ——
        new(ShortcutAction.TrashMoveUp, Key.Up, ShortcutScope.TrashMain, "上移选中", CommandParameter: "up"),
        new(ShortcutAction.TrashMoveDown, Key.Down, ShortcutScope.TrashMain, "下移选中", CommandParameter: "down"),
        new(ShortcutAction.TrashSelectLast, Key.End, ShortcutScope.TrashMain, "选中末项"),

        // —— 左栏：↑/↓ 按可见视觉顺序移动（单元 = 选中 + 进入；链接叶子 = 定位）；←/→ 折叠展开 ——
        new(ShortcutAction.TrashTreeUp, Key.Up, ShortcutScope.TrashTree, "上移树节点", CommandParameter: "up"),
        new(ShortcutAction.TrashTreeDown, Key.Down, ShortcutScope.TrashTree, "下移树节点", CommandParameter: "down"),
        new(ShortcutAction.TrashTreeCollapse, Key.Left, ShortcutScope.TrashTree, "折叠树节点"),
        new(ShortcutAction.TrashTreeExpand, Key.Right, ShortcutScope.TrashTree, "展开树节点"),
    };

    private static readonly ShortcutSpec[] SearchSpecs =
    {
        // 输入框内按键：焦点在搜索框里时按 Enter = 执行搜索（输入框自身的编辑键不受影响）
        new(ShortcutAction.SearchRun, Key.Enter, ShortcutScope.Search, "执行搜索", ControlName: "SearchBox",
            ContextGate: "仅在搜索框获得焦点时"),

        // 结果列表上下文（搜索页**可操作** = 完整集，对齐浏览页主栏的"通用选择机械"；
        // ↑/↓/End/Ctrl+A/点空白/Esc 与浏览页共用 ListSelection 同一实现）
        new(ShortcutAction.SearchMoveUp, Key.Up, ShortcutScope.Search, "上移选中", CommandParameter: "up"),
        new(ShortcutAction.SearchMoveDown, Key.Down, ShortcutScope.Search, "下移选中", CommandParameter: "down"),
        new(ShortcutAction.SearchSelectLast, Key.End, ShortcutScope.Search, "选中末项"),
        new(ShortcutAction.SearchSelectAll, Key.A, ShortcutScope.Search, "全选", ModifierKeys.Control),
        new(ShortcutAction.SearchOpen, Key.Enter, ShortcutScope.Search, "打开选中项",
            ContextGate: "有选中（搜索框内 Enter 仍 = 执行搜索）"),
        new(ShortcutAction.SearchDelete, Key.Delete, ShortcutScope.Search, "删除选中项", ContextGate: "有选中（危险键受宿主守卫）"),
        new(ShortcutAction.SearchRefresh, Key.F5, ShortcutScope.Search, "重新搜索", ContextGate: "已执行过查询"),
        new(ShortcutAction.SearchEscape, Key.Escape, ShortcutScope.Search, "取消选中 / 清空搜索",
            ContextGate: "有选中 → 清选中；否则清空（=「取消」）"),
    };

    private static readonly ShortcutSpec[] SmartListsSpecs =
    {
        // 结果页返回卡片列表（分层：有选中 → 先清选中；否则返回。关闭细节：非破坏动作，不弹确认）
        new(ShortcutAction.SmartListsBack, Key.Escape, ShortcutScope.SmartLists, "取消选中 / 返回列表",
            ContextGate: "有选中 → 清选中；否则返回卡片列表"),
        // 结果列表上下文（结果页 = **只读**：可读、可选、不可操作——无删除/无 Ctrl+A；
        // ↑/↓/End/Esc/点空白 与浏览页共用 ListSelection 同一实现）
        new(ShortcutAction.SmartListsMoveUp, Key.Up, ShortcutScope.SmartLists, "上移选中", CommandParameter: "up"),
        new(ShortcutAction.SmartListsMoveDown, Key.Down, ShortcutScope.SmartLists, "下移选中", CommandParameter: "down"),
        new(ShortcutAction.SmartListsSelectLast, Key.End, ShortcutScope.SmartLists, "选中末项"),
        new(ShortcutAction.SmartListsOpen, Key.Enter, ShortcutScope.SmartLists, "打开选中项（进浏览页）", ContextGate: "有选中"),
        new(ShortcutAction.SmartListsRefresh, Key.F5, ShortcutScope.SmartLists, "重新查询", ContextGate: "结果页打开时"),
    };

    private static readonly ShortcutSpec[] ToolsSpecs =
    {
        // 输入框内按键：焦点在 ID 输入框里时按 Enter = 执行 ID 跳转
        new(ShortcutAction.ToolsIdJump, Key.Enter, ShortcutScope.Tools, "执行 ID 跳转", ControlName: "IdInput",
            ContextGate: "仅在 ID 输入框获得焦点时"),
        // 去重明细（只读对比页）：选中出口 + 只读延伸键（↑/↓/End/Esc/点空白 = ListSelection 同一实现）
        new(ShortcutAction.ToolsEscape, Key.Escape, ShortcutScope.Tools, "取消明细选中",
            ContextGate: "去重明细有选中行时"),
        new(ShortcutAction.ToolsDetailUp, Key.Up, ShortcutScope.Tools, "上移明细选中",
            CommandParameter: "up", ContextGate: "去重明细打开时"),
        new(ShortcutAction.ToolsDetailDown, Key.Down, ShortcutScope.Tools, "下移明细选中",
            CommandParameter: "down", ContextGate: "去重明细打开时"),
        new(ShortcutAction.ToolsDetailSelectLast, Key.End, ShortcutScope.Tools, "选中明细末项",
            ContextGate: "去重明细打开时"),
        // 三页（搜索 / 智能列表 / 去重明细）的 Enter 语义统一 = **打开浏览页的链接详情页**
        //（外部打开网站是右栏那枚「打开网站」按钮，键位不承担；跳转仅作预留能力）
        new(ShortcutAction.ToolsDetailOpen, Key.Enter, ShortcutScope.Tools, "打开选中项（进浏览页详情）",
            ContextGate: "去重明细有选中行时"),
        new(ShortcutAction.ToolsDetailRefresh, Key.F5, ShortcutScope.Tools, "重新查重",
            ContextGate: "去重明细打开时"),
    };

    /// <summary>
    /// 设置页键位组。
    /// </summary>
    /// <remarks>
    /// 设置页此前**没有任何页面级键位**（<c>Array.Empty</c>）。这里只加一条，理由是：
    /// 「外观」面板的取色盘是一个**模态编辑态**，它需要一个"放弃本次编辑"的出口；
    /// 而 Esc = 放弃当前编辑态是全站既定语义（地址栏编辑 / 就地改名 / 各页 Esc 分层都如此），
    /// 缺了它会逼出"面板自持 KeyDown"这种违规写法（架构红线：键位只许在总表声明）。
    /// 作用域 = Settings 页内；未开取色盘时该命令 CanExecute=false，等于不存在。
    /// </remarks>
    private static readonly ShortcutSpec[] SettingsSpecs =
    {
        new(ShortcutAction.SettingsEscape, Key.Escape, ShortcutScope.Settings, "放弃取色（关闭取色盘）",
            ContextGate: "取色盘打开时"),
    };

    /// <summary>全部页面的键位组（**顺序即文档顺序**；每页一组，组不共享根作用域）。</summary>
    public static readonly IReadOnlyList<ShortcutPageSpec> Pages = new[]
    {
        new ShortcutPageSpec(ShortcutPage.Browser, "浏览页", ShortcutScope.Browser, BrowserSpecs),
        new ShortcutPageSpec(ShortcutPage.Trash, "回收站页", ShortcutScope.Trash, TrashSpecs),
        new ShortcutPageSpec(ShortcutPage.Search, "搜索页", ShortcutScope.Search, SearchSpecs),
        new ShortcutPageSpec(ShortcutPage.SmartLists, "智能列表页", ShortcutScope.SmartLists, SmartListsSpecs),
        new ShortcutPageSpec(ShortcutPage.Tools, "工具页", ShortcutScope.Tools, ToolsSpecs),
        new ShortcutPageSpec(ShortcutPage.Settings, "设置页", ShortcutScope.Settings, SettingsSpecs),
    };

    public static ShortcutPageSpec For(ShortcutPage page)
        => Pages.First(p => p.Page == page);

    /// <summary>按页面装配**页面级**注册表（控件锚定绑定不在此列，见 <see cref="ControlBindings"/>）。</summary>
    public static ShortcutRegistry Build(ShortcutPage page, IShortcutCommands commands)
    {
        var registry = new ShortcutRegistry();
        foreach (var spec in For(page).Specs.Where(s => s.ControlName == null))
            registry.Register(ToBinding(spec, commands));
        return registry;
    }

    /// <summary>该页的控件锚定绑定（键 → 控件名 → 命令），供 <see cref="ShortcutHost"/> 挂到具体控件上。</summary>
    public static IReadOnlyList<(string ControlName, ShortcutBinding Binding)> ControlBindings(
        ShortcutPage page, IShortcutCommands commands)
        => For(page).Specs.Where(s => s.ControlName != null)
            .Select(s => (s.ControlName!, ToBinding(s, commands)))
            .ToList();

    private static ShortcutBinding ToBinding(ShortcutSpec spec, IShortcutCommands commands) => new()
    {
        Key = spec.Key,
        Modifiers = spec.Modifiers,
        Scope = spec.Scope,
        Command = commands.Command(spec.ActionId),
        CommandParameter = spec.CommandParameter,
        Description = spec.Description,
    };

    /// <summary>人类可读的键位清单（Markdown）——由内部工具导出为 `文档/KEYBOARD.md`，与总表同源。</summary>
    public static string Describe()
    {
        var lines = new List<string>
        {
            "# LinkPocket 快捷键总表（每页一组、组间不互通）",
            "",
            "> **本文件由 `ShortcutCatalog` 生成**（工具：`dotnet run --project 工具/SmartProbe -- --dump-shortcuts <路径>`），",
            "> 请勿手改：改键位 → 改 `src/LinkPocket.UIKit/Input/ShortcutCatalog.cs` → 重新导出。",
            "> 隔离规则：一页一组作用域，页面之间既不共享作用域也不继承彼此的作用域链；",
            "> 只有浏览页注册全局键（Ctrl+E/F 切搜索页）。",
            "",
        };
        foreach (var page in Pages)
        {
            lines.Add($"## {page.Title}（根作用域 `{page.RootScope}`）");
            lines.Add("");
            if (page.Specs.Count == 0)
            {
                lines.Add("（无页面级快捷键）");
                lines.Add("");
                continue;
            }
            lines.Add("| 键位 | 动作 | 作用域 | 生效条件 | 备注 |");
            lines.Add("|---|---|---|---|---|");
            foreach (var spec in page.Specs)
            {
                var gesture = ShortcutBinding.FormatGesture(spec.Key, spec.Modifiers);
                var where = spec.ControlName != null ? $"控件级（`{spec.ControlName}`）" : $"`{spec.Scope}`";
                var note = spec.CommandParameter == null ? "" : $"参数：`{spec.CommandParameter}`";
                lines.Add($"| `{gesture}` | {spec.Description} | {where} | {spec.ContextGate} | {note} |");
            }
            lines.Add("");
        }
        lines.Add("## 控件级编辑键（不进总表，属控件自身编辑语义）");
        lines.Add("");
        lines.Add("| 控件 | 键位 | 语义 |");
        lines.Add("|---|---|---|");
        lines.Add("| 面包屑地址栏编辑框（`BreadcrumbBar`） | Enter / Esc / Tab / ↑↓ | 逐级解析并导航 / 取消编辑 / 候选补全 / 候选移动 |");
        lines.Add("| 就地改名编辑框（`InlineNameEditor`） | Enter / Esc | 提交 / 取消 |");
        lines.Add("");
        return string.Join("\n", lines);
    }
}

/// <summary>
/// 页面的「动作 id → 命令」映射（各页提供；总表只认动作 id，不认识任何页面类型）。
/// 解析不到即抛：键位表与页面接线不一致属于装配缺陷，启动就暴露（绝不静默少一个键）。
/// </summary>
public interface IShortcutCommands
{
    ICommand Command(string actionId);
}

/// <summary>映射实现（各页在装配时构建一次）。</summary>
public sealed class ShortcutCommandMap : IShortcutCommands
{
    private readonly Dictionary<string, ICommand> _map = new(StringComparer.Ordinal);

    public ShortcutCommandMap Add(string actionId, ICommand command)
    {
        _map[actionId] = command;
        return this;
    }

    public ICommand Command(string actionId)
        => _map.TryGetValue(actionId, out var command)
            ? command
            : throw new InvalidOperationException(
                $"快捷键动作未接线：[{actionId}] 没有对应的命令（键位总表与页面映射不一致）。");
}
