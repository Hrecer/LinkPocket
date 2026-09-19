using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace LinkPocket.Input
{
    /// <summary>
    /// 快捷键宿主：挂到页面根（UserControl），统一订阅 PreviewKeyDown 并按**活跃作用域**分发。
    /// 不走 XAML InputBindings 的原因：作用域由"栏归属"（VM 状态）决定，而不是"焦点碰巧落在
    /// 哪个元素"——InputBindings 只能按元素路由，表达不了"同一键在两栏语义不同"。
    /// 输入控件让位：焦点在文本框/密码框/可编辑下拉里时一律不分发（编辑语义优先；
    /// 控件级例外——面包屑编辑框的 Enter/Tab/Esc/↑↓——由控件自己处理，见架构白名单）。
    /// </summary>
    public sealed class ShortcutHost
    {
        private readonly ShortcutRegistry _registry;
        private readonly Func<ShortcutScope> _activeScope;
        private FrameworkElement? _host;

        public ShortcutHost(ShortcutRegistry registry, Func<ShortcutScope> activeScope)
        {
            _registry = registry;
            _activeScope = activeScope;
        }

        /// <summary>装配到宿主（重复 Attach 会先解绑旧宿主，防订阅累积）。</summary>
        public void Attach(FrameworkElement host)
        {
            Detach();
            _host = host;
            host.PreviewKeyDown += OnPreviewKeyDown;
        }

        /// <summary>解绑（VM 换绑 / 页面卸载时调用）。</summary>
        public void Detach()
        {
            if (_host == null) return;
            _host.PreviewKeyDown -= OnPreviewKeyDown;
            _host = null;
        }

        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            // 结构性守卫（危险键绝不跨页/跨上下文误触，用户令 2026-09-19）：
            // 宿主页必须**可见**且**键盘焦点在页内**才分发——页面被切走（Collapsed）或焦点掉到
            // 窗口/其它页时，本页注册表一律不参与。这是可证伪的硬条件，不依赖任何时序假设。
            if (_host is not { IsVisible: true } || !_host.IsKeyboardFocusWithin) return;
            if (IsTextInputFocused()) return;   // 输入控件让位：编辑语义优先

            // Alt 组合键在 WPF 中以 Key.System 到达，真实键在 SystemKey（如 Alt+D → SystemKey=D）
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            var binding = _registry.Resolve(_activeScope(), key, Keyboard.Modifiers);
            if (binding == null) return;

            if (binding.Command.CanExecute(binding.CommandParameter))
                binding.Command.Execute(binding.CommandParameter);
            e.Handled = true;   // 命中即消费（无论 CanExecute 与否）：绝不穿透到其它语义
        }

        /// <summary>焦点是否在输入控件内（文本框 / 密码框 / 可编辑下拉）：是则让位。</summary>
        public static bool IsTextInputFocused()
        {
            return Keyboard.FocusedElement switch
            {
                TextBoxBase => true,
                PasswordBox => true,
                ComboBox { IsEditable: true } => true,
                _ => false
            };
        }
    }
}