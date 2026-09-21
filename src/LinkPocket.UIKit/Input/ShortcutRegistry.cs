using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;

namespace LinkPocket.Input
{
    /// <summary>
    /// 键位注册表（键位唯一事实源）：注册 + **同作用域重复键冲突检测** + 解析。
    /// 重复注册同一手势（同作用域 + 同键 + 同修饰键）说明键位表里两条声明抢一个键 ——
    /// **装配即抛**（启动就暴露，绝不静默"后者覆盖前者"）。
    /// 解析 = 按活跃作用域由内向外回退（见 <see cref="ShortcutScopes.Chain"/>），最近者胜。
    /// </summary>
    public sealed class ShortcutRegistry
    {
        private readonly List<ShortcutBinding> _bindings = new();

        public IReadOnlyList<ShortcutBinding> Bindings => _bindings;

        public void Register(ShortcutBinding binding)
        {
            var conflict = _bindings.FirstOrDefault(b =>
                b.Scope == binding.Scope && b.Key == binding.Key && b.Modifiers == binding.Modifiers);
            if (conflict != null)
                throw new InvalidOperationException(
                    $"快捷键冲突：[{binding.Scope}] {binding.GestureText} 被重复注册" +
                    $"shortcut conflict on {binding.Key}+{binding.Modifiers} (existing: {conflict.DescriptionKey}; incoming: {binding.DescriptionKey})");
            _bindings.Add(binding);
        }

        public void Register(IEnumerable<ShortcutBinding> bindings)
        {
            foreach (var b in bindings) Register(b);
        }

        /// <summary>按"活跃作用域 → 外"解析命中的绑定（未命中返回 null）。</summary>
        public ShortcutBinding? Resolve(ShortcutScope active, Key key, ModifierKeys modifiers)
        {
            foreach (var scope in ShortcutScopes.Chain(active))
            {
                foreach (var b in _bindings)
                    if (b.Scope == scope && b.Key == key && b.Modifiers == modifiers)
                        return b;
            }
            return null;
        }
    }
}