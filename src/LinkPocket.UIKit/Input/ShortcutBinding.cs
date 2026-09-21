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
        /// <summary>动作说明的文案键（冲突信息给开发者看，显示键而不是句子）。</summary>
        public string DescriptionKey { get; init; } = string.Empty;

        /// <summary>键位显示文本（如 "Ctrl+Shift+N"、"Alt+←"）——提示文案与键位清单的唯一生成处。</summary>
        public string GestureText => FormatGesture(Key, Modifiers);

        /// <summary>键位显示文本（总表/清单导出与绑定共用同一份格式化）。</summary>
        public static string FormatGesture(Key key, ModifierKeys modifiers)
        {
            var parts = new List<string>(4);
            if (modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
            if (modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
            if (modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
            if (modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
            parts.Add(KeyText(key));
            return string.Join("+", parts);
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