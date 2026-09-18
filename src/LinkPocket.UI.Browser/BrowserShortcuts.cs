using System.Windows.Input;
using LinkPocket.Input;
using LinkPocket.ViewModels;

namespace LinkPocket.Views.Browser
{
    /// <summary>
    /// 浏览页快捷键键位表 = **唯一事实源**：所有浏览页快捷键都在此声明（键位 / 作用域 / 命令 / 描述），
    /// 禁止在 XAML InputBindings 或 code-behind KeyDown 里散落
    /// （架构红线：全仓除 Input/ 子系统与控件级白名单外不得出现 KeyBinding/KeyDown 处理）。
    /// 解析顺序 = 活跃作用域由内向外回退（见 <see cref="ShortcutScopes.Chain"/>）。
    /// </summary>
    public static class BrowserShortcuts
    {
        /// <summary>
        /// 构建浏览页键位注册表。活跃作用域由视图给出：
        /// 主栏 = <see cref="ShortcutScope.BrowserMain"/>；左栏 = <see cref="ShortcutScope.BrowserTree"/>；
        /// 两栏通用键声明在 <see cref="ShortcutScope.Browser"/>（子作用域自动回退命中）。
        /// </summary>
        public static ShortcutRegistry CreateRegistry(BrowserViewModel vm)
        {
            var registry = new ShortcutRegistry();
            registry.Register(new[]
            {
                // —— 编辑类（两栏通用：选中集合是唯一事实源，栏不改变命令语义）——
                new ShortcutBinding { Key = Key.X, Modifiers = ModifierKeys.Control, Scope = ShortcutScope.Browser, Command = vm.CutCommand, Description = "剪切" },
                new ShortcutBinding { Key = Key.C, Modifiers = ModifierKeys.Control, Scope = ShortcutScope.Browser, Command = vm.CopyCommand, Description = "复制" },
                new ShortcutBinding { Key = Key.V, Modifiers = ModifierKeys.Control, Scope = ShortcutScope.Browser, Command = vm.PasteCommand, Description = "粘贴" },
                new ShortcutBinding { Key = Key.A, Modifiers = ModifierKeys.Control, Scope = ShortcutScope.Browser, Command = vm.SelectAllCommand, Description = "全选" },
                new ShortcutBinding { Key = Key.Delete, Scope = ShortcutScope.Browser, Command = vm.DeleteSelectionCommand, Description = "删除选中项" },
                new ShortcutBinding { Key = Key.F2, Scope = ShortcutScope.Browser, Command = vm.RenameSelectionCommand, Description = "重命名选中项" },
                new ShortcutBinding { Key = Key.Enter, Scope = ShortcutScope.Browser, Command = vm.OpenSelectionCommand, Description = "打开选中项" },

                // —— 导航类 ——
                new ShortcutBinding { Key = Key.Back, Scope = ShortcutScope.Browser, Command = vm.GoBackCommand, Description = "后退" },
                new ShortcutBinding { Key = Key.Up, Modifiers = ModifierKeys.Alt, Scope = ShortcutScope.Browser, Command = vm.GoUpCommand, Description = "返回上级" },
                new ShortcutBinding { Key = Key.Left, Modifiers = ModifierKeys.Alt, Scope = ShortcutScope.Browser, Command = vm.GoBackCommand, Description = "后退" },
                new ShortcutBinding { Key = Key.Right, Modifiers = ModifierKeys.Alt, Scope = ShortcutScope.Browser, Command = vm.GoForwardCommand, Description = "前进" },
                new ShortcutBinding { Key = Key.F5, Scope = ShortcutScope.Browser, Command = vm.RefreshCommand, Description = "刷新当前目录" },
                new ShortcutBinding { Key = Key.D, Modifiers = ModifierKeys.Alt, Scope = ShortcutScope.Browser, Command = vm.EnterPathEditCommand, Description = "聚焦地址栏" },
                new ShortcutBinding { Key = Key.E, Modifiers = ModifierKeys.Control | ModifierKeys.Shift, Scope = ShortcutScope.Browser, Command = vm.ExpandTreeToCurrentCommand, Description = "展开到当前位置" },
                new ShortcutBinding { Key = Key.N, Modifiers = ModifierKeys.Control | ModifierKeys.Shift, Scope = ShortcutScope.Browser, Command = vm.NewFolderCommand, Description = "新建文件夹" },
                new ShortcutBinding { Key = Key.C, Modifiers = ModifierKeys.Control | ModifierKeys.Shift, Scope = ShortcutScope.Browser, Command = vm.CopyPathCommand, Description = "复制路径" },
                new ShortcutBinding { Key = Key.Z, Modifiers = ModifierKeys.Control, Scope = ShortcutScope.Browser, Command = vm.UndoCommand, Description = "撤销" },
                new ShortcutBinding { Key = Key.Y, Modifiers = ModifierKeys.Control, Scope = ShortcutScope.Browser, Command = vm.RedoCommand, Description = "重做" },

                // —— 两栏通用：右键菜单键（当前选中行）——
                new ShortcutBinding { Key = Key.F10, Modifiers = ModifierKeys.Shift, Scope = ShortcutScope.Browser, Command = vm.ShowContextMenuCommand, Description = "右键菜单" },
                new ShortcutBinding { Key = Key.Apps, Scope = ShortcutScope.Browser, Command = vm.ShowContextMenuCommand, Description = "右键菜单" },

                // —— 主栏：↑/↓ 移动选中（单选）+ 滚入视口；End 到末项（Explorer 口径，边界停住）——
                new ShortcutBinding { Key = Key.Up, Scope = ShortcutScope.BrowserMain, Command = vm.MoveSelectionCommand, CommandParameter = "up", Description = "上移选中" },
                new ShortcutBinding { Key = Key.Down, Scope = ShortcutScope.BrowserMain, Command = vm.MoveSelectionCommand, CommandParameter = "down", Description = "下移选中" },
                new ShortcutBinding { Key = Key.End, Scope = ShortcutScope.BrowserMain, Command = vm.SelectLastCommand, Description = "选中末项" },

                // —— 左栏：↑/↓ 按可见视觉顺序移动（与鼠标同语义：文件夹 = 选中 + 进入）；←/→ 折叠展开 ——
                new ShortcutBinding { Key = Key.Up, Scope = ShortcutScope.BrowserTree, Command = vm.MoveTreeSelectionCommand, CommandParameter = "up", Description = "上移树节点" },
                new ShortcutBinding { Key = Key.Down, Scope = ShortcutScope.BrowserTree, Command = vm.MoveTreeSelectionCommand, CommandParameter = "down", Description = "下移树节点" },
                new ShortcutBinding { Key = Key.Left, Scope = ShortcutScope.BrowserTree, Command = vm.ToggleTreeExpandCommand, Description = "折叠树节点" },
                new ShortcutBinding { Key = Key.Right, Scope = ShortcutScope.BrowserTree, Command = vm.ToggleTreeExpandCommand, Description = "展开树节点" },

                // —— Esc（分层：有剪切态先取消剪切，否则清空选中）——
                new ShortcutBinding { Key = Key.Escape, Scope = ShortcutScope.Browser, Command = vm.EscapeCommand, Description = "取消剪切 / 取消选中" },

                // —— 全局（任何页面）：切到搜索页并聚焦搜索框 ——
                new ShortcutBinding { Key = Key.E, Modifiers = ModifierKeys.Control, Scope = ShortcutScope.Global, Command = vm.NavigateToSearchCommand, Description = "搜索" },
                new ShortcutBinding { Key = Key.F, Modifiers = ModifierKeys.Control, Scope = ShortcutScope.Global, Command = vm.NavigateToSearchCommand, Description = "搜索" },
            });
            return registry;
        }
    }
}