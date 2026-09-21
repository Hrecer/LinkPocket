using System.Collections.Generic;

namespace LinkPocket.Input
{
    /// <summary>
    /// 快捷键作用域（可嵌套；**一页一组、组间不互通**）：
    /// <code>
    /// Global ⊃ Browser ⊃ { BrowserMain, BrowserTree }
    ///                Trash   ⊃ { TrashMain, TrashTree }
    ///                Search · SmartLists · Tools · Settings（各自独立、平级）
    /// </code>
    /// 键在**最近的活跃作用域**解析（由内向外回退）：同一键可在不同栏/不同页绑不同动作
    /// （这正是"↑/↓ 两栏都能用、语义各自不同"的正解）；未在最内层命中时回退到外层。
    ///
    /// ⚠️ 隔离硬约束：
    /// ① 每个页面的作用域链**只包含自己的组 + 自己的栏**——页面之间既不共享也不互相继承；
    /// ② 只有浏览页注册 <see cref="Global"/>（Ctrl+E/F 切搜索页），其余页面的链不含 Global；
    /// ③ 键位声明一律写在 <see cref="ShortcutCatalog"/>，页面不得自行拼链条或注册绑定。
    /// </summary>
    public enum ShortcutScope
    {
        /// <summary>全局（任何页面）：如 Ctrl+E / Ctrl+F 切搜索页（当前仅浏览页注册）。</summary>
        Global,

        /// <summary>浏览页整体（两栏通用）：如 Ctrl+X/C/V、Alt+←/→。</summary>
        Browser,

        /// <summary>浏览页主栏（内容列表）：如 ↑/↓ / End。</summary>
        BrowserMain,

        /// <summary>浏览页左栏（文件夹树）：如 ↑/↓ / ←/→。</summary>
        BrowserTree,

        /// <summary>回收站整体（两栏通用）：如 Backspace/Alt+←/→、F5、Delete（永久删除）。</summary>
        Trash,

        /// <summary>回收站主栏（内容列表）：如 ↑/↓ / End。</summary>
        TrashMain,

        /// <summary>回收站左栏（被删单元树）：如 ↑/↓ / ←/→。</summary>
        TrashTree,

        /// <summary>搜索页：Enter 执行搜索（控件锚定在搜索框上）。</summary>
        Search,

        /// <summary>智能列表页：Esc 从结果页返回卡片列表。</summary>
        SmartLists,

        /// <summary>工具页：Enter 执行 ID 跳转（控件锚定在 ID 输入框上）。</summary>
        Tools,

        /// <summary>设置页（当前无页面级快捷键；占位以保持"每页一组"的对称）。</summary>
        Settings,
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

        private static readonly ShortcutScope[] TrashChain =
            { ShortcutScope.Trash };

        private static readonly ShortcutScope[] TrashMainChain =
            { ShortcutScope.TrashMain, ShortcutScope.Trash };

        private static readonly ShortcutScope[] TrashTreeChain =
            { ShortcutScope.TrashTree, ShortcutScope.Trash };

        private static readonly ShortcutScope[] GlobalChain =
            { ShortcutScope.Global };

        // —— 独立页面：各只有自己一层（不继承任何其它页面的作用域，也不含 Global）——
        private static readonly ShortcutScope[] SearchChain = { ShortcutScope.Search };
        private static readonly ShortcutScope[] SmartListsChain = { ShortcutScope.SmartLists };
        private static readonly ShortcutScope[] ToolsChain = { ShortcutScope.Tools };
        private static readonly ShortcutScope[] SettingsChain = { ShortcutScope.Settings };

        /// <summary>从活跃作用域向外回退的解析顺序（第一个命中的绑定胜出）。</summary>
        public static IReadOnlyList<ShortcutScope> Chain(ShortcutScope active) => active switch
        {
            ShortcutScope.BrowserMain => MainChain,
            ShortcutScope.BrowserTree => TreeChain,
            ShortcutScope.Browser => BrowserChain,
            ShortcutScope.TrashMain => TrashMainChain,
            ShortcutScope.TrashTree => TrashTreeChain,
            ShortcutScope.Trash => TrashChain,
            ShortcutScope.Search => SearchChain,
            ShortcutScope.SmartLists => SmartListsChain,
            ShortcutScope.Tools => ToolsChain,
            ShortcutScope.Settings => SettingsChain,
            _ => GlobalChain
        };
    }
}
