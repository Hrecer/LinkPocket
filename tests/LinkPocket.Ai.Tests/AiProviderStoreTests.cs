using LinkPocket.Contracts;
using Xunit;

namespace LinkPocket.Ai.Tests;

/// <summary>
/// 服务商配置存储：模板/覆盖层语义 / 草稿宽松与准入严格 / 模型合并与来源保留 /
/// 损坏拒绝覆盖（LP.AI.015）/ 单条凭据解不开只标记该服务商。
/// </summary>
public class AiProviderStoreTests
{
    private static AiCredentialStore Credentials(string root) => new(root, new AiTestEnv.FakeCipher());

    [Fact]
    public void 服务商目录_预设Id唯一_接入地址与申请入口齐备_且显示名走文案键()
    {
        var templates = AiProviderCatalog.Templates;
        Assert.Equal(21, templates.Count);
        Assert.Equal(templates.Count, templates.Select(t => t.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(templates.Count, templates.Select(t => t.DisplayNameKey).Distinct(StringComparer.Ordinal).Count());
        Assert.All(templates, t =>
        {
            Assert.Equal($"ai.provider.{t.Id}", t.DisplayNameKey);   // 键 = ai.provider.<id>（中文只在 StringTables）
            Assert.False(string.IsNullOrWhiteSpace(t.DisplayName));
            Assert.True(t.DisplayName.All(char.IsAscii), $"{t.Id} 的回退名必须是 ASCII：{t.DisplayName}");
            Assert.StartsWith("http", t.BaseUrl, StringComparison.Ordinal);
        });
        Assert.All(templates.Where(t => !t.IsLocal), t => Assert.NotNull(t.ApiKeyManagementUrl));
        Assert.All(templates, t => Assert.Equal(t.PresetModelIds.Count,
            t.PresetModelIds.Distinct(StringComparer.Ordinal).Count()));
    }

    [Fact]
    public void 服务商清单_默认投影给出全部预设_未配密钥时列为未配置并说明缺什么()
    {
        var root = AiTestEnv.NewRoot();
        try
        {
            var list = new AiProviderStore(root).List(Credentials(root));

            Assert.Equal(AiProviderCatalog.Templates.Count, list.Count);
            var openai = Assert.Single(list, p => p.Id == "openai");
            Assert.Equal(AiProviderSource.Preset, openai.Source);
            Assert.Equal("ai.provider.openai", openai.DisplayNameKey);
            Assert.False(openai.HasApiKey);
            Assert.Equal(AiProviderStatus.NotConfigured, openai.Status);
            Assert.Contains(openai.Issues!, i => i is { FieldPath: "api_key", Code: AiConfigIssueCodes.ApiKeyMissing });
            Assert.Contains(openai.Models, m => m is { Id: "gpt-6-astra", Enabled: true, Source: AiModelSource.Preset });

            var local = Assert.Single(list, p => p.Id == "local");
            Assert.True(local.IsLocal);
            Assert.DoesNotContain(local.Issues!, i => i.Code == AiConfigIssueCodes.ApiKeyMissing);
        }
        finally { AiTestEnv.Drop(root); }
    }

    [Fact]
    public void 服务商存储_配置完整时未验证_测试成功后已验证_测试失败为验证失败()
    {
        var root = AiTestEnv.NewRoot();
        try
        {
            var credentials = Credentials(root);
            var store = new AiProviderStore(root);
            credentials.Set("openai", "sk-1234567890abcdef");

            var configured = Assert.Single(store.List(credentials), p => p.Id == "openai");
            Assert.Equal(AiProviderStatus.Configured, configured.Status);
            Assert.True(configured.HasApiKey);
            Assert.Equal("sk-1…cdef", configured.ApiKeyMasked);
            Assert.Empty(configured.Issues!);

            Assert.Equal(AiProviderStatus.Verified, store.SetLastTestResult("openai", null, credentials).Status);

            var failed = store.SetLastTestResult("openai", AiErrors.AuthFailed, credentials);
            Assert.Equal(AiProviderStatus.Failed, failed.Status);
            Assert.Equal(AiErrors.AuthFailed, failed.StatusErrorCode);
            Assert.Equal(AiProviderStatus.Failed,
                Assert.Single(store.List(credentials), p => p.Id == "openai").Status);   // 重启后徽标仍在
        }
        finally { AiTestEnv.Drop(root); }
    }

    [Fact]
    public void 服务商存储_保存草稿允许半填_但准入问题如实列出()
    {
        var root = AiTestEnv.NewRoot();
        try
        {
            var credentials = Credentials(root);
            var info = new AiProviderStore(root).SaveProvider(
                new AiProviderDraft("my-gw", "自建网关", AiProtocol.OpenAiChat, "", Enabled: true, IsLocal: false),
                credentials);

            Assert.Equal(AiProviderSource.Custom, info.Source);
            Assert.Equal(AiProviderStatus.NotConfigured, info.Status);
            Assert.Contains(info.Issues!, i => i.Code == AiConfigIssueCodes.InvalidUrl);
            Assert.Contains(info.Issues!, i => i.Code == AiConfigIssueCodes.ApiKeyMissing);
            Assert.Contains(info.Issues!, i => i.Code == AiConfigIssueCodes.NoEnabledModel);
        }
        finally { AiTestEnv.Drop(root); }
    }

    [Fact]
    public void 服务商存储_覆盖预设后删除_回到出厂模板()
    {
        var root = AiTestEnv.NewRoot();
        try
        {
            var credentials = Credentials(root);
            var store = new AiProviderStore(root);
            store.SaveProvider(new AiProviderDraft("deepseek", "DeepSeek 代理", AiProtocol.OpenAiChat,
                "https://my-proxy.example.com/v1", Enabled: true, IsLocal: false), credentials);

            var overridden = Assert.Single(store.List(credentials), p => p.Id == "deepseek");
            Assert.Equal("https://my-proxy.example.com/v1", overridden.BaseUrl);
            Assert.Equal("DeepSeek 代理", overridden.DisplayName);
            Assert.Null(overridden.DisplayNameKey);   // 改过名 → 不再走文案键（显示用户数据）
            Assert.Equal("https://platform.deepseek.com/api_keys", overridden.ApiKeyManagementUrl);   // 模板元数据仍在

            store.DeleteProvider("deepseek");
            var factory = Assert.Single(store.List(credentials), p => p.Id == "deepseek");
            Assert.Equal("https://api.deepseek.com/anthropic", factory.BaseUrl);
            Assert.Equal("DeepSeek", factory.DisplayName);
            Assert.Equal("ai.provider.deepseek", factory.DisplayNameKey);   // 删除覆盖层 → 回到走键的出厂态
        }
        finally { AiTestEnv.Drop(root); }
    }

    [Fact]
    public void 服务商存储_空名字保存不视为改名_仍走文案键()
    {
        var root = AiTestEnv.NewRoot();
        try
        {
            var credentials = Credentials(root);
            var store = new AiProviderStore(root);
            store.SaveProvider(new AiProviderDraft("openai", "", AiProtocol.OpenAiChat,
                "https://api.openai.com/v1", Enabled: true, IsLocal: false), credentials);

            var info = Assert.Single(store.List(credentials), p => p.Id == "openai");
            Assert.Equal("ai.provider.openai", info.DisplayNameKey);
            Assert.Equal("OpenAI", info.DisplayName);
        }
        finally { AiTestEnv.Drop(root); }
    }

    [Fact]
    public void 服务商存储_自定义服务商可增删_未知服务商保存模型报LP_AI_001()
    {
        var root = AiTestEnv.NewRoot();
        try
        {
            var credentials = Credentials(root);
            var store = new AiProviderStore(root);
            store.SaveProvider(new AiProviderDraft("my-gw", "自建网关", AiProtocol.OpenAiChat,
                "https://gw.example.com/v1", Enabled: true, IsLocal: false), credentials);
            Assert.Contains(store.List(credentials), p => p is { Id: "my-gw", Source: AiProviderSource.Custom });
            Assert.Null(Assert.Single(store.List(credentials), p => p.Id == "my-gw").DisplayNameKey);   // 自建名 = 用户数据

            var ex = Assert.Throws<AiException>(() => store.SaveModel(
                new AiModelDraft("nope", "m", "m", true, null, null, true, true), credentials));
            Assert.Equal(AiErrors.ProviderNotConfigured, ex.Error.Code);

            store.DeleteProvider("my-gw");
            Assert.DoesNotContain(store.List(credentials), p => p.Id == "my-gw");
        }
        finally { AiTestEnv.Drop(root); }
    }

    [Fact]
    public void 服务商存储_合并拉取列表_新模型默认未启用且更新保留来源()
    {
        var root = AiTestEnv.NewRoot();
        try
        {
            var credentials = Credentials(root);
            var store = new AiProviderStore(root);

            var merged = store.MergeFetchedModels("openai", ["gpt-6-astra", "o3-mini"], credentials);
            var fetched = Assert.Single(merged.Models, m => m.Id == "o3-mini");
            Assert.False(fetched.Enabled);
            Assert.Equal(AiModelSource.Fetched, fetched.Source);
            Assert.Equal(AiModelSource.Preset, Assert.Single(merged.Models, m => m.Id == "gpt-6-astra").Source);

            var enabled = store.SaveModel(
                new AiModelDraft("openai", "o3-mini", "o3-mini", true, 200_000, null, true, true), credentials);
            var updated = Assert.Single(enabled.Models, m => m.Id == "o3-mini");
            Assert.True(updated.Enabled);
            Assert.Equal(AiModelSource.Fetched, updated.Source);
            Assert.Equal(200_000, updated.ContextWindow);
        }
        finally { AiTestEnv.Drop(root); }
    }

    [Fact]
    public void 服务商存储_文件损坏时拒绝覆盖并报LP_AI_015()
    {
        const string broken = "{ not json";
        var root = AiTestEnv.NewRoot();
        try
        {
            var credentials = Credentials(root);
            var store = new AiProviderStore(root);
            File.WriteAllText(store.FilePath, broken);

            var read = Assert.Throws<AiException>(() => store.List(credentials));
            Assert.Equal(AiErrors.AiDataStoreFailed, read.Error.Code);

            var write = Assert.Throws<AiException>(() => store.SaveProvider(
                new AiProviderDraft("x", "X", AiProtocol.OpenAiChat, "https://x.example.com/v1", true, false),
                credentials));
            Assert.Equal(AiErrors.AiDataStoreFailed, write.Error.Code);
            Assert.Equal(broken, File.ReadAllText(store.FilePath));
        }
        finally { AiTestEnv.Drop(root); }
    }

    [Fact]
    public void 服务商存储_版本不符视为损坏()
    {
        var root = AiTestEnv.NewRoot();
        try
        {
            var credentials = Credentials(root);
            var store = new AiProviderStore(root);
            File.WriteAllText(store.FilePath, "{\"Version\":9,\"Providers\":[]}");

            var ex = Assert.Throws<AiException>(() => store.List(credentials));
            Assert.Equal(AiErrors.AiDataStoreFailed, ex.Error.Code);
        }
        finally { AiTestEnv.Drop(root); }
    }

    [Fact]
    public void 服务商存储_单条凭据解不开时只标记该服务商失败_不拖垮整个清单()
    {
        var root = AiTestEnv.NewRoot();
        try
        {
            new AiCredentialStore(root, new AiTestEnv.FakeCipher()).Set("openai", "sk-1234567890abcdef");
            var store = new AiProviderStore(root);

            var list = store.List(new AiCredentialStore(root, new AiTestEnv.FailingUnprotectCipher()));
            Assert.Equal(AiProviderCatalog.Templates.Count, list.Count);   // 清单照常给出

            var openai = Assert.Single(list, p => p.Id == "openai");
            Assert.True(openai.HasApiKey);                                  // 文件里有（如实）
            Assert.Null(openai.ApiKeyMasked);
            Assert.Equal(AiProviderStatus.Failed, openai.Status);
            Assert.Equal(AiErrors.CredentialStoreFailed, openai.StatusErrorCode);
            Assert.DoesNotContain(openai.Issues!, i => i.Code == AiConfigIssueCodes.ApiKeyMissing);
        }
        finally { AiTestEnv.Drop(root); }
    }
}
