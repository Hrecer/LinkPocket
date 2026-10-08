namespace LinkPocket.Cli;

/// <summary>
/// 极简命令行解析：<c>--key value</c> / <c>--key=value</c> / <c>--flag</c> / <c>-f</c>。
/// 位置参数按原序保留，因此一条命令既可写成一段（<c>links.list</c>）也可写成两段（<c>links list</c>）。
/// </summary>
public sealed class CliArgs
{
    private readonly HashSet<string> _boolFlags;

    public List<string> Positional { get; } = new();
    public Dictionary<string, List<string>> Named { get; } = new(StringComparer.OrdinalIgnoreCase);

    private CliArgs(HashSet<string> boolFlags) => _boolFlags = boolFlags;

    public static CliArgs Parse(string[] args, IEnumerable<string> boolFlags)
    {
        var parsed = new CliArgs(new HashSet<string>(boolFlags, StringComparer.OrdinalIgnoreCase));
        for (var i = 0; i < args.Length; i++)
        {
            var token = args[i];
            if (token.StartsWith("--", StringComparison.Ordinal))
            {
                var body = token[2..];
                var equals = body.IndexOf('=');
                if (equals >= 0)
                {
                    parsed.Add(body[..equals], body[(equals + 1)..]);
                    continue;
                }

                // 已知开关、或后面没有值：按"无值开关"收下；否则吃掉下一个 token 当值。
                if (parsed._boolFlags.Contains(body)
                    || i + 1 >= args.Length
                    || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    parsed.Named.TryAdd(body, new List<string>());
                }
                else
                {
                    parsed.Add(body, args[++i]);
                }
                continue;
            }

            if (token.Length > 1 && token[0] == '-' && !char.IsDigit(token[1]))
            {
                parsed.Named.TryAdd(token[1..], new List<string>());
                continue;
            }

            parsed.Positional.Add(token);
        }
        return parsed;
    }

    private void Add(string name, string value)
    {
        if (!Named.TryGetValue(name, out var list)) Named[name] = list = new List<string>();
        list.Add(value);
    }

    public bool Flag(string name) => Named.ContainsKey(name);

    /// <summary>最后一个值（同名重复给出时取后者，与 shell 习惯一致）。</summary>
    public string? Value(string name) => Named.TryGetValue(name, out var list) && list.Count > 0 ? list[^1] : null;

    public IReadOnlyList<string> Values(string name) => Named.TryGetValue(name, out var list) ? list : Array.Empty<string>();
}
