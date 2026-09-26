using System.Text.Json;
using LinkPocket.Contracts;
using LinkPocket.Engine;
using Xunit;

namespace LinkPocket.Engine.Tests;

/// <summary>
/// 撤销日志**三档落盘**的回归网（2026-09-26）：
/// ① 压缩 + 后台写分片；② 体量上限与启动自愈；③ 摘要常驻 + 步骤按需水合。
/// </summary>
/// <remarks>
/// 为什么要有这一套：原实现每次撤销栈变动都**整份序列化并重写**、启动**整份读+反序列化** ——
/// 一次批量脚本（改 1430+ 书签）留下单条 3544 步的条目 ⇒ 日志涨到 456MB，用户实测"切页/开目录卡 1-2 秒"。
/// 下面每条用例都钉住"代价与条目有多大脱钩"的一个面。
/// </remarks>
public class UndoJournalV2Tests
{
    private static readonly CommandDescriptor Descriptor =
        new("test.x", "test", "d", [], CommandCaps.Mutation, UndoInverse: "test.y");

    private static JsonElement Args() => JsonSerializer.Deserialize<JsonElement>("{}");

    [Fact]
    public async Task 大条目_步骤转独立分片_重启后按需水合()
    {
        var path = Temp();
        try
        {
            var a = new UndoCoordinator(path);
            // 同组 300 次登记 → 合并成 1 条 300 步（> 内联上限 50 ⇒ 该走分片）
            for (var i = 0; i < 300; i++) a.Record(Descriptor, Args(), CallerRef.Test, groupId: "g1");
            var entry = Assert.Single(await a.ListAsync(default));
            Assert.Equal(300, entry.Steps.Count);

            // 分片由**后台线程**写；索引只在分片就绪后才引用它（绝不写半截）。
            // ⚠️ 分组条目会长大 ⇒ 后台会**反复重写**同一份分片（每次步数变了都要重写），
            // 所以这里必须等它"**写完最后那一版**"（落盘静默 600ms），否则拿到的是早先那份半截快照。
            await WaitFor(() => Directory.Exists(path + ".parts")
                                && Directory.GetFiles(path + ".parts", "*.json.gz").Length == 1);
            await WaitForSettled(path);

            // ① 索引是 gzip 且很小（不含步骤正文 = 原来那 456MB 的元凶）
            using (var fs = File.OpenRead(path)) Assert.Equal(0x1F, fs.ReadByte());
            Assert.True(new FileInfo(path).Length < 4096, $"索引应很小，实为 {new FileInfo(path).Length} 字节");

            // ③ 重启：只读索引 ⇒ 大条目的步骤**不进内存**，但展示摘要还在
            var b = new UndoCoordinator(path);
            var loaded = Assert.Single(await b.ListAsync(default));
            Assert.Empty(loaded.Steps);
            Assert.Equal("test.x", loaded.Command);   // 来自 SummaryCommand（未水合也能显示）

            // ③ 撤销到它时才水合：步骤逐字段回来（可执行）
            var taken = await b.TakeUndoAsync(null, default);
            Assert.NotNull(taken);
            Assert.Equal(300, taken!.Steps.Count);
            Assert.All(taken.Steps, s => Assert.Equal("test.y", s.InverseCommand));
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public async Task 小组目_仍随索引内联_不产生分片()
    {
        var path = Temp();
        try
        {
            var a = new UndoCoordinator(path);
            for (var i = 0; i < 3; i++) a.Record(Descriptor, Args(), CallerRef.Test);
            await Task.Delay(200);

            var parts = path + ".parts";
            Assert.True(!Directory.Exists(parts) || Directory.GetFiles(parts, "*.json.gz").Length == 0,
                "小条目不该产生分片（索引内联即可；该目录只允许放索引的临时文件）");
            var b = new UndoCoordinator(path);   // 重启后步骤直接在内存里
            var loaded = await b.ListAsync(default);
            Assert.Equal(3, loaded.Count);
            Assert.All(loaded, e => Assert.NotEmpty(e.Steps));
            Assert.Equal(3, loaded.Count);
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public async Task 旧格式巨型日志_启动即丢弃_空栈起步不拖慢()
    {
        var path = Temp();
        try
        {
            // 造一份"旧格式且超大"的日志（70MB 非 gzip 文件；判定只看体量与魔数，内容不参与）
            await using (var fs = File.Create(path)) fs.SetLength(70L * 1024 * 1024);

            var a = new UndoCoordinator(path);   // 不抛、空栈起步（= 既有的"存档不可用"语义）
            Assert.Empty(await a.ListAsync(default));
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public async Task 超单条目硬上限_本次会话可撤销_但不入日志()
    {
        var path = Temp();
        try
        {
            var a = new UndoCoordinator(path);
            for (var i = 0; i < 5100; i++) a.Record(Descriptor, Args(), CallerRef.Test, groupId: "big");
            var entry = Assert.Single(await a.ListAsync(default));
            Assert.Equal(5100, entry.Steps.Count);   // 内存里完整 ⇒ 本会话可撤销
            await Task.Delay(300);

            var b = new UndoCoordinator(path);       // 重启后不再有这条（体量硬上限的代价）
            Assert.Empty(await b.ListAsync(default));
        }
        finally { Cleanup(path); }
    }

    private static string Temp() => Path.Combine(Path.GetTempPath(), $"lp-undo-v2-{Guid.NewGuid():N}.json");

    /// <summary>等落盘静默：索引与所有分片的长度/修改时间连续 3 次采样不变（= 后台写完了最后那一版）。</summary>
    private static async Task WaitForSettled(string path)
    {
        string Fingerprint()
        {
            var parts = path + ".parts";
            var files = Directory.Exists(parts) ? Directory.GetFiles(parts, "*.json.gz") : [];
            return string.Join("|", new[] { path }.Concat(files).Select(f =>
            {
                var info = new FileInfo(f);
                return $"{info.Length}@{info.LastWriteTimeUtc.Ticks}";
            }));
        }

        var last = Fingerprint();
        var stable = 0;
        for (var i = 0; i < 200 && stable < 3; i++)
        {
            await Task.Delay(200);
            var now = Fingerprint();
            stable = now == last ? stable + 1 : 0;
            last = now;
        }
        Assert.True(stable >= 3, "等待超时：落盘未在 ~40 秒内静默（后台分片一直在重写？）");
    }

    private static async Task WaitFor(Func<bool> ok)
    {
        for (var i = 0; i < 100 && !ok(); i++) await Task.Delay(50);
        Assert.True(ok(), "等待超时：分片未在 ~5 秒内就绪");
    }

    private static void Cleanup(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* 测试清理尽力而为 */ }
        try { if (Directory.Exists(path + ".parts")) Directory.Delete(path + ".parts", true); } catch { }
        // 临时文件也不许留（同目录下的备份测试会扫 `*.tmp-*` 判"目录里没有残留"——
        // 我曾因一份陈旧残留把那条用例判红；本文件的用例必须自己扫干净）
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                foreach (var stray in Directory.GetFiles(dir, Path.GetFileName(path) + ".tmp-*"))
                    File.Delete(stray);
        }
        catch { /* 同上 */ }
    }
}
