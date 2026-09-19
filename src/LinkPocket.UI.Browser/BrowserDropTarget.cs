namespace LinkPocket.ViewModels;

/// <summary>
/// 传输来源：剪贴板（Ctrl+X/V）还是拖拽（左键 / 右键拖拽菜单）。
/// 只用于少数**来源相关**的收尾差异（剪切载荷是否消费、新项是否置尾选中），不参与"搬什么、搬到哪"的判定。
/// </summary>
public enum TransferOrigin
{
    Clipboard,
    Drag,
}

/// <summary>
/// 拖拽落点（悬停高亮 + 「移动到 X」提示的状态载体，唯一事实来源在 VM）：
/// <list type="bullet">
/// <item><see cref="FolderId"/> = 落点文件夹 ID；<c>null</c> 表示**根目录**（根不是实体、没有 ID——零哨兵）；</item>
/// <item><see cref="Pane"/> = 指针真正所在的那一栏（同一实体可能两栏都有呈现，高亮只落一处）；</item>
/// <item><see cref="Name"/> = 提示文案里的名称（行名 / 节点名 / 列表空白落点 = 当前目录名）；</item>
/// <item><see cref="Mode"/> = 本次落点会**移动**还是**复制**（Ctrl 决定）——提示文案、光标、最终动作共用它。</item>
/// </list>
/// ⚠️「**没有落点**」用整个对象为 <c>null</c> 表示，与「**落在根上**」严格区分——
/// 前者要熄灭高亮且不显示提示，后者要显示「移动到「全部书签」」。
/// </summary>
public sealed record BrowserDropTarget(string? FolderId, BrowserPane Pane, string Name, TransferMode Mode = TransferMode.Move);
