using System.Collections.Generic;
using System.Windows.Input;

namespace LinkPocket.Input
{
    /// <summary>
    /// 一条快捷键绑定：键位 + 作用域 + 命令（+ 可选参数 / 描述文案）。
    /// 命令一律走 <see cref="ICommand"/>：CanExecute 决定"这一下按得动按不动"，
    /// 执行前由宿主复核（与按钮同一套命令状态，不另造可用性判据）。
    /// </summary>
    public sealed class ShortcutBinding
    {
        public required Key Key { get; init; }

        public ModifierKeys Modifiers { get; init; }

        public ShortcutScope Scope { get; init; } = ShortcutScope.Browser;

        public required ICommand Command { get; init; }

        /// <summary>命令参数（同一命令服务多键位时区分，如树节点操作）。</summary>
        public object? CommandParameter { get; init; }

        /// <summary>描述文案（冲突报错与提示文案生成共用，如右键菜单里的「(Ctrl+X)」）。</summary>
        public string Description { get; init; } = string.Empty;

        /// <summary>键位显示文本（如 "Ctrl+Shift+N"、"Alt+←"）——提示文案的唯一生成处。</summary>
        public string GestureText
        {
            get
            {
                var parts = new List<string>(4);
                if (Modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
                if (Modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
                if (Modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
                if (Modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
                parts.Add(KeyText(Key));
                return string.Join("+", parts);
            }
        }

        /// <summary>可打印键名（方向键/常见功能键用符号与缩写，其余取枚举名）。</summary>
        private static string KeyText(Key key) => key switch
        {
            Key.Left => "←",
            Key.Right => "→",
            Key.Up => "↑",
            Key.Down => "↓",
            Key.Escape => "Esc",
            Key.Delete => "Del",
            Key.Back => "Backspace",
            Key.Enter => "Enter",
            Key.Space => "Space",
            _ => key.ToString()
        };
    }
}