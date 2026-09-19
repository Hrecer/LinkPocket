using System;
using System.Windows.Input;
using LinkPocket.Input;

namespace LinkPocket.App.Tests;

/// <summary>
/// 键位总表测试用桩：任意动作 id 都返回同一个可辨识的命令（键位/作用域结构与命令实现无关）。
/// </summary>
public sealed class StubShortcutCommands : IShortcutCommands
{
    public static readonly StubShortcutCommands Instance = new();

    public ICommand Command(string actionId) => new ProbeCommand(actionId);

    public sealed class ProbeCommand : ICommand
    {
        public ProbeCommand(string actionId) => ActionId = actionId;

        public string ActionId { get; }

        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) { }
    }
}
