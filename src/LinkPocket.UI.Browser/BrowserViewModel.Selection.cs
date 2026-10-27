using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using LinkPocket.Contracts;
using LinkPocket.Models;

namespace LinkPocket.ViewModels;

/// <summary>
/// BrowserViewModel · 分区：选中与树/行投影（SelectRow/SetSelection/清选中/跳转定位/选中区一行）（partial——结构拆分，行为与公开 API 不变）。
/// </summary>
public partial class BrowserViewModel
{
    /// <summary>把唯一事实来源（选中集合 <see cref="Selection"/> + 拖拽落点 <see cref="_dropTargetId"/>）
    /// 投影到主栏行 + 目录树，并刷新派生状态（树文件夹行另吃导航位置，见 <see cref="SyncTreeSelection"/>）。
    /// 在选中写入（<see cref="SetSelection"/>）、落点写入（<see cref="SetDropTarget"/>）与 Rows/Tree 重建后（RefreshAsync）调用；
    /// 行与树都不持独立状态，全部按事实来源重放。</summary>
    private void ApplySelectionToView()
    {
        SyncMainRowSelection();
        SyncTreeSelection();
        SyncTreeDropTarget();      // 树节点重建后落点高亮同样要重放（行侧是 getter 投影，无需重放）
        _rowsReplaced = false;     // 本轮"整体替换"的一次性标记到此结束
        NotifySelectionChanged();
    }

    /// <summary>
    /// 主栏行投影：每行 IsSelected = 其 Id 是否在选中集合（行只读，集合是唯一事实）。
    /// 行的 IsSelected getter 已直接读 <see cref="IsSelectedId"/>，此处仅为强制刷新绑定。
    /// </summary>
    /// <remarks>
    /// ⚠️ 行集合**刚被整体替换**时直接跳过（见 <see cref="_rowsReplaced"/>）：那是全新的行对象，
    /// 视图侧（虚拟化容器重建 / Recycling 复用换 DataContext）**首次求值就会读宿主集合**，
    /// 再逐行发一万次属性通知纯属白烧——实测这一步就是"打开大目录"里 UI 线程后半程的主要成本。
    /// </remarks>
    private void SyncMainRowSelection()
    {
        if (_rowsReplaced) return;
        foreach (var r in Rows) r.InvalidateIsSelected();
    }

    /// <summary>行集合在本轮刷新里被整体替换过（<see cref="ApplySelectionToView"/> 消费后归零）。</summary>
    private bool _rowsReplaced;

    /// <summary>
    /// 目录树投影：**文件夹行（含虚根）= 当前所在位置**。浏览页左栏只显示文件夹（Windows 导航窗格口径），
    /// 因此这里的"选中态"只有一种含义——回答"我在哪个文件夹"，任何时刻恰好一个文件夹行亮着
    /// （在根目录时就是虚根「全部书签」），主栏点空白清选中不会把它抹掉。
    /// 树不持久任何状态：位置来自 <see cref="Controller"/>.CurrentFolderId，纯投影，容器重建不影响。
    /// </summary>
    private void SyncTreeSelection()
    {
        var currentFolderId = Controller.CurrentFolderId;
        foreach (var node in AllTreeNodes())
        {
            node.IsSelected = currentFolderId == null
                ? node.IsRoot
                : string.Equals(node.FolderId, currentFolderId, StringComparison.Ordinal);
        }
    }

    /// <summary>主栏单选：把选中集合收敛为仅 <paramref name="row"/>.Id（唯一事实来源写入）。</summary>
    private void SelectRow(BrowserRowViewModel? row)
    {
        if (row == null) return;
        Selection.SelectSingle(row.Id);
    }

    /// <summary>
    /// 带修饰键的选择路由（共享 <see cref="ListSelection.Click"/>：Ctrl 翻转 / Shift 以锚点画区间 /
    /// 无修饰 = 单选）。由视图在鼠标抬起时调用（读 Keyboard.Modifiers）；
    /// 选中集合是唯一事实来源，绝不直接改行状态。
    /// </summary>
    public void SelectRowWithModifiers(BrowserRowViewModel? row, ModifierKeys mods)
    {
        if (row == null) return;
        Selection.Click(row.Id, mods.HasFlag(ModifierKeys.Control), mods.HasFlag(ModifierKeys.Shift),
            Rows.Select(r => r.Id).ToList());
    }

    public void SelectAllRows()
    {
        Selection.SelectAll(Rows.Select(r => r.Id).ToList());
    }

    /// <summary>
    /// 选中唯一的写入入口（全部经共享 <see cref="ListSelection"/>）：本次调用是主栏选中/清除动作的
    /// 目标 ID 集，写完后由核心触发 Changed → <see cref="ApplySelectionToView"/> 投影到主栏行 + 目录树 + 派生状态。
    /// 任何选择路径（行点击/树点击/全选/清空）都只走这里，不直接在行对象或树上写选中——
    /// 事实来源唯一、且跨 Rows/Tree 重建存活。
    /// </summary>
    private void SetSelection(IEnumerable<string>? ids = null, string? anchor = null, Action<HashSet<string>>? mutate = null)
    {
        // ⚠️ mutate 的起点必须是**当前集合**（ListSelection.Mutate 内部保证）：
        // Ctrl 翻转 = "在当前选中上增/删目标 ID"；曾从空集起步 → 多选永远做不到（用例已锁死）。
        if (mutate != null) Selection.Mutate(mutate, anchor);
        else if (ids != null) Selection.Set(ids, anchor);
        else if (!string.IsNullOrEmpty(anchor)) Selection.Mutate(_ => { }, anchor);
    }

    /// <summary>把指定 ID 纳入选中集合（不清空其他选中）并投影两栏——用于从详情页返回等"还原选中"语义。</summary>
    public void RestoreSelection(string id) => Selection.Restore(id);

    /// <summary>
    /// 视图应把某一行滚入视口（定位/跳转后保证选中项可见）。
    /// 由 <see cref="NavigateAndSelectAsync"/> 触发，BrowserView 订阅处理；
    /// 视图不在场（无 UI 的会话）时无人订阅也不影响数据层结果。
    /// </summary>
    public event EventHandler<BrowserRowViewModel>? FocusRowRequested;

    /// <summary>
    /// 进入指定目录并选中其中一行（行可为链接或文件夹）——「跳转」的浏览页执行原语。
    /// 由定位组件（Services/ContentLocator）经 IBrowserLocateHost 端口调用；
    /// 目录与选中逻辑属于浏览页自身领域，故实现在此，界面只需滚动。
    /// 返回该行是否存在并被选中。
    /// </summary>
    /// <summary>
    /// 进入指定目录并选中其中一行（行可为链接或文件夹）——「跳转」的浏览页执行原语。
    /// 选中直接写 <see cref="Selection"/>（唯一事实来源），不依赖行对象引用：
    /// 即使该行未在当前 Rows（分页/目录重建中），ID 也照常落在选中集合，树叶子按
    /// <see cref="SyncTreeSelection"/> 同步高亮；此后导航成功该行出现在 Rows 即由主栏行投影选中。
    /// 返回 true 表示定位目标已纳入选中集合；界面可据此滚动。
    /// </summary>
    public async Task<bool> NavigateAndSelectAsync(string? folderId, string rowId)
    {
        if (string.IsNullOrEmpty(rowId)) return false;

        // 已在目标目录时不必重载（避免无谓的列表重建与闪烁）
        // 注意：链接叶子定位=进根（folderId null）、CurrentFolderId 已是 null 时也直接下单选集，
        // 无需重载，避免异步重建导致"主栏闪一下"。
        if (Controller.CurrentFolderId != folderId)
            await LoadAsync(folderId);

        SetSelection(new[] { rowId }, rowId);
        var row = Rows.FirstOrDefault(r => r.Id == rowId);
        if (row != null) FocusRowRequested?.Invoke(this, row);
        return true;
    }

    /// <summary>
    /// 点击树节点统一入口（展开 ≠ 进入）：chevron 只负责展开/收起，行主体单击才到此。
    /// 左栏只有两类位置行（虚根「全部书签」与文件夹），两个动词：
    /// 无条件 `LoadAsync` 进入该目录（点是当前位置同样重载刷新一次，Windows 口径；
    /// 重复导航不污染历史）。文件夹有实体身份，同时把自己写入选中集合（沿用右键/拖拽语义）；
    /// 虚根 <c>FolderId == null</c> → 只进入、不写选中。
    /// 树自身不持有状态：文件夹行高亮 = **当前位置**（<see cref="SyncTreeSelection"/> 直接投影导航位置）。
    /// </summary>
    public async Task SelectTreeNodeAsync(FolderNode node)
    {
        if (node.IsRoot)
        {
            await LoadAsync(null);
            return;
        }

        if (node.FolderId != null) SetSelection(new[] { node.FolderId }, node.FolderId);
        await LoadAsync(node.FolderId);
    }

    /// <summary>遍历整棵树（含虚拟根「全部书签」），返回全部节点的深度优先序列。</summary>
    private IEnumerable<FolderNode> AllTreeNodes()
    {
        foreach (var root in FolderTree)
            foreach (var node in EnumerateSelfAndChildren(root))
                yield return node;
    }

    private static IEnumerable<FolderNode> EnumerateSelfAndChildren(FolderNode node)
    {
        // 显式栈的深度优先前序（与原递归顺序一致：子节点逆序压栈、弹出即原顺序；深树不爆栈）
        var stack = new Stack<FolderNode>();
        stack.Push(node);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            yield return current;
            for (var i = current.Children.Count - 1; i >= 0; i--)
                stack.Push(current.Children[i]);
        }
    }

    public void ClearSelection()
    {
        SetSelection(Enumerable.Empty<string>(), anchor: null);
    }

    /// <summary>点列表卡空白：主栏获得键盘语义归属 + 清空选中（唯一实现 UIKit BlankClick 的命令端）。</summary>
    private void ClearMainPaneSelection()
    {
        ActivatePane(BrowserPane.Main);
        ClearSelection();
    }

    /// <summary>点页面其它空白（导航行 / 命令栏 / 内容区 / 状态栏）：保持当前栏归属，只清选中
    /// （ActivatePane 会把键盘焦点收回页内——"清焦点"语义）。</summary>
    private void ClearPageSelection()
    {
        ActivatePane(ActivePane);
        ClearSelection();
    }

}