using System.Windows.Input;
using LinkPocket.Input;
using LinkPocket.ViewModels;

namespace LinkPocket.Views;

/// <summary>
/// 回收站键位表 = **唯一事实源**（与浏览页 BrowserShortcuts 同一子系统；禁止在 XAML/C# 散落键盘处理）。
///
/// <para>**阉割项**（用户定稿 2026-09-19）：无 Ctrl+Z / Ctrl+Y（撤销/重做——回收站内操作不入撤销栈）、
/// 无 Ctrl+X/C/V（**回收站只读，不可搬移**；剪贴板语义会把主表/回收站两种模型混在一起）、
/// 无 F2（无重命名）、无 Ctrl+Shift+N（无新建）、
/// 无 Ctrl+Shift+C / Ctrl+Shift+E（无外部路径语义 / 树展开到当前位置）、**不继承任何全局键**
/// （Ctrl+E/F 只挂浏览页；本表不注册 Global 作用域）。</para>
///
/// <para>保留：导航（后退/前进/返回上级/F5）、选择（↑↓/Ctrl+A/Esc）、打开（Enter）、
/// **还原（Ctrl+R 到原位置 / Ctrl+Shift+R 到根目录，D1 拍板）**、永久删除（Delete，带确认）与右键菜单键。</para>
/// </summary>
public static class TrashShortcuts
{
    /// <summary>构建回收站键位注册表；活跃作用域由视图按"栏归属"给出（主栏 / 左栏）。</summary>
    public static ShortcutRegistry CreateRegistry(TrashViewModel vm)
    {
        var registry = new ShortcutRegistry();
        registry.Register(new[]
        {
            // —— 导航类（两栏通用） ——
            new ShortcutBinding { Key = Key.Back, Scope = ShortcutScope.Trash, Command = vm.GoBackCommand, Description = "后退" },
            new ShortcutBinding { Key = Key.Up, Modifiers = ModifierKeys.Alt, Scope = ShortcutScope.Trash, Command = vm.GoUpCommand, Description = "返回上级" },
            new ShortcutBinding { Key = Key.Left, Modifiers = ModifierKeys.Alt, Scope = ShortcutScope.Trash, Command = vm.GoBackCommand, Description = "后退" },
            new ShortcutBinding { Key = Key.Right, Modifiers = ModifierKeys.Alt, Scope = ShortcutScope.Trash, Command = vm.GoForwardCommand, Description = "前进" },
            new ShortcutBinding { Key = Key.F5, Scope = ShortcutScope.Trash, Command = vm.RefreshCommand, Description = "刷新回收站" },

            // —— 选择 / 打开 / 永久删除（两栏通用） ——
            new ShortcutBinding { Key = Key.A, Modifiers = ModifierKeys.Control, Scope = ShortcutScope.Trash, Command = vm.SelectAllCommand, Description = "全选" },
            new ShortcutBinding { Key = Key.Enter, Scope = ShortcutScope.Trash, Command = vm.OpenSelectionCommand, Description = "打开选中项" },
            new ShortcutBinding { Key = Key.Delete, Scope = ShortcutScope.Trash, Command = vm.PurgeSelectionCommand, Description = "永久删除选中项" },
            new ShortcutBinding { Key = Key.R, Modifiers = ModifierKeys.Control, Scope = ShortcutScope.Trash, Command = vm.RestoreSelectionCommand, Description = "还原选中项到原位置" },
            new ShortcutBinding { Key = Key.R, Modifiers = ModifierKeys.Control | ModifierKeys.Shift, Scope = ShortcutScope.Trash, Command = vm.RestoreSelectionToRootCommand, Description = "还原选中项到根目录" },
            new ShortcutBinding { Key = Key.Escape, Scope = ShortcutScope.Trash, Command = vm.ClearSelectionCommand, Description = "取消选中" },

            // —— 右键菜单键（当前选中行） ——
            new ShortcutBinding { Key = Key.F10, Modifiers = ModifierKeys.Shift, Scope = ShortcutScope.Trash, Command = vm.ShowContextMenuCommand, Description = "右键菜单" },
            new ShortcutBinding { Key = Key.Apps, Scope = ShortcutScope.Trash, Command = vm.ShowContextMenuCommand, Description = "右键菜单" },

            // —— 主栏：↑/↓ 移动选中（单选）+ 滚入视口；End 到末项（边界停住） ——
            new ShortcutBinding { Key = Key.Up, Scope = ShortcutScope.TrashMain, Command = vm.MoveSelectionCommand, CommandParameter = "up", Description = "上移选中" },
            new ShortcutBinding { Key = Key.Down, Scope = ShortcutScope.TrashMain, Command = vm.MoveSelectionCommand, CommandParameter = "down", Description = "下移选中" },
            new ShortcutBinding { Key = Key.End, Scope = ShortcutScope.TrashMain, Command = vm.SelectLastCommand, Description = "选中末项" },

            // —— 左栏：↑/↓ 按可见视觉顺序移动（单元 = 选中 + 进入；链接叶子 = 定位）；←/→ 折叠展开 ——
            new ShortcutBinding { Key = Key.Up, Scope = ShortcutScope.TrashTree, Command = vm.MoveTreeSelectionCommand, CommandParameter = "up", Description = "上移树节点" },
            new ShortcutBinding { Key = Key.Down, Scope = ShortcutScope.TrashTree, Command = vm.MoveTreeSelectionCommand, CommandParameter = "down", Description = "下移树节点" },
            new ShortcutBinding { Key = Key.Left, Scope = ShortcutScope.TrashTree, Command = vm.ToggleTreeExpandCommand, Description = "折叠树节点" },
            new ShortcutBinding { Key = Key.Right, Scope = ShortcutScope.TrashTree, Command = vm.ToggleTreeExpandCommand, Description = "展开树节点" },
        });
        return registry;
    }
}
