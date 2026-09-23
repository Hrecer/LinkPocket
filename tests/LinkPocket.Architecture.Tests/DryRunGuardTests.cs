using System.Text.RegularExpressions;
using Xunit;

namespace LinkPocket.Architecture.Tests;

/// <summary>
/// 干跑隔离卡口：声明 <c>FileIo</c> / <c>NetworkOutsideGate</c> 的 **Mutation** 命令 = 副作用会越出数据库事务
/// （写文件 / 联网 / 落缓存），处理器必须检查 <c>ctx.DryRun</c>（只校验 + 如实预告影响面，**零副作用**）——
/// "先预演再执行"的承诺靠这张网兜底，不靠自觉（G6 缺口的收口；判据 = 去注释后的源码文本）。
/// </summary>
public class DryRunGuardTests
{
    /// <summary>豁免清单（写明理由）：干跑安全不来自本类自查的命令。</summary>
    private static readonly Dictionary<string, string> Exemptions = new(StringComparer.Ordinal)
    {
        ["staging.commit"] = "干跑经 DispatchNestedAsync 原样透传给目标命令（目标自检 DryRun）；本命令自身零副作用",
        ["bookmarks.import"] = "FileIo = 只读入参文件（File.Exists + 读取）；全部副作用在数据库事务内（干跑由引擎回滚），无落盘/联网面",
    };

    [Fact]
    public void 声明FileIo或网络能力的写命令_处理器必须检查DryRun()
    {
        var offenders = new List<string>();
        var seen = 0;
        foreach (var (command, className, file, body) in SideEffectCommands())
        {
            seen++;
            if (Exemptions.ContainsKey(command)) continue;
            if (!body.Contains("ctx.DryRun", StringComparison.Ordinal))
                offenders.Add($"{command}（{className} · {Path.GetFileName(file)}）");
        }

        Assert.True(seen >= 8, $"扫到的副作用写命令太少（{seen}）——扫描器需要跟着源码结构更新");
        Assert.True(offenders.Count == 0,
            "以下命令声明了 FileIo/NetworkOutsideGate 但处理器没有检查 ctx.DryRun（干跑必须零副作用）：\n"
            + string.Join("\n", offenders));
    }

    /// <summary>负向对照：判据必须真的能红（合成"声明 FileIo 但不检查 DryRun"的处理器要被抓住）。</summary>
    [Fact]
    public void 护栏判据_负向对照必须命中()
    {
        const string sample = """
            internal sealed class SyntheticExportHandler : ICommandHandler
            {
                public CommandDescriptor Descriptor { get; } = new(
                    Name: "synthetic.export", Category: "test", Description: "x",
                    Parameters: [], Caps: CommandCaps.Mutation | CommandCaps.FileIo);

                public Task<CommandResult> ExecuteAsync(ICommandContext ctx, JsonElement args)
                    => Task.FromResult(CommandResult.Ok(1));
            }
            """;
        var hit = ClassBlocks(sample)
            .Where(block => IsSideEffectMutation(block.Body))
            .ToList();
        var found = Assert.Single(hit);
        Assert.Equal("synthetic.export", CommandsIn(found.Body).Single());
        Assert.DoesNotContain("ctx.DryRun", found.Body, StringComparison.Ordinal);
    }

    // ===== 扫描实现（源码文本级；与其余源码扫描护栏同族） =====

    private static IEnumerable<(string Command, string ClassName, string File, string Body)> SideEffectCommands()
    {
        var srcDir = Path.Combine(RepoRoot, "src");
        foreach (var file in Directory.EnumerateFiles(srcDir, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                continue;
            foreach (var block in ClassBlocks(File.ReadAllText(file)))
            {
                if (!IsSideEffectMutation(block.Body)) continue;
                foreach (var command in CommandsIn(block.Body))
                    yield return (command, block.Name, file, block.Body);
            }
        }
    }

    /// <summary>声明为 Mutation 且带 FileIo / NetworkOutsideGate 能力 = 副作用越出事务（判据落在能力位上）。</summary>
    private static bool IsSideEffectMutation(string classBody)
        => classBody.Contains("CommandCaps.Mutation", StringComparison.Ordinal)
           && (classBody.Contains("FileIo", StringComparison.Ordinal)
               || classBody.Contains("NetworkOutsideGate", StringComparison.Ordinal));

    private static List<string> CommandsIn(string classBody)
    {
        var names = new List<string>();
        foreach (Match match in Regex.Matches(classBody, "Name:\\s*\"([a-z_]+\\.[a-z_]+)\"|new\\(\"([a-z_]+\\.[a-z_]+)\""))
            names.Add(match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value);
        return names.Distinct(StringComparer.Ordinal).ToList();
    }

    private static List<(string Name, string Body)> ClassBlocks(string text)
    {
        // 去行注释后按 class 声明切块（描述符是类成员——"块内含能力位与 DryRun 检查"即判据）
        var stripped = StripLineComments(text);
        var matches = Regex.Matches(stripped,
            @"(?m)^(?:(?:internal|public|private|protected|sealed|static|partial|abstract|file)\s+)*class\s+(\w+)");
        var blocks = new List<(string Name, string Body)>();
        for (var i = 0; i < matches.Count; i++)
        {
            var start = matches[i].Index;
            var end = i + 1 < matches.Count ? matches[i + 1].Index : stripped.Length;
            blocks.Add((matches[i].Groups[1].Value, stripped[start..end]));
        }
        return blocks;
    }

    private static string StripLineComments(string text)
        => Regex.Replace(text, @"//.*$", "", RegexOptions.Multiline);

    private static string RepoRoot
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "LinkPocket.sln")))
                dir = dir.Parent!;
            return dir!.FullName;
        }
    }
}
