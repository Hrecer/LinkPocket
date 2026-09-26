using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using LinkPocket.Contracts;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using LinkPocket.Input;
using LinkPocket.ViewModels;
using LinkPocket.I18n;

namespace LinkPocket.Views.Browser;

public partial class BrowserView : UserControl
{
    // —— 拖拽浮层 + 落点提示（Windows 11 手感）——

    /// <summary>本次拖拽的浮层（半透明行快照 + 移动/复制提示）；拖拽期间存在，结束即摘除。</summary>
    private DragVisualAdorner? _dragVisual;

    /// <summary>本次拖拽是否以 Esc 取消（OLE 在 <c>QueryContinueDrag</c> 里如实告知）。
    /// **取消不是失败**：收尾时据此跳过成环判定，绝不弹窗。</summary>
    private bool _dragCancelledByEscape;

    /// <summary>挂出浮层并开始跟踪指针位置（<c>GiveFeedback</c> 在拖拽期间持续触发——
    /// 拖拽时 WPF 不再派发 MouseMove，只能这样跟手）。</summary>
    private void ShowDragVisual(IReadOnlyList<DragItem> items)
    {
        _dragVisual = DragVisualAdorner.Attach(this);
        _dragCancelledByEscape = false;
        if (_dragVisual == null) return;
        _dragVisual.Show(items);
        _dragVisual.UpdateHint(ViewModel?.DropTargetHintText ?? string.Empty);
        // 拖拽源在本页内 → GiveFeedback / QueryContinueDrag 会冒泡到本页；处理器按方法组注册，成对移除（同一实例语义）
        AddHandler(DragDrop.GiveFeedbackEvent, new GiveFeedbackEventHandler(OnGiveFeedback));
        AddHandler(DragDrop.QueryContinueDragEvent, new QueryContinueDragEventHandler(OnQueryContinueDrag));
    }

    /// <summary>摘除浮层（拖拽结束 / 拖拽被取消）。</summary>
    private void HideDragVisual()
    {
        if (_dragVisual == null) return;
        RemoveHandler(DragDrop.GiveFeedbackEvent, new GiveFeedbackEventHandler(OnGiveFeedback));
        RemoveHandler(DragDrop.QueryContinueDragEvent, new QueryContinueDragEventHandler(OnQueryContinueDrag));
        _dragVisual.Detach();
        _dragVisual = null;
    }

    private void OnGiveFeedback(object sender, GiveFeedbackEventArgs e)
    {
        if (_dragVisual == null || !GetCursorPos(out var screen)) return;
        // 屏幕物理像素 → 本页坐标（PointFromScreen 已处理 DPI 缩放）
        _dragVisual.UpdatePosition(PointFromScreen(new Point(screen.X, screen.Y)));
    }

    /// <summary>
    /// 拖拽中鼠标/键盘状态变化（**修饰键每次变化都会触发**——拖拽期间鼠标不动时 OLE 不再派发 DragOver，
    /// 这里是唯一能拿到"Ctrl 刚被按下/松开"的时机）：
    /// <list type="bullet">
    /// <item>记下 Esc 取消（收尾据此不弹成环窗）；</item>
    /// <item>把当前修饰键映射成落点模式写回 VM 的落点状态——**松手执行的动作与提示条说的永远一致**。
    /// （光标由 OLE 在 DragOver 时按同一规则设置，纯改键不成动鼠标时可能滞后一次，见 WARNINGS。）</item>
    /// </list>
    /// </summary>
    private void OnQueryContinueDrag(object sender, QueryContinueDragEventArgs e)
    {
        if (e.EscapePressed) _dragCancelledByEscape = true;
        ViewModel?.SetDropTargetMode(
            BrowserViewModel.ResolveDropMode((e.KeyStates & DragDropKeyStates.ControlKey) != 0));
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out ScreenPoint point);

    /// <summary>系统双击时间（毫秒）：快速连击判定取**系统设置**，不写死常量。</summary>
    [DllImport("user32.dll")]
    private static extern int GetDoubleClickTime();

    [StructLayout(LayoutKind.Sequential)]
    private struct ScreenPoint
    {
        public int X;
        public int Y;
    }

    /// <summary>写入落点（视图侧唯一出口）：VM 负责高亮投影，浮层负责提示文案——两者永远同步。</summary>
    private void ApplyDropTarget(BrowserDropTarget? target)
    {
        ViewModel?.SetDropTarget(target);
        _dragVisual?.UpdateHint(ViewModel?.DropTargetHintText ?? string.Empty);
    }

    /// <summary>清空落点（视图侧唯一出口）：离开可落点 / 拖拽结束时调用，避免残留高亮与残留提示。</summary>
    private void ClearDropTarget()
    {
        ViewModel?.ClearDropTarget();
        _dragVisual?.UpdateHint(string.Empty);
        Breadcrumb.SetDropHighlight(null);   // 面包屑段高亮一并熄灭（没拖拽经过时是安全的空操作）
    }

    // —— 行拖拽（参考 Windows 资源管理器：按下 → 移动超过阈值 → 进入拖拽）——

    private Point _rowDragStart;

    /// <summary>本次按下命中的行（手势归属凭据；抬起时校验"同一次手势"用）。</summary>
    private BrowserRowViewModel? _pressRow;

    /// <summary>按下时的修饰键：抬起沿用按下时刻的值（中途变键不改变本次点击语义）。</summary>
    private ModifierKeys _pressModifiers;

    /// <summary>按下时的点击计数：≥2 = 双击手势的第二击，绝不承载选择语义（第二击只作打开）。</summary>
    private int _pressClickCount;

    /// <summary>本次按下是否属于"对同一行的快速连击"：与上一次按下同一行且间隔小于系统双击时间。
    /// ⚠️ 不能只信 <see cref="MouseButtonEventArgs.ClickCount"/>：选择/刷新会让行容器被重建，
    /// 重建后 WPF 的 ClickCount 从 1 重新计数 —— 于是"想双击进入文件夹"的第二击被判成单击，
    /// 直接进了改名（用户实测：老是误触重命名、进不去文件夹）。按时间戳判定与元素身份无关。</summary>
    private bool _pressRapidRepeat;

    /// <summary>上一次按下的行与时刻（快速连击判定用）。</summary>
    private object? _lastPressRow;
    private long _lastPressTick;

    /// <summary>本次手势是否已进入拖拽（拖拽结束的抬起不得再补做选择收敛）。</summary>
    private bool _dragStarted;

    /// <summary>右键拖拽：按下起点 + 命中的行（与左键各记一套，两种按钮的拖拽互不干扰）。</summary>
    private Point _rowRightDragStart;
    private BrowserRowViewModel? _rightPressRow;

    /// <summary>右键拖拽手势进行中：期间压掉行/树的右键菜单——右键抬起还会触发一次
    /// <c>WM_CONTEXTMENU</c>，不压就会在本视图的「复制到此处 / 移动到此处」菜单之外再弹一个。
    /// 手势结束（菜单关闭、或没弹出菜单时的消息处理收尾）即复位。</summary>
    private bool _rightDragGesture;

    /// <summary>本次右键拖拽自己弹的菜单：类级 <c>Opened</c> 处理器据此**放行**（不能把自己的菜单也压掉）。</summary>
    private ContextMenu? _rightDragMenu;

    /// <summary>
    /// 本次拖拽松手时的**落点意图**（Drop 处理器只记录、**不执行**）。
    ///
    /// <para>为什么必须先记后执行：OLE 的 Drop 回调发生在拖拽模态循环**内部**——在那里直接执行会在
    /// "拖拽还没结束"时就弹窗 / 写库（实测：规范弹窗弹出来了、拖拽浮层还挂在屏幕上；
    /// 右键拖拽更糟——松开即被执行，还没点菜单东西就搬走了）。</para>
    ///
    /// <para>统一口径：Drop 只写这张"待执行单"，真正执行在 <see cref="StartDrag"/> 里
    /// （循环退出之后、浮层摘除之后）——**左键按它执行、右键拖拽忽略它**（改由菜单选择决定）。</para>
    /// </summary>
    private (IReadOnlyList<DragItem> Items, string? TargetId, TransferMode Mode)? _pendingDrop;

    /// <summary>本次拖拽**从哪一栏发起**（主栏行 = Main / 树节点 = Tree）；不在拖拽中时为 null。</summary>
    private BrowserPane? _dragSourcePane;

    /// <summary>本次拖动集合（与浮层/执行同一份快照）——落点候选据此排除"同栏里自己拖动的那几项"。</summary>
    private IReadOnlyList<DragItem> _dragItems = [];

    /// <summary>按下时该行是否**已是唯一选中**（Windows 慢双击改名的判定依据：第一次单击选中，第二次单击改名）。</summary>
    private bool _pressWasSoleSelection;

    /// <summary>拖拽数据：本次拖动集合（<see cref="DragItem"/> 快照，与行/树 VM 解耦——
    /// 主栏行与树节点都能构造，放置端按 Id 通用）。</summary>
    public record BrowserDragPayload(IReadOnlyList<DragItem> Items);

    /// <summary>
    /// 本次鼠标事件的**命中元素**：真实输入时 <c>OriginalSource</c> = 最深的命中元素；
    /// 合成事件（探针 / 测试直调 <c>RaiseEvent</c>）只填 <c>Source</c>、<c>OriginalSource</c> 为 null
    /// ——只用 OriginalSource 会让判据在合成事件下恒为"没命中"（实测：探针里"点在名字上"判 false）。
    /// </summary>
    private static DependencyObject? HitOf(MouseButtonEventArgs e)
        => (e.OriginalSource ?? e.Source) as DependencyObject;

    /// <summary>按下：记下手势凭据；无修饰键按未选中行 = 立即单选（Windows 按下即反馈）。</summary>
    private void RowBorder_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _rowDragStart = e.GetPosition(this);
        _dragStarted = false;
        _pressRow = null;                      // 先清凭据：下面任何早退都不得留下上一次的手势
        _pressWasSoleSelection = false;
        _pressModifiers = Keyboard.Modifiers;
        _pressClickCount = e.ClickCount;

        // 就地改名编辑框内的鼠标操作（定位光标 / 选词 / 双击选词）归编辑框自己：
        // 不参与行选择、不进入拖拽、也不承载双击打开（否则双击编辑框会把目录打开）。
        if (InlineNameEditor.IsWithin(HitOf(e))) return;

        _pressRow = (sender as FrameworkElement)?.DataContext as BrowserRowViewModel;
        if (ViewModel == null || _pressRow == null) return;
        ViewModel.ActivatePane(BrowserPane.Main);   // 点主栏 = 该栏获得键盘语义归属（焦点随之收进页面）
        // 记下"按下时它已是唯一选中"——抬起据此判定"再次单击同一项"（Windows 慢双击改名）
        _pressWasSoleSelection = _pressRow.IsSelected && ViewModel.SelectionCount == 1;
        // 快速连击（同一行、间隔 < 系统双击时间）= 用户想双击打开 → 改名让位
        var now = Environment.TickCount64;
        _pressRapidRepeat = ReferenceEquals(_lastPressRow, _pressRow)
            && now - _lastPressTick < GetDoubleClickTime();
        _lastPressRow = _pressRow;
        _lastPressTick = now;
        if (_pressModifiers == ModifierKeys.None && !_pressRow.IsSelected)
            ViewModel.SelectRowWithModifiers(_pressRow, ModifierKeys.None);
    }

    /// <summary>
    /// 右键按下：只记拖拽起点与命中行（**右键不改选中**——选中语义只由左键单击与键盘决定；
    /// 也不在此提交改名，那由菜单打开时的类级处理器统一做）。
    /// </summary>
    private void RowBorder_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        _rowRightDragStart = e.GetPosition(this);
        _rightPressRow = InlineNameEditor.IsWithin(HitOf(e))
            ? null
            : (sender as FrameworkElement)?.DataContext as BrowserRowViewModel;
        _rightDragGesture = false;   // 新手势开始：上一次的抑制窗口到此为止
        _rightDragMenu = null;
    }

    /// <summary>
    /// 行上移动：按**按下的按钮**分派——左键超过阈值 = 左键拖拽，右键超过阈值 = 右键拖拽。
    /// 两者都只做"是否进入拖拽"的判定，真正的起手式与收尾在 <see cref="StartDrag"/>（一条路径，两处不各写一份）。
    /// </summary>
    private void RowBorder_MouseMove(object sender, MouseEventArgs e)
    {
        // 右键拖拽（Windows 口径：右键拖到目标松手 → 弹「复制到此处 / 移动到此处 / 取消」）
        if (e.RightButton == MouseButtonState.Pressed)
        {
            if (_rightPressRow == null) return;
            if (!DragSupport.BeyondThreshold(e.GetPosition(this), _rowRightDragStart)) return;
            var rightRow = _rightPressRow;
            _rightPressRow = null;   // 本次手势只发起一次
            StartRowDrag((DependencyObject)sender, rightRow, rightButton: true);
            return;
        }

        if (e.LeftButton != MouseButtonState.Pressed) return;
        if (_pressRow == null) return;   // 按下不在行主体（如落在改名编辑框内）→ 不进入行拖拽
        if (!DragSupport.BeyondThreshold(e.GetPosition(this), _rowDragStart)) return;
        _dragStarted = true;             // 拖拽结束的抬起不得再补做选择收敛
        StartRowDrag((DependencyObject)sender, _pressRow, rightButton: false);
    }

    // 起手阈值与 OLE 效果映射已上收 UIKit（Views.DragSupport，浏览页/回收站共用同一口径）。

    /// <summary>当前修饰键 → 落点模式（**唯一**：Ctrl = 复制；各行/节点 DragOver 的提示与光标都读它）。</summary>
    private static TransferMode CurrentDropMode()
        => BrowserViewModel.ResolveDropMode((Keyboard.Modifiers & ModifierKeys.Control) != 0);

    /// <summary>
    /// 发起一次主栏行拖拽：载荷与选中语义收敛在 VM（<c>PrepareDragFromRow</c>——拖未选中行先单选、
    /// 拖已选中行拖整个集合），视图只负责浮层、OLE 循环与收尾。
    /// </summary>
    private void StartRowDrag(DependencyObject source, BrowserRowViewModel row, bool rightButton)
    {
        if (ViewModel == null) return;
        var items = ViewModel.PrepareDragFromRow(row);
        if (items.Count == 0) return;
        StartDrag(source, items, rightButton, BrowserPane.Main);
    }

    /// <summary>
    /// 一次拖拽的完整生命周期（行左键 / 行右键 / 树节点**共用**）：浮层 → OLE 循环 → 收尾。
    ///
    /// <para>允许的效果是 <c>Move | Copy</c>：只给 Move 的话 OLE 会把 DragOver 里设的 Copy **夹成 None**，
    /// 复制光标永远出不来（"按 Ctrl 拖动 = 复制"的前提）。</para>
    ///
    /// <para>收尾在**拖拽循环退出之后**才做（顺序很关键）：先摘浮层、再清落点，然后
    /// ① 左键 → 执行 Drop 记下的意图（**成环由传输流水线统一拒绝并弹规范弹窗**——与粘贴同一条路径）；
    /// ② 右键 → 弹「复制到「X」/ 移动到「X」/ 取消」，**不选就不搬任何东西**（Windows 口径）。</para>
    ///
    /// <para>Esc 取消 = OLE 不派发 Drop → 待执行单为空 → 什么都不做（**结构性保证**：
    /// 再也没有"途经记账"那类判据可以出错）。</para>
    ///
    /// <para><paramref name="sourcePane"/> = 本次拖拽**从哪一栏发起**（主栏行 = Main / 树节点 = Tree）：
    /// 与"本次拖动集合"一起决定落点候选是否排除**该项自身的元素**（同栏放回原处 = 取消，见
    /// <see cref="IsDropPositionCandidate"/>）。跨栏拖到同一实体仍是落点 → 照常高亮，松手由执行层判成环并弹窗。</para>
    /// </summary>
    private void StartDrag(DependencyObject source, IReadOnlyList<DragItem> items, bool rightButton,
        BrowserPane sourcePane)
    {
        _rightDragGesture = rightButton;
        _pendingDrop = null;
        _dragSourcePane = sourcePane;
        _dragItems = items;
        ShowDragVisual(items);
        DragDrop.DoDragDrop(source, new DataObject(new BrowserDragPayload(items)),
            DragDropEffects.Move | DragDropEffects.Copy);
        HideDragVisual();

        // 落点状态（含模式）在清空**之前**读走：它就是"松手时呈现的那个动作"的唯一事实来源
        //（模式由 DragOver 与 QueryContinueDrag 共同维护，这里绝不第二次判定 Ctrl）。
        var target = ViewModel?.DropTarget;
        var mode = target?.Mode ?? TransferMode.Move;
        var drop = _pendingDrop;
        _pendingDrop = null;
        ClearDropTarget();
        // 覆盖式 + 及时复位：拖拽结束即不再有"源栏 / 拖动集合"的概念（跨事件状态绝不留常驻标志）
        _dragSourcePane = null;
        _dragItems = [];

        if (rightButton)
        {
            // Esc 取消 = 什么都没发生（Windows 同口径）：右键拖拽取消同样不弹菜单。
            if (_dragCancelledByEscape)
            {
                Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => _rightDragGesture = false));
                return;
            }
            // 右键拖拽**不执行** drop（尚未选择）；执行由菜单项决定（同一条传输流水线）。
            ShowRightDragDropMenu(items, target, mode);
            return;
        }

        if (drop is { } pending)
            _ = ViewModel?.DropItemsAsync(pending.Items, pending.TargetId, pending.Mode);
    }

    /// <summary>
    /// 右键拖拽松手菜单（Windows 口径）：在松手位置弹出「复制到此处 / 移动到此处 / 取消」。
    /// 目标 = **松手时的落点状态**（DragOver 写的唯一事实来源，含"列表空白 = 当前目录"）；
    /// 落点为空（非法目标 / 窗口外）= 不给菜单——没有可选项，也就没有"此处"。
    /// 菜单打开期间**保留落点高亮**（据此确认"此处"是哪里），菜单关闭时熄灭。
    /// </summary>
    private void ShowRightDragDropMenu(IReadOnlyList<DragItem> items, BrowserDropTarget? target, TransferMode mode)
    {
        if (ViewModel == null || target == null)
        {
            // 没弹出自己的菜单：抑制窗口延续到消息处理收尾（右键抬起可能还会再触发一次系统菜单）
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => _rightDragGesture = false));
            return;
        }

        var menu = new ContextMenu { DataContext = ViewModel };   // 与其它菜单同口径：打开即收口改名态
        menu.Resources.Add(typeof(MenuItem), (Style)FindResource("LpMenuItem"));

        foreach (var action in BuildRightDragMenuItems(items, target.FolderId, target.Name)) menu.Items.Add(action);
        // 菜单不加分隔线——三项连续排列（复制 / 移动 / 取消）。
        var cancel = new MenuItem { Header = Loc.T("common.cancel") };
        cancel.Click += (_, _) => menu.IsOpen = false;
        menu.Items.Add(cancel);

        menu.Closed += (_, _) =>
        {
            ClearDropTarget();          // 手势彻底结束：落点高亮与提示一起熄灭
            _rightDragGesture = false;
            _rightDragMenu = null;
        };

        _rightDragMenu = menu;
        menu.PlacementTarget = this;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        menu.IsOpen = true;
    }

    /// <summary>
    /// 右键拖拽菜单里的**动作项**（纯构造，不涉及弹出——渲染检查据此直调断言内容）：
    /// 点它 = 用同一条传输流水线执行（不另开写库路径）。
    /// 顺序与 Windows 一致（复制在前），文案把"此处"直接写成目标名（不靠高亮去猜）。
    /// </summary>
    private List<MenuItem> BuildRightDragMenuItems(IReadOnlyList<DragItem> items, string? targetId, string targetName)
    {
        var shown = BookmarkDisplay.Segment(targetName);   // 虚根落点是 token，拼文案前投影
        var copyLabel = Loc.T("browser.menu.copyTo", shown);
        var copy = new MenuItem { Header = copyLabel };
        copy.Click += (_, _) => _ = ViewModel?.DropItemsAsync(items, targetId, TransferMode.Copy);

        var moveLabel = Loc.T("browser.menu.moveTo", shown);
        var move = new MenuItem { Header = moveLabel };
        move.Click += (_, _) => _ = ViewModel?.DropItemsAsync(items, targetId, TransferMode.Move);

        return new List<MenuItem> { copy, move };
    }

    // 拖拽收尾（成环弹窗 + 右键菜单 + 执行）统一在 `StartDrag` 内：它同时服务主栏行与树节点。
    // 视图**不再**自带成环判定：成环（拖到自己/自己的子文件夹）与其它非法情形统一由传输流水线拒绝并弹窗
    // ——与"剪切粘贴"共用同一个弹窗，对外口径只有一套。

    /// <summary>
    /// 落点候选判定（**唯一实现**，主栏行与树节点共用）：**主栏文件夹行 / 树非链接节点**才作落点。
    /// 链接行与树上的链接叶子**不是**落点（拖到书签上什么都不发生，与 Explorer 一致）。
    ///
    /// <para><b>同栏里「自己拖动的那几项」不作落点</b>：把文件夹拖回它自己在**同一栏**里的
    /// 行/节点 = 「放回原处」= 取消——不记意图、不高亮、不弹窗（原先会走到执行层判成环并弹「无法移动」）。
    /// 判据是「**动作真正落在哪**」：候选栏 = 本次拖拽的发起栏（<see cref="_dragSourcePane"/>）且候选实体 ∈
    /// 本次拖动集合（<see cref="_dragItems"/>）。**跨栏**拖到同一实体（树 → 主栏那一行 / 主栏 → 树那个节点）
    /// 仍是落点 → 照常高亮 + 提示，松手由执行层统一判成环并弹规范弹窗（这是"真的在把它搬进它自己"）。
    /// 「全部书签」虚根的 <c>FolderId</c> 为 null（= 根目录）→ 与集合无关，照常可作落点。</para>
    ///
    /// <para>⚠️ 成环（自身后代）**不在这里判定**——真正的成环落点一视同仁地高亮 + 显示提示，
    /// 松手之后由执行层（传输流水线）统一拒绝并弹规范弹窗；这是统一口径
    /// （过去"悬停禁用光标 + 无提示"与"粘贴弹窗"是两套，现统一为都弹窗）。</para>
    /// </summary>
    private bool IsDropPositionCandidate(object? dataContext, BrowserPane candidatePane)
    {
        if (dataContext is not (BrowserRowViewModel { IsFolder: true } or FolderNode { IsLink: false }))
            return false;

        var id = dataContext switch
        {
            BrowserRowViewModel row => row.Id,
            FolderNode node => node.FolderId,
            _ => null
        };
        if (id == null || candidatePane != _dragSourcePane) return true;

        foreach (var item in _dragItems)
            if (string.Equals(item.Id, id, StringComparison.Ordinal)) return false;
        return true;
    }

    private void RowBorder_DragOver(object sender, DragEventArgs e)
    {
        try
        {
            var row = (sender as FrameworkElement)?.DataContext as BrowserRowViewModel;

            // 拖拽中只表达"落在哪个文件夹"（高亮 + 「移动到/复制到 X」提示 + 对应光标），**不判成环**：
            // 落点是不是自己/自己的后代，由执行层在**松手之后**统一拒绝并弹窗。
            // 模式（Ctrl = 复制）随修饰键走同一条落点状态：提示文案与光标永远说的是同一件事。
            var ok = IsDropPositionCandidate(row, BrowserPane.Main);
            var mode = CurrentDropMode();
            ApplyDropTarget(ok ? new BrowserDropTarget(row!.Id, BrowserPane.Main, row.Name, mode) : null);
            e.Effects = ok ? DragSupport.EffectFor(mode) : DragDropEffects.None;
        }
        finally
        {
            e.Handled = true;   // 无论沿途是否异常，本事件归属拖拽流程（防冒泡到其它落点）
        }
    }

    /// <summary>拖拽离开行：熄灭落点高亮。落点是**覆盖式**状态（铁律 9），离开必须清零；
    /// 随即 DragOver 会重设真正的新落点，所以行间移动只会看到高亮"跟着指针走"。</summary>
    private void RowBorder_DragLeave(object sender, DragEventArgs e) => ClearDropTarget();

    /// <summary>落在行上：**只记意图不执行**（执行在 <see cref="StartDrag"/> 里、拖拽循环退出之后）。</summary>
    private void RowBorder_Drop(object sender, DragEventArgs e)
    {
        try
        {
            var payload = e.Data.GetData(typeof(BrowserDragPayload)) as BrowserDragPayload;
            var row = (sender as FrameworkElement)?.DataContext as BrowserRowViewModel;
            if (payload != null && IsDropPositionCandidate(row, BrowserPane.Main))
                _pendingDrop = (payload.Items, row!.Id, ViewModel?.DropTargetMode ?? TransferMode.Move);
        }
        finally
        {
            e.Handled = true;
        }
    }

    // —— 列表卡空白落点（Windows 口径：拖到文件夹内容区空白 = 落在**当前所在文件夹**）——

    /// <summary>
    /// 拖到列表空白：落点 = **当前所在文件夹**（没有目标项，因此不高亮任何行，只给提示）。
    /// 行上的 DragOver 会 <c>e.Handled = true</c>，所以指针在行上时不会走到这里（行优先、语义更具体）。
    /// </summary>
    private void ListCard_DragOver(object sender, DragEventArgs e)
    {
        try
        {
            var payload = e.Data.GetData(typeof(BrowserDragPayload)) as BrowserDragPayload;
            if (payload == null || ViewModel == null) return;
            var mode = CurrentDropMode();
            ApplyDropTarget(new BrowserDropTarget(
                ViewModel.CurrentFolderId, BrowserPane.Main, ViewModel.CurrentFolderDisplayName, mode));
            e.Effects = DragSupport.EffectFor(mode);
        }
        finally
        {
            e.Handled = true;
        }
    }

    /// <summary>拖拽离开列表卡：熄灭落点提示（覆盖式状态，离开清零）。</summary>
    private void ListCard_DragLeave(object sender, DragEventArgs e) => ClearDropTarget();

    /// <summary>
    /// 落在列表空白 = 落进**当前所在文件夹**（Windows 口径）。按住 Ctrl 时就是"在本页做一个副本"：
    /// 文件夹副本由引擎按同层唯一命名规范编号（「名 (2)」）；移动模式下已在目标目录的项由传输流水线
    /// 自动记为"已在目标位置"（无操作，非错误），与 Explorer 一致。
    ///
    /// <para>"把当前文件夹拖到它自己的空白上"这类成环也走同一条路：**只记意图**，
    /// 由传输流水线在拖拽结束之后统一拒绝并弹规范弹窗（与粘贴同一个弹窗）。</para>
    /// </summary>
    private void ListCard_Drop(object sender, DragEventArgs e)
    {
        try
        {
            var payload = e.Data.GetData(typeof(BrowserDragPayload)) as BrowserDragPayload;
            if (payload != null && ViewModel != null)
                _pendingDrop = (payload.Items, ViewModel.CurrentFolderId, ViewModel.DropTargetMode);
        }
        finally
        {
            e.Handled = true;
        }
    }

    // —— 树节点拖拽源/拖放/选中（FolderTreePanel 事件转发）——

    /// <summary>
    /// 树节点**拖拽源**（左键与右键都由面板上报，据 <c>e.RightButton</c> 分派）：载荷与选中语义完全复用主栏那一套
    /// （VM <see cref="BrowserViewModel.PrepareDragFromNode"/>）——拖未选中节点先单选该节点、拖已选中节点拖动整个选中集合
    /// （树选中同样落在唯一选中集合里）。「全部书签」虚根不是实体 → 载荷为空 → 不发起拖拽。
    /// 起手式与收尾与主栏**同一个入口** <see cref="StartDrag"/>（浮层 / OLE 循环 / 右键菜单 / 成环判定都不各写一份）。
    /// </summary>
    private void FolderTreePanel_NodeDragStartRequested(object? sender, TreeItemDragStartEventArgs e)
    {
        if (ViewModel == null || e.Node is not FolderNode node || e.Source == null) return;
        var items = ViewModel.PrepareDragFromNode(node);
        if (items.Count == 0) return;
        StartDrag(e.Source, items, e.RightButton, BrowserPane.Tree);
    }

    /// <summary>树节点拖拽经过：命中节点是真实文件夹才作落点（链接叶子不是移动目标——**拖到书签上什么也不发生**）。
    /// 与主栏同口径：合法 = 光标 + 落点高亮 + 「移动到/复制到 X」提示；**不判成环**（成环由执行层统一拒绝并弹窗）。
    /// 「全部书签」虚根 FolderId 为 null = 根目录，是合法落点（移到/复制到根）。</summary>
    private void FolderTreePanel_NodeDragOver(object? sender, TreeItemDragEventArgs e)
    {
        try
        {
            var node = e.Node as FolderNode;
            var ok = IsDropPositionCandidate(node, BrowserPane.Tree);
            var mode = CurrentDropMode();
            ApplyDropTarget(ok ? new BrowserDropTarget(node!.FolderId, BrowserPane.Tree, node.Name, mode) : null);
            e.Args.Effects = ok ? DragSupport.EffectFor(mode) : DragDropEffects.None;
        }
        finally
        {
            e.Args.Handled = true;
        }
    }

    /// <summary>拖拽离开树节点：熄灭落点高亮（覆盖式状态，离开清零；新落点由随后的 DragOver 覆盖写入）。</summary>
    private void FolderTreePanel_NodeDragLeave(object? sender, TreeItemDragEventArgs e) => ClearDropTarget();

    /// <summary>落在树节点：**只记意图不执行**（执行在 <see cref="StartDrag"/> 里、拖拽循环退出之后）。
    /// 模式取自落点状态（Ctrl = 复制），与提示条说的是同一个值。</summary>
    private void FolderTreePanel_NodeDrop(object? sender, TreeItemDragEventArgs e)
    {
        try
        {
            var payload = e.Args.Data.GetData(typeof(BrowserDragPayload)) as BrowserDragPayload;
            var node = e.Node as FolderNode;
            if (payload != null && IsDropPositionCandidate(node, BrowserPane.Tree))
                _pendingDrop = (payload.Items, node!.FolderId, ViewModel?.DropTargetMode ?? TransferMode.Move);
        }
        finally
        {
            e.Args.Handled = true;
        }
    }

    /// <summary>点击树节点行主体统一交给 VM（数据驱动选中）：
    /// 文件夹 → 选中并进入；链接叶子 → 主区定位选中该行；「全部书签」虚拟根 → 进入根目录（不写选中）。
    /// 树高亮由 FolderNode.IsSelected 从 VM 唯一选中集合派生，无需在此记录目标或操作容器。</summary>
    private void FolderTreePanel_NodeSelected(object? sender, object? node)
    {
        if (ViewModel == null || node is not FolderNode fn) return;
        ViewModel.ActivatePane(BrowserPane.Tree);   // 点左栏 = 该栏获得键盘语义归属（焦点随之收进页面）
        _ = ViewModel.SelectTreeNodeAsync(fn);
    }

    /// <summary>点击文件夹树空白：清空选中（主栏 + 树一起取消，唯一事实来源清空）。</summary>
    private void FolderTreePanel_BackgroundClicked(object? sender, EventArgs e)
    {
        ViewModel?.ActivatePane(BrowserPane.Tree);
        if (ViewModel != null) ViewModel.ClearSelection();
    }

    // 进入路径编辑态时的"聚焦 + 全选"归 BreadcrumbBar 控件自身（编辑框一见可见就聚焦）：
    // 视图不再订阅 VM 属性通知去抓编辑框——通知链里可视状态尚未落地，聚焦会静默失败（实测见 WARNINGS）。

    // 列表卡"点空白清选中"已收口到唯一实现 UIKit `Views.BlankClick`（XAML 上按区域挂载，
    // 含"按下/抬起双空白 + 单击"归属校验；命中列表行不算空白——行外层 Border 带 Tag="BrowserRow"）。
    // 本文件不再保留第二份手写命中测试（铁律 10：同类行为只有一条实现）。
}
