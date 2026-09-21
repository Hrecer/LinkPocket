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
    /// <summary>把两个唯一事实来源（选中集合 <see cref="Selection"/> + 拖拽落点 <see cref="_dropTargetId"/>）
    /// 投影到主栏行 + 目录树，并刷新派生状态。
    /// 在选中写入（<see cref="SetSelection"/>）、落点写入（<see cref="SetDropTarget"/>）与 Rows/Tree 重建后（RefreshAsync）调用；
    /// 行与树都是这两个集合的只读投影，无任何独立状态。</summary>
    private void ApplySelectionToView()
    {
        SyncMainRowSelection();
        SyncTreeSelection();
        SyncTreeDropTarget();      // 树节点重建后落点高亮同样要重放（行侧是 getter 投影，无需重放）
        NotifySelectionChanged();
    }

    /// <summary>主栏行投影：每行 IsSelected = 其 Id 是否在选中集合（行只读，集合是唯一事实）。
    /// 行的 IsSelected getter 已直接读 <see cref="IsSelectedId"/>，此处仅为强制刷新绑定。</summary>
    private void SyncMainRowSelection()
    {
        foreach (var r in Rows) r.InvalidateIsSelected();
    }

    /// <summary>
    /// 目录树投影：树节点高亮 = 选中集合里真正被选中的实体，**与当前所处目录无关**。
    /// 「位于某文件夹 / 根目录」是导航位置，由面包屑表达，绝不转换为树高亮——
    /// 进入某个文件夹不代表该文件夹"被选中"（位置 ≠ 选中）。
    /// 树不持久任何选中状态，全部由唯一事实来源 <see cref="Selection"/>（共享 ListSelection）派生：
    /// 链接叶子高亮 = 该链接在集合；文件夹节点高亮 = 其 FolderId 在集合（当且仅当该文件夹实体在选中集合中）。
    /// </summary>
    private void SyncTreeSelection()
    {
        foreach (var node in AllTreeNodes())
        {
            // 虚拟根「全部书签」不是实体：不因位于根目录而高亮；仅当真实实体（链接叶子或文件夹）被选中才高亮
            string? entityId = node.IsLink ? node.Id : node.FolderId;
            node.IsSelected = entityId != null && Selection.Contains(entityId);
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
    /// 点击树节点统一入口（展开 ≠ 选中 ≠ 进入，三者物理分离）：
    /// chevron 只负责展开/收起（模板内独立控件，绝不进入此方法）；行主体单击才到此。
    /// 只有两类行、两个动词，零特例：
    /// · **位置行**（文件夹 / 虚根「全部书签」）= 进入目录：无条件 `LoadAsync`——
    ///   点是当前位置同样重载刷新一次（Windows 口径：点当前文件夹、已在根点「全部书签」都刷新）；
    ///   重复导航不污染历史（<see cref="BrowserHistory.NavigateTo"/> 对同目录直接忽略）；
    ///   重载也不动选中（选中是独立集合，重载后按 ID 重新投影）。
    ///   两类位置行唯一差异 = 有没有实体身份：文件夹把自己写入选中集合（单击 = 选中该文件夹 + 进入）；
    ///   虚根 <c>FolderId == null</c>（不是实体、没有可高亮的身份）→ 只进入、不写选中。
    /// · **实体行**（链接叶子）= 定位：进入其所属目录（已在目标目录则免重载——行本来就在，无谓重建只会闪烁）
    ///   并把该链接写入选中集合。
    /// 树自身不持有持久选中状态：高亮完全由 <see cref="SyncTreeSelection"/> 从 <see cref="Selection"/>
    /// 派生，与主栏行选中同一唯一事实来源，二者天然一致。
    /// </summary>
    public async Task SelectTreeNodeAsync(FolderNode node)
    {
        if (node.IsLink)
        {
            // 实体行：把链接 ID 写入选中集合（唯一事实），主栏与树同时投影高亮；
            // 即使该行尚未出现在 Rows（分页），也先记录选中，由导航/刷新投影补齐。
            SetSelection(new[] { node.Id }, node.Id);
            await NavigateAndSelectAsync(node.ParentId, node.Id);
            return;
        }

        // 位置行：进入目录（无条件重载 = 点是当前位置也刷新）；只有真实文件夹有实体身份，虚根不写选中。
        // 树高亮由 Selection 派生，与"进入"本身无关：位置仍由面包屑表达（位置 ≠ 选中）。
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
        yield return node;
        foreach (var child in node.Children)
            foreach (var sub in EnumerateSelfAndChildren(child))
                yield return sub;
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