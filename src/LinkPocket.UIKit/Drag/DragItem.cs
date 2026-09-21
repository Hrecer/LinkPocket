namespace LinkPocket.ViewModels;

/// <summary>
/// 拖拽载荷项（轻量：搬运一个实体只需要这三件事）：
/// <see cref="Id"/> = 实体 ID（文件夹 ID / 链接 ID）、<see cref="IsFolder"/> = 类型、<see cref="Name"/> = 显示名（提示文案用）。
///
/// <para>为什么不再直接搬行 VM：载荷原先绑死 <c>BrowserRowViewModel</c>，导致**只有主栏行能发起拖拽**——
/// 左栏树节点即使选中了同一批实体也无从构造载荷。抽象成轻量项后，主栏行与树节点都能构造，
/// 放置端（主栏文件夹行 / 树节点）与移动逻辑一律按 <see cref="Id"/> + <see cref="IsFolder"/> 通用，
/// 所以"树 → 主栏""树 → 树"与"主栏 → 主栏"走的是同一条移动路径。</para>
///
/// <para>它同时是**传输流水线的唯一载荷项**（<c>BrowserViewModel.TransferAsync</c>）：拖拽、右键拖拽、剪贴板粘贴
/// 全都投影成同一种项再走同一条流水线——不再有"每种入口各写一份逐项循环"的余地。
/// （剪贴板自己存的是 ID 清单 = 存储格式，不是第二份传输实现。）</para>
///
/// <para>归属 <c>LinkPocket.UIKit</c>：浏览页与回收站
/// 两页共用同一份拖拽载荷（回收站内搬移也走同一个形状），不再各持一份。</para>
/// </summary>
public sealed record DragItem(string Id, bool IsFolder, string Name);

/// <summary>
/// 传输模式（**唯一决定"会发生什么"**）：默认移动；按住 Ctrl = 复制。
/// 与提示文案（`移动到「X」`/`复制到「X」`）、拖拽光标（Move/Copy）、最终执行的动作**同源**——
/// 落点状态里存的就是它，界面各处只做投影，绝不各判一次。
/// ⚠️ 回收站**只允许移动**（站内搬移不产生副本——ID 唯一），其落点状态恒为 <see cref="Move"/>。
/// </summary>
public enum TransferMode
{
    Move,
    Copy,
}
