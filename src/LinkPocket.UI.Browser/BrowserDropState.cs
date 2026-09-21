using System;
using System.Windows;
using LinkPocket.I18n;

namespace LinkPocket.ViewModels;

/// <summary>
/// 浏览页**拖拽落点状态（控制器）**：从 BrowserViewModel 抽出的内聚状态机——
/// 目标 / 模式 / 提示文案的唯一事实来源（**覆盖式**更新，铁律 9，绝不累积）。
///
/// 宿主（VM）只做三件事：`Set` 写状态、读属性做投影、订阅 <see cref="Changed"/> 刷新
/// 行高亮（拉刷）+ 树高亮（推送）+ 提示文案属性。视图侧（光标 / 提示条 / 松手动作）全部读这里，
/// 不各自再做命中测试或修饰键判定。
/// </summary>
public sealed class BrowserDropState
{
    /// <summary>当前落点（null = 指针不在任何可落点上：空白 / 非法目标 / 链接）。</summary>
    public BrowserDropTarget? Target { get; private set; }

    /// <summary>当前落点会做什么（无落点 = 移动，仅作默认值；调用方只在有落点时用它）。</summary>
    public TransferMode Mode => Target?.Mode ?? TransferMode.Move;

    /// <summary>落点提示文案（空串 = 不显示）：`移动到「X」` / `复制到「X」`——
    /// 文案口径在 <see cref="Views.DragSupport.HintText"/>（唯一实现，与回收站页共用）。</summary>
    public string HintText => Target == null
        ? string.Empty
        // 落点名可能是虚根 token（@root）：拼进用户文案前必须投影成当前语言的根名
        : Views.DragSupport.HintText(BookmarkDisplay.Segment(Target.Name), Target.Mode);

    /// <summary>状态变化（落点或模式）→ 宿主重投影行/树 + 通知提示文案属性。</summary>
    public event Action? Changed;

    /// <summary>
    /// 写入落点：拖拽悬停的**唯一入口**，**覆盖式**（每次 DragOver 重写当前值，既不清零也不累积）。
    /// 传 <c>null</c> = 指针不在任何可落点上；非法目标（拖到自己或自己的后代）也传 null
    /// （光标已用禁止态表达，不该再高亮或提示"移动到"）。
    /// </summary>
    public void Set(BrowserDropTarget? target)
    {
        if (Equals(Target, target)) return;   // record 值相等 = 同一落点：不重复投影（DragOver 会高频触发）
        Target = target;
        Changed?.Invoke();
    }

    /// <summary>
    /// 落点**不变、只换动作**（拖拽中按下/松开 Ctrl）：鼠标没动时 OLE 不会再派发 DragOver，
    /// 但 `QueryContinueDrag` 每次修饰键变化都会触发 → 由此把模式补进落点状态，保证
    /// **松手时执行的动作与提示条说的一致**（光标由 OLE 决定，可能滞后一次，见 WARNINGS）。
    /// </summary>
    public void SetMode(TransferMode mode)
    {
        if (Target == null || Target.Mode == mode) return;
        Set(Target with { Mode = mode });
    }

    /// <summary>拖拽结束（松手 / Esc 取消 / 拖出可落点）统一清空落点：绝不留残留高亮。</summary>
    public void Clear() => Set(null);

    /// <summary>主栏某行是否为当前落点（行 IsDropTarget 直接读这里——行是只读投影）。</summary>
    public bool IsRowTarget(string id)
        => Target is { Pane: BrowserPane.Main } t && string.Equals(t.FolderId, id, StringComparison.Ordinal);

    /// <summary>某树节点实体是否为当前落点（树节点侧是推送式投影的判据；链接叶子与虚根永不作落点）。</summary>
    public bool IsNodeTarget(string? entityId)
        => Target is { Pane: BrowserPane.Tree } t
           && entityId != null
           && string.Equals(t.FolderId, entityId, StringComparison.Ordinal);

    /// <summary>
    /// 修饰键 → 传输模式的**唯一实现**（默认移动；按住 Ctrl = 复制——Windows 单卷口径）。
    /// 三处调用（DragOver 的光标与提示、QueryContinueDrag 的即时刷新、Drop 的最终动作）都走这里，
    /// 不允许任何地方再写第二份 Ctrl 判定。
    /// </summary>
    public static TransferMode ResolveMode(bool controlPressed)
        => controlPressed ? TransferMode.Copy : TransferMode.Move;
}
