using System.Text.Json;
using LinkPocket.Contracts;
using Xunit;

namespace LinkPocket.Ai.Tests;

/// <summary>会话存储与大结果存储：往返 / 倒序清单 / 损坏如实报错 / ID 形状守卫 / 删除连大结果。</summary>
public class AiStoreTests
{
    private static AiSessionFile NewFile(string sessionId)
    {
        var now = DateTimeOffset.UtcNow;
        return AiSessionFile.Create(new AiSessionSummary(sessionId, "t", AiMode.ConfirmEach, null, null, now, now, 0, 0, null));
    }

    [Fact]
    public void 会话存储_往返保持内容_且清单按最后活动倒序()
    {
        var root = AiTestEnv.NewRoot();
        try
        {
            var store = new AiSessionStore(root);
            var older = NewFile("s-aaa");
            older.Messages.Add(new AiMessage("m-1", 1, AiRole.User, "hello", DateTimeOffset.UtcNow, "t-1"));
            older.Chat.Add(new AiChatMessage("user", "hello"));
            store.Save(older);

            var newer = NewFile("s-bbb");
            newer.Summary = newer.Summary with { UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(5) };
            store.Save(newer);

            var list = store.List();
            Assert.Equal(["s-bbb", "s-aaa"], list.Select(s => s.SessionId).ToArray());

            var loaded = store.Load("s-aaa")!;
            Assert.Equal("hello", loaded.Messages[0].Text);
            Assert.Equal("hello", loaded.Chat[0].Text);
            Assert.False(File.Exists(store.StoreDirectory + "/s-aaa.json.tmp"));
        }
        finally { AiTestEnv.Drop(root); }
    }

    [Fact]
    public void 会话存储_损坏或版本不符如实报错_不覆盖()
    {
        var root = AiTestEnv.NewRoot();
        try
        {
            var store = new AiSessionStore(root);
            System.IO.Directory.CreateDirectory(store.StoreDirectory);
            var path = Path.Combine(store.StoreDirectory, "s-bad.json");
            const string broken = "{ not json";
            File.WriteAllText(path, broken);

            var load = Assert.Throws<AiException>(() => store.Load("s-bad"));
            Assert.Equal(AiErrors.SessionStoreFailed, load.Error.Code);
            Assert.Throws<AiException>(() => store.List());
            Assert.Equal(broken, File.ReadAllText(path));
        }
        finally { AiTestEnv.Drop(root); }
    }

    [Fact]
    public void 会话存储_拒绝非法会话ID_防路径穿越()
    {
        var root = AiTestEnv.NewRoot();
        try
        {
            var store = new AiSessionStore(root);
            foreach (var bad in new[] { "../escape", "a/b", "a\\b", "s-1.json", "中文" })
                Assert.Throws<AiException>(() => store.Load(bad));
        }
        finally { AiTestEnv.Drop(root); }
    }

    [Fact]
    public void 会话删除_同时清掉大结果目录()
    {
        var root = AiTestEnv.NewRoot();
        try
        {
            var store = new AiSessionStore(root);
            store.Save(NewFile("s-del"));
            var reference = store.Artifacts.Write("s-del", "c-1", "{}");
            Assert.Contains("s-del.data", reference, StringComparison.Ordinal);
            Assert.Equal("{}", store.Artifacts.Read("s-del", "c-1"));

            store.Delete("s-del");
            Assert.Null(store.Load("s-del"));
            Assert.Null(store.Artifacts.Read("s-del", "c-1"));
            store.Delete("s-del");   // 幂等
        }
        finally { AiTestEnv.Drop(root); }
    }

    [Fact]
    public void 大结果存储_超限落盘_引用可回读()
    {
        var root = AiTestEnv.NewRoot();
        try
        {
            var artifacts = new AiArtifactStore(root);
            var big = new string('x', AiArtifactStore.InlineLimitChars + 1);
            var reference = artifacts.Write("s-1", "c-9", big);

            Assert.Equal(big, artifacts.Read("s-1", "c-9"));
            Assert.Equal(Path.Combine("s-1.data", "c-9.json"), reference);
        }
        finally { AiTestEnv.Drop(root); }
    }

    [Fact]
    public void 草稿_不落盘不进列表_提升后才写文件()
    {
        var root = AiTestEnv.NewRoot();
        try
        {
            var store = new AiSessionStore(root);
            store.CreateDraft(NewFile("s-draft"));

            // 草稿：内存里能读到、标为 Deferred，但磁盘上什么都没有、列表为空。
            var draft = store.Load("s-draft")!;
            Assert.Equal(AiSessionPersistence.Deferred, draft.Summary.Persistence);
            Assert.True(store.IsDraft("s-draft"));
            Assert.Empty(store.List());
            Assert.False(Directory.Exists(store.StoreDirectory)
                && Directory.EnumerateFiles(store.StoreDirectory, "*.json").Any());

            // 草稿态下 Save 也不落盘（原地更新内存聚合）。
            draft.Messages.Add(new AiMessage("m-1", 1, AiRole.User, "hi", DateTimeOffset.UtcNow, "t-1"));
            store.Save(draft);
            Assert.Empty(store.List());

            // 提升：落盘 + 从草稿表移除，此后进列表、且标记为正式。
            store.Promote("s-draft");
            Assert.False(store.IsDraft("s-draft"));
            var promoted = Assert.Single(store.List());
            Assert.Equal("s-draft", promoted.SessionId);
            Assert.Equal(AiSessionPersistence.Immediate, promoted.Persistence);
            Assert.Single(store.Load("s-draft")!.Messages);   // 草稿期间写的消息一并落盘
        }
        finally { AiTestEnv.Drop(root); }
    }

    [Fact]
    public void 丢弃草稿_只删内存_正式会话不受影响()
    {
        var root = AiTestEnv.NewRoot();
        try
        {
            var store = new AiSessionStore(root);
            var real = NewFile("s-real");
            store.Save(real);            // 正式会话（落盘）

            store.CreateDraft(NewFile("s-draft"));
            store.DiscardDraft("s-draft");

            Assert.Null(store.Load("s-draft"));
            Assert.False(store.IsDraft("s-draft"));
            Assert.NotNull(store.Load("s-real"));            // 正式会话毫发无损
            Assert.Single(store.List());

            // 对正式会话调用 Promote / DiscardDraft 都是幂等 no-op（绝不误删真会话）。
            store.Promote("s-real");
            store.DiscardDraft("s-real");
            Assert.NotNull(store.Load("s-real"));
        }
        finally { AiTestEnv.Drop(root); }
    }
}
