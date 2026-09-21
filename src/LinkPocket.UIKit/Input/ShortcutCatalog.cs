using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;
using LinkPocket.I18n;

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
    /// <summary>动作说明的<b>文案键</b>（如 <c>shortcut.cut</c>）——总表里不许存成品句子。</summary>
    string DescriptionKey,
    ModifierKeys Modifiers = ModifierKeys.None,
    object? CommandParameter = null,
    string? ControlName = null,
    /// <summary>生效条件的<b>文案键</b>；空 = 无条件（导出文档里画成 <c>—</c>）。</summary>
    string ContextGateKey = "");

/// <summary>一个页面的键位组（页 ↔ 根作用域 一对一，组与组之间不互通）。</summary>
public sealed record ShortcutPageSpec(
    ShortcutPage Page,
    /// <summary>页面名的<b>文案键</b>（如 <c>shortcut.page.browser</c>）。</summary>
    string TitleKey,
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
        new(ShortcutAction.BrowserCut, Key.X, ShortcutScope.Browser, "shortcut.cut", ModifierKeys.Control, ContextGateKey: "gate.listContext"),
        new(ShortcutAction.BrowserCopy, Key.C, ShortcutScope.Browser, "shortcut.copy", ModifierKeys.Control, ContextGateKey: "gate.listContextSame"),
        new(ShortcutAction.BrowserPaste, Key.V, ShortcutScope.Browser, "shortcut.paste", ModifierKeys.Control, ContextGateKey: "gate.listContextSame"),
        new(ShortcutAction.BrowserSelectAll, Key.A, ShortcutScope.Browser, "shortcut.selectAll", ModifierKeys.Control, ContextGateKey: "gate.pathEditingOrRename"),
        new(ShortcutAction.BrowserDelete, Key.Delete, ShortcutScope.Browser, "shortcut.deleteSelected", ContextGateKey: "gate.listContextSame"),
        new(ShortcutAction.BrowserRename, Key.F2, ShortcutScope.Browser, "shortcut.renameSelected", ContextGateKey: "gate.singlePlusList"),
        new(ShortcutAction.BrowserOpen, Key.Enter, ShortcutScope.Browser, "shortcut.openSelected", ContextGateKey: "gate.singlePlusList"),

        // —— 导航类 ——
        new(ShortcutAction.BrowserGoBack, Key.Back, ShortcutScope.Browser, "shortcut.back", ContextGateKey: "gate.pathEditingOrRename"),
        new(ShortcutAction.BrowserGoUp, Key.Up, ShortcutScope.Browser, "shortcut.up", ModifierKeys.Alt, ContextGateKey: "gate.pathEditingOrRename"),
        new(ShortcutAction.BrowserGoBack, Key.Left, ShortcutScope.Browser, "shortcut.back", ModifierKeys.Alt, ContextGateKey: "gate.pathEditingOrRename"),
        new(ShortcutAction.BrowserGoForward, Key.Right, ShortcutScope.Browser, "shortcut.forward", ModifierKeys.Alt, ContextGateKey: "gate.pathEditingOrRename"),
        new(ShortcutAction.BrowserRefresh, Key.F5, ShortcutScope.Browser, "shortcut.refreshFolder", ContextGateKey: ""),
        new(ShortcutAction.BrowserFocusPath, Key.D, ShortcutScope.Browser, "shortcut.focusPath", ModifierKeys.Alt, ContextGateKey: "gate.pathEditingOrRename"),
        new(ShortcutAction.BrowserExpandTreeToCurrent, Key.E, ShortcutScope.Browser, "shortcut.expandToCurrent", ModifierKeys.Control | ModifierKeys.Shift, ContextGateKey: ""),
        new(ShortcutAction.BrowserNewFolder, Key.N, ShortcutScope.Browser, "shortcut.newFolder", ModifierKeys.Control | ModifierKeys.Shift, ContextGateKey: "gate.listContextPlain"),
        new(ShortcutAction.BrowserCopyPath, Key.C, ShortcutScope.Browser, "shortcut.copyPath", ModifierKeys.Control | ModifierKeys.Shift, ContextGateKey: "gate.pathEditingOrRename"),
        new(ShortcutAction.BrowserUndo, Key.Z, ShortcutScope.Browser, "shortcut.undo", ModifierKeys.Control, ContextGateKey: "gate.listAndUndoStack"),
        new(ShortcutAction.BrowserRedo, Key.Y, ShortcutScope.Browser, "shortcut.redo", ModifierKeys.Control, ContextGateKey: "gate.listAndUndoStack"),

        // —— 两栏通用：右键菜单键（当前选中行）——
        new(ShortcutAction.BrowserContextMenu, Key.F10, ShortcutScope.Browser, "shortcut.contextMenu", ModifierKeys.Shift, ContextGateKey: "gate.hasSelection"),
        new(ShortcutAction.BrowserContextMenu, Key.Apps, ShortcutScope.Browser, "shortcut.contextMenu", ContextGateKey: "gate.hasSelection"),

        // —— 主栏：↑/↓ 移动选中（单选）+ 滚入视口；End 到末项（Explorer 口径，边界停住）——
        new(ShortcutAction.BrowserMoveUp, Key.Up, ShortcutScope.BrowserMain, "shortcut.moveUp", CommandParameter: "up"),
        new(ShortcutAction.BrowserMoveDown, Key.Down, ShortcutScope.BrowserMain, "shortcut.moveDown", CommandParameter: "down"),
        new(ShortcutAction.BrowserSelectLast, Key.End, ShortcutScope.BrowserMain, "shortcut.selectLast"),

        // —— 左栏：↑/↓ 按可见视觉顺序移动（与鼠标同语义：文件夹 = 选中 + 进入）；←/→ 折叠展开 ——
        new(ShortcutAction.BrowserTreeUp, Key.Up, ShortcutScope.BrowserTree, "shortcut.treeUp", CommandParameter: "up"),
        new(ShortcutAction.BrowserTreeDown, Key.Down, ShortcutScope.BrowserTree, "shortcut.treeDown", CommandParameter: "down"),
        new(ShortcutAction.BrowserTreeCollapse, Key.Left, ShortcutScope.BrowserTree, "shortcut.treeCollapse"),
        new(ShortcutAction.BrowserTreeExpand, Key.Right, ShortcutScope.BrowserTree, "shortcut.treeExpand"),

        // —— Esc（分层：详情页打开 → 退出详情页；有剪切态 → 取消剪切；否则清空选中）——
        new(ShortcutAction.BrowserEscape, Key.Escape, ShortcutScope.Browser, "shortcut.escapeBrowser", ContextGateKey: "gate.notInEditor"),

        // —— 全局（任何页面）：切到搜索页并聚焦搜索框（**只有浏览页注册全局键**）——
        new(ShortcutAction.BrowserNavigateToSearch, Key.E, ShortcutScope.Global, "shortcut.toSearch", ModifierKeys.Control),
        new(ShortcutAction.BrowserNavigateToSearch, Key.F, ShortcutScope.Global, "shortcut.toSearch", ModifierKeys.Control),
    };

    private static readonly ShortcutSpec[] TrashSpecs =
    {
        // —— 导航类（两栏通用） ——
        new(ShortcutAction.TrashGoBack, Key.Back, ShortcutScope.Trash, "shortcut.back"),
        new(ShortcutAction.TrashGoUp, Key.Up, ShortcutScope.Trash, "shortcut.up", ModifierKeys.Alt),
        new(ShortcutAction.TrashGoBack, Key.Left, ShortcutScope.Trash, "shortcut.back", ModifierKeys.Alt),
        new(ShortcutAction.TrashGoForward, Key.Right, ShortcutScope.Trash, "shortcut.forward", ModifierKeys.Alt),
        new(ShortcutAction.TrashRefresh, Key.F5, ShortcutScope.Trash, "shortcut.refreshTrash"),
        // 审计补齐（与浏览页 Alt+D 对齐）：回收站地址栏同样可点空白即编辑，缺的是键盘入口
        new(ShortcutAction.TrashFocusPath, Key.D, ShortcutScope.Trash, "shortcut.focusPath", ModifierKeys.Alt,
            ContextGateKey: "gate.pathEditing"),

        // —— 选择 / 打开 / 永久删除（两栏通用） ——
        new(ShortcutAction.TrashSelectAll, Key.A, ShortcutScope.Trash, "shortcut.selectAll", ModifierKeys.Control, ContextGateKey: "gate.pathEditing"),
        new(ShortcutAction.TrashOpen, Key.Enter, ShortcutScope.Trash, "shortcut.openSelected", ContextGateKey: "gate.pathEditing"),
        new(ShortcutAction.TrashPurge, Key.Delete, ShortcutScope.Trash, "shortcut.purgeSelected", ContextGateKey: "gate.hasSelectionOverlayClosed"),
        new(ShortcutAction.TrashRestoreOrigin, Key.R, ShortcutScope.Trash, "shortcut.restoreOrigin", ModifierKeys.Control, ContextGateKey: "gate.hasSelectionOverlayClosed"),
        new(ShortcutAction.TrashRestoreRoot, Key.R, ShortcutScope.Trash, "shortcut.restoreRoot", ModifierKeys.Control | ModifierKeys.Shift, ContextGateKey: "gate.hasSelectionOverlayClosed"),
        new(ShortcutAction.TrashEscape, Key.Escape, ShortcutScope.Trash, "shortcut.escapeTrash", ContextGateKey: "gate.escapeTrashSemantics"),

        // —— 右键菜单键（当前选中行） ——
        new(ShortcutAction.TrashContextMenu, Key.F10, ShortcutScope.Trash, "shortcut.contextMenu", ModifierKeys.Shift, ContextGateKey: "gate.hasSelection"),
        new(ShortcutAction.TrashContextMenu, Key.Apps, ShortcutScope.Trash, "shortcut.contextMenu", ContextGateKey: "gate.hasSelection"),

        // —— 主栏：↑/↓ 移动选中（单选）+ 滚入视口；End 到末项（边界停住） ——
        new(ShortcutAction.TrashMoveUp, Key.Up, ShortcutScope.TrashMain, "shortcut.moveUp", CommandParameter: "up"),
        new(ShortcutAction.TrashMoveDown, Key.Down, ShortcutScope.TrashMain, "shortcut.moveDown", CommandParameter: "down"),
        new(ShortcutAction.TrashSelectLast, Key.End, ShortcutScope.TrashMain, "shortcut.selectLast"),

        // —— 左栏：↑/↓ 按可见视觉顺序移动（单元 = 选中 + 进入；链接叶子 = 定位）；←/→ 折叠展开 ——
        new(ShortcutAction.TrashTreeUp, Key.Up, ShortcutScope.TrashTree, "shortcut.treeUp", CommandParameter: "up"),
        new(ShortcutAction.TrashTreeDown, Key.Down, ShortcutScope.TrashTree, "shortcut.treeDown", CommandParameter: "down"),
        new(ShortcutAction.TrashTreeCollapse, Key.Left, ShortcutScope.TrashTree, "shortcut.treeCollapse"),
        new(ShortcutAction.TrashTreeExpand, Key.Right, ShortcutScope.TrashTree, "shortcut.treeExpand"),
    };

    private static readonly ShortcutSpec[] SearchSpecs =
    {
        // 输入框内按键：焦点在搜索框里时按 Enter = 执行搜索（输入框自身的编辑键不受影响）
        new(ShortcutAction.SearchRun, Key.Enter, ShortcutScope.Search, "shortcut.runSearch", ControlName: "SearchBox",
            ContextGateKey: "gate.focusSearchBox"),

        // 结果列表上下文（搜索页**可操作** = 完整集，对齐浏览页主栏的"通用选择机械"；
        // ↑/↓/End/Ctrl+A/点空白/Esc 与浏览页共用 ListSelection 同一实现）
        new(ShortcutAction.SearchMoveUp, Key.Up, ShortcutScope.Search, "shortcut.moveUp", CommandParameter: "up"),
        new(ShortcutAction.SearchMoveDown, Key.Down, ShortcutScope.Search, "shortcut.moveDown", CommandParameter: "down"),
        new(ShortcutAction.SearchSelectLast, Key.End, ShortcutScope.Search, "shortcut.selectLast"),
        new(ShortcutAction.SearchSelectAll, Key.A, ShortcutScope.Search, "shortcut.selectAll", ModifierKeys.Control),
        new(ShortcutAction.SearchOpen, Key.Enter, ShortcutScope.Search, "shortcut.openSelected",
            ContextGateKey: "gate.hasSelectionSearchEnter"),
        new(ShortcutAction.SearchDelete, Key.Delete, ShortcutScope.Search, "shortcut.deleteSelected", ContextGateKey: "gate.hasSelectionGuarded"),
        new(ShortcutAction.SearchRefresh, Key.F5, ShortcutScope.Search, "shortcut.research", ContextGateKey: "gate.queried"),
        new(ShortcutAction.SearchEscape, Key.Escape, ShortcutScope.Search, "shortcut.escapeSearch",
            ContextGateKey: "gate.escapeSearchSemantics"),
    };

    private static readonly ShortcutSpec[] SmartListsSpecs =
    {
        // 结果页返回卡片列表（分层：有选中 → 先清选中；否则返回。关闭细节：非破坏动作，不弹确认）
        new(ShortcutAction.SmartListsBack, Key.Escape, ShortcutScope.SmartLists, "shortcut.escapeSmartLists",
            ContextGateKey: "gate.escapeSmartSemantics"),
        // 结果列表上下文（结果页 = **只读**：可读、可选、不可操作——无删除/无 Ctrl+A；
        // ↑/↓/End/Esc/点空白 与浏览页共用 ListSelection 同一实现）
        new(ShortcutAction.SmartListsMoveUp, Key.Up, ShortcutScope.SmartLists, "shortcut.moveUp", CommandParameter: "up"),
        new(ShortcutAction.SmartListsMoveDown, Key.Down, ShortcutScope.SmartLists, "shortcut.moveDown", CommandParameter: "down"),
        new(ShortcutAction.SmartListsSelectLast, Key.End, ShortcutScope.SmartLists, "shortcut.selectLast"),
        new(ShortcutAction.SmartListsOpen, Key.Enter, ShortcutScope.SmartLists, "shortcut.openInBrowserPage", ContextGateKey: "gate.hasSelection"),
        new(ShortcutAction.SmartListsRefresh, Key.F5, ShortcutScope.SmartLists, "shortcut.requery", ContextGateKey: "gate.resultsOpen"),
    };

    private static readonly ShortcutSpec[] ToolsSpecs =
    {
        // 输入框内按键：焦点在 ID 输入框里时按 Enter = 执行 ID 跳转
        new(ShortcutAction.ToolsIdJump, Key.Enter, ShortcutScope.Tools, "shortcut.idJump", ControlName: "IdInput",
            ContextGateKey: "gate.focusIdInput"),
        // 去重明细（只读对比页）：选中出口 + 只读延伸键（↑/↓/End/Esc/点空白 = ListSelection 同一实现）
        new(ShortcutAction.ToolsEscape, Key.Escape, ShortcutScope.Tools, "shortcut.escapeDetail",
            ContextGateKey: "gate.detailHasSelection"),
        new(ShortcutAction.ToolsDetailUp, Key.Up, ShortcutScope.Tools, "shortcut.detailUp",
            CommandParameter: "up", ContextGateKey: "gate.detailOpen"),
        new(ShortcutAction.ToolsDetailDown, Key.Down, ShortcutScope.Tools, "shortcut.detailDown",
            CommandParameter: "down", ContextGateKey: "gate.detailOpen"),
        new(ShortcutAction.ToolsDetailSelectLast, Key.End, ShortcutScope.Tools, "shortcut.detailSelectLast",
            ContextGateKey: "gate.detailOpen"),
        // 三页（搜索 / 智能列表 / 去重明细）的 Enter 语义统一 = **打开浏览页的链接详情页**
        //（外部打开网站是右栏那枚「打开网站」按钮，键位不承担；跳转仅作预留能力）
        new(ShortcutAction.ToolsDetailOpen, Key.Enter, ShortcutScope.Tools, "shortcut.openDetailInBrowser",
            ContextGateKey: "gate.detailHasSelection"),
        new(ShortcutAction.ToolsDetailRefresh, Key.F5, ShortcutScope.Tools, "shortcut.rededup",
            ContextGateKey: "gate.detailOpen"),
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
        new(ShortcutAction.SettingsEscape, Key.Escape, ShortcutScope.Settings, "shortcut.abandonPicker",
            ContextGateKey: "gate.pickerOpen"),
    };

    /// <summary>全部页面的键位组（**顺序即文档顺序**；每页一组，组不共享根作用域）。</summary>
    public static readonly IReadOnlyList<ShortcutPageSpec> Pages = new[]
    {
        new ShortcutPageSpec(ShortcutPage.Browser, "shortcut.page.browser", ShortcutScope.Browser, BrowserSpecs),
        new ShortcutPageSpec(ShortcutPage.Trash, "shortcut.page.trash", ShortcutScope.Trash, TrashSpecs),
        new ShortcutPageSpec(ShortcutPage.Search, "shortcut.page.search", ShortcutScope.Search, SearchSpecs),
        new ShortcutPageSpec(ShortcutPage.SmartLists, "shortcut.page.smartLists", ShortcutScope.SmartLists, SmartListsSpecs),
        new ShortcutPageSpec(ShortcutPage.Tools, "shortcut.page.tools", ShortcutScope.Tools, ToolsSpecs),
        new ShortcutPageSpec(ShortcutPage.Settings, "shortcut.page.settings", ShortcutScope.Settings, SettingsSpecs),
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
        DescriptionKey = spec.DescriptionKey,
    };

    /// <summary>
    /// 人类可读的键位清单（Markdown）——由内部工具导出为 `文档/KEYBOARD.md`，与总表同源。
    /// 总表里存的是<b>文案键</b>，导出时按当前界面语言取词（文档跟着界面走，不写死某一种语言）。
    /// </summary>
    public static string Describe()
    {
        static string Text(string key) => key.Length == 0 ? "—" : Loc.T(key);

        var lines = new List<string>
        {
            Loc.T("shortcut.doc.title"),
            "",
            Loc.T("shortcut.doc.generated"),
            Loc.T("shortcut.doc.editSource"),
            Loc.T("shortcut.doc.isolation"),
            Loc.T("shortcut.doc.globalKeys"),
            "",
        };
        foreach (var page in Pages)
        {
            lines.Add(string.Format(Loc.T("shortcut.doc.pageHeading"), Loc.T(page.TitleKey), page.RootScope));
            lines.Add("");
            if (page.Specs.Count == 0)
            {
                lines.Add("shortcut.doc.none");
                lines.Add("");
                continue;
            }
            lines.Add("shortcut.doc.tableHead");
            lines.Add("|---|---|---|---|---|");
            foreach (var spec in page.Specs)
            {
                var gesture = ShortcutBinding.FormatGesture(spec.Key, spec.Modifiers);
                var where = spec.ControlName != null ? string.Format(Loc.T("shortcut.doc.controlScoped"), spec.ControlName) : $"`{spec.Scope}`";
                var note = spec.CommandParameter == null ? "" : string.Format(Loc.T("shortcut.doc.parameter"), spec.CommandParameter);
                lines.Add($"| `{gesture}` | {Loc.T(spec.DescriptionKey)} | {where} | {Text(spec.ContextGateKey)} | {note} |");
            }
            lines.Add("");
        }
        lines.Add("shortcut.doc.controlHead");
        lines.Add("");
        lines.Add("shortcut.doc.controlTable");
        lines.Add("|---|---|---|");
        lines.Add("shortcut.doc.breadcrumb");
        lines.Add("shortcut.doc.inlineRename");
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
                $"shortcut action not wired: [{actionId}] has no command (key table and page mapping disagree)");
}
