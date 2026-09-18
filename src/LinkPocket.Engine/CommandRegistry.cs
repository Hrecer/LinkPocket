using LinkPocket.Contracts;
using LinkPocket.Kernel.Commands;

namespace LinkPocket.Engine;

/// <summary>
/// 命令注册表：模块经 RegisterCommands 注册处理器；命令名 = 域.动作 全小写下划线。
/// 注册即自描述——目录/文档/测试骨架全部由 Descriptor 机械生成（单一事实源）。
/// </summary>
public sealed class CommandRegistry
{
    private readonly Dictionary<string, ICommandHandler> _handlers = new(StringComparer.Ordinal);

    /// <summary>注册处理器；命令名冲突即抛（启动期失败优于运行期歧义）。</summary>
    public void Register(ICommandHandler handler)
    {
        var name = handler.Descriptor.Name;
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("命令名不能为空", nameof(handler));
        if (_handlers.ContainsKey(name))
            throw new InvalidOperationException($"命令「{name}」重复注册");
        _handlers[name] = handler;
    }

    public void RegisterAll(IEnumerable<ICommandHandler> handlers)
    {
        foreach (var h in handlers) Register(h);
    }

    public ICommandHandler? Resolve(string command)
        => _handlers.TryGetValue(command, out var handler) ? handler : null;

    /// <summary>按类别取全部描述符（category = null 时取全部），按名称排序保证目录稳定。</summary>
    public IReadOnlyList<CommandDescriptor> Describe(string? category)
        => _handlers.Values
            .Select(h => h.Descriptor)
            .Where(d => category == null || string.Equals(d.Category, category, StringComparison.Ordinal))
            .OrderBy(d => d.Name, StringComparer.Ordinal)
            .ToList();
}
