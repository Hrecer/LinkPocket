using System;
using System.Collections.Generic;
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

        /// <summary>
        /// 挂上该页在**总表**里声明的控件锚定绑定（输入框内的 Enter 这类"输入框内按键"）：
        /// 只在该控件获得焦点时生效，且不参与页面级分发（编辑语义优先，互不打架）。
        /// 控件名在宿主的命名域里找不到即抛 —— 键位表与页面接线不一致属于装配缺陷，启动就暴露。
        /// </summary>
        public void AttachControls(ShortcutPage page, FrameworkElement nameScopeOwner, IShortcutCommands commands)
        {
            _controlHandlers.Clear();
            foreach (var (controlName, binding) in ShortcutCatalog.ControlBindings(page, commands))
            {
                if (nameScopeOwner.FindName(controlName) is not FrameworkElement control)
                    throw new InvalidOperationException(
                        $"shortcut control anchor not found: page {page} binds keys to control '{controlName}', which the page name scope does not contain.");
                KeyEventHandler handler = (_, e) => OnControlKeyDown(control, binding, e);
                control.PreviewKeyDown += handler;
                _controlHandlers.Add((control, handler));
            }
        }

        private void OnControlKeyDown(FrameworkElement control, ShortcutBinding binding, KeyEventArgs e)
        {
            if (_host is not { IsVisible: true }) return;   // 宿主页不可见（切页）时一律不分发
            if (e.Key != binding.Key || Keyboard.Modifiers != binding.Modifiers) return;
            if (binding.Command.CanExecute(binding.CommandParameter))
                binding.Command.Execute(binding.CommandParameter);
            e.Handled = true;
        }

        /// <summary>解绑（VM 换绑 / 页面卸载时调用；含控件锚定绑定的解绑）。</summary>
        public void Detach()
        {
            foreach (var (control, handler) in _controlHandlers) control.PreviewKeyDown -= handler;
            _controlHandlers.Clear();
            if (_host == null) return;
            _host.PreviewKeyDown -= OnPreviewKeyDown;
            _host = null;
        }

        private readonly List<(FrameworkElement Control, KeyEventHandler Handler)> _controlHandlers = new();

        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            // 结构性守卫（危险键绝不跨页/跨上下文误触）：
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

        /// <summary>焦点是否在输入控件内（文本框 / 密码框 / 可编辑下拉）：是则让位。
        /// ⚠️ **只读**文本（展示型可拖选文本 <see cref="Views.SelectableText"/>）只有**真的选中了内容**才让位
        /// （此时 Ctrl+C 复制选区是用户预期）；否则页面快捷键照常可用——否则点一下网址 / 描述，
        /// 整页快捷键（Ctrl+A / Delete / Esc / ↑↓ / Ctrl+R…）就会静默失效，而这页根本没有编辑语义。</summary>
        public static bool IsTextInputFocused()
        {
            return Keyboard.FocusedElement switch
            {
                TextBox tb => !tb.IsReadOnly || tb.SelectionLength > 0,
                TextBoxBase => true,   // 其它可编辑文本宿主（如 RichTextBox）：按输入控件让位
                PasswordBox => true,
                ComboBox { IsEditable: true } => true,
                _ => false
            };
        }
    }
}