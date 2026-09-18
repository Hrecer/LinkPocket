using System.Collections.Generic;

namespace LinkPocket.Input
{
    /// <summary>
    /// 快捷键作用域（可嵌套）：Global ⊃ Browser ⊃ { BrowserMain, BrowserTree }。
    /// 键在**最近的活跃作用域**解析（由内向外回退）：同一键可在不同栏绑不同动作
    /// （这正是"↑/↓ 两栏都能用、语义各自不同"的正解）；未在最内层命中时回退到外层。
    /// </summary>
    public enum ShortcutScope
    {
        /// <summary>全局（任何页面）：如 Ctrl+E / Ctrl+F 切搜索页。</summary>
        Global,

        /// <summary>浏览页整体（两栏通用）：如 Ctrl+X/C/V、Alt+←/→。</summary>
        Browser,

        /// <summary>浏览页主栏（内容列表）：如 ↑/↓ / End。</summary>
        BrowserMain,

        /// <summary>浏览页左栏（文件夹树）：如 ↑/↓ / ←/→。</summary>
        BrowserTree
    }

    /// <summary>作用域链（近 → 远）：解析顺序的唯一事实源，禁止在别处自行拼"回退逻辑"。</summary>
    public static class ShortcutScopes
    {
        private static readonly ShortcutScope[] MainChain =
            { ShortcutScope.BrowserMain, ShortcutScope.Browser, ShortcutScope.Global };

        private static readonly ShortcutScope[] TreeChain =
            { ShortcutScope.BrowserTree, ShortcutScope.Browser, ShortcutScope.Global };

        private static readonly ShortcutScope[] BrowserChain =
            { ShortcutScope.Browser, ShortcutScope.Global };

        private static readonly ShortcutScope[] GlobalChain =
            { ShortcutScope.Global };

        /// <summary>从活跃作用域向外回退的解析顺序（第一个命中的绑定胜出）。</summary>
        public static IReadOnlyList<ShortcutScope> Chain(ShortcutScope active) => active switch
        {
            ShortcutScope.BrowserMain => MainChain,
            ShortcutScope.BrowserTree => TreeChain,
            ShortcutScope.Browser => BrowserChain,
            _ => GlobalChain
        };
    }
}