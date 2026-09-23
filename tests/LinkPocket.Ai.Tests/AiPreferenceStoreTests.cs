using LinkPocket.Contracts;
using Xunit;

namespace LinkPocket.Ai.Tests;

/// <summary>助手偏好存储：出厂缺省 / 往返 / 加字段不升版本 / 版本不符与越界如实报错且拒绝覆盖。</summary>
public class AiPreferenceStoreTests
{
    [Fact]
    public void 偏好存储_缺文件时给出出厂缺省()
    {
        var root = AiTestEnv.NewRoot();
        try
        {
            var preferences = new AiPreferenceStore(root).Load();

            Assert.Null(preferences.ProviderId);
            Assert.Equal(AiMode.ConfirmEach, preferences.Mode);          // 缺省模式 = 每次确认（已拍板决策）
            Assert.Equal(200, preferences.MaxChangesPerTurn);
            Assert.Equal(500, preferences.MaxBatchSteps);
            Assert.False(preferences.AdvancedToolsEnabled);
        }
        finally { AiTestEnv.Drop(root); }
    }

    [Fact]
    public void 偏好存储_往返保持字段_且旧文件缺字段取缺省不升版本()
    {
        var root = AiTestEnv.NewRoot();
        try
        {
            var store = new AiPreferenceStore(root);
            store.Save(new AiPreferences("openai", "gpt-4o", AiMode.AutoApply, 10, 50, 20, 60, 64_000, true));

            var loaded = store.Load();
            Assert.Equal("openai", loaded.ProviderId);
            Assert.Equal(AiMode.AutoApply, loaded.Mode);
            Assert.Equal(60, loaded.CallsPerMinute);
            Assert.True(loaded.AdvancedToolsEnabled);
            Assert.False(File.Exists(store.FilePath + ".tmp"));

            File.WriteAllText(store.FilePath, "{\"Version\":1,\"Preferences\":{\"Mode\":2}}");
            var partial = store.Load();
            Assert.Equal(AiMode.AutoApply, partial.Mode);
            Assert.Equal(200, partial.MaxChangesPerTurn);
            Assert.Null(partial.ProviderId);
        }
        finally { AiTestEnv.Drop(root); }
    }

    [Fact]
    public void 偏好存储_版本不符或越界都报LP_AI_015且拒绝覆盖()
    {
        const string mismatched = "{\"Version\":9,\"Preferences\":{}}";
        var root = AiTestEnv.NewRoot();
        try
        {
            var store = new AiPreferenceStore(root);
            File.WriteAllText(store.FilePath, mismatched);

            var load = Assert.Throws<AiException>(() => store.Load());
            Assert.Equal(AiErrors.AiDataStoreFailed, load.Error.Code);

            var save = Assert.Throws<AiException>(() => store.Save(new AiPreferences()));
            Assert.Equal(AiErrors.AiDataStoreFailed, save.Error.Code);
            Assert.Equal(mismatched, File.ReadAllText(store.FilePath));

            File.WriteAllText(store.FilePath, "{\"Version\":1,\"Preferences\":{\"MaxChangesPerTurn\":0}}");
            var range = Assert.Throws<AiException>(() => store.Load());
            Assert.Equal(AiErrors.AiDataStoreFailed, range.Error.Code);
            Assert.Equal("max_changes_per_turn",
                range.Error.Details!.Value.GetProperty("field").GetString());
        }
        finally { AiTestEnv.Drop(root); }
    }
}
