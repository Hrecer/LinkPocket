namespace LinkPocket.ViewModels;

/// <summary>
/// 拖拽载荷项（轻量：移动一个实体只需要这三件事）：
/// <see cref="Id"/> = 实体 ID（文件夹 ID / 链接 ID）、<see cref="IsFolder"/> = 类型、<see cref="Name"/> = 显示名（提示文案用）。
///
/// <para>为什么不再直接搬行 VM：载荷原先绑死 <c>BrowserRowViewModel</c>，导致**只有主栏行能发起拖拽**——
/// 左栏树节点即使选中了同一批实体也无从构造载荷。抽象成轻量项后，主栏行与树节点都能构造，
/// 放置端（主栏文件夹行 / 树节点）与移动逻辑一律按 <see cref="Id"/> + <see cref="IsFolder"/> 通用，
/// 所以"树 → 主栏""树 → 树"与"主栏 → 主栏"走的是同一条移动路径。</para>
/// </summary>
public sealed record DragItem(string Id, bool IsFolder, string Name);

/// <summary>
/// 拖拽落点（悬停高亮 + 「移动到 X」提示的状态载体，唯一事实来源在 VM）：
/// <list type="bullet">
/// <item><see cref="FolderId"/> = 落点文件夹 ID；<c>null</c> 表示**根目录**（根不是实体、没有 ID——零哨兵）；</item>
/// <item><see cref="Pane"/> = 指针真正所在的那一栏（同一实体可能两栏都有呈现，高亮只落一处）；</item>
/// <item><see cref="Name"/> = 提示文案里的名称（行名 / 节点名 / 列表空白落点 = 当前目录名）。</item>
/// </list>
/// ⚠️「**没有落点**」用整个对象为 <c>null</c> 表示，与「**落在根上**」严格区分——
/// 前者要熄灭高亮且不显示提示，后者要显示「移动到「全部书签」」。
/// </summary>
public sealed record BrowserDropTarget(string? FolderId, BrowserPane Pane, string Name);
