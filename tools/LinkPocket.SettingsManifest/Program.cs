using System;
using System.IO;
using System.Text;
using System.Text.Json;
using LinkPocket.Ai;
using LinkPocket.Theming.Preferences;

// update\settings-manifest.json（更新器的"设置版本清单"）生成器。
//
// 用法：dotnet run --project tools/LinkPocket.SettingsManifest -- <输出文件>
// 由 pack.ps1 在打包时调用（见 UPDATE.md §8 发布物变更明细）。
//
// 单一事实源 = 各 Store 的 CurrentVersion 常量：清单在这里**现场读常量**生成，手写无处漂移。
// 覆盖面 = data\ 下随更新搬移、且常量可读的设置存储（ui-preferences + ai 四个 JSON 存储）。
// 不在清单内的设置（各有兜底，见 UPDATE.md §5.1 缺步策略）：
//   · macros.json —— 引擎内部常量不可达（FileMacroStore 是 internal）；
//   · ai\sessions\*.json —— 每份文件自带 version，由 AiSessionStore 的加载校验兜底；
//   · update-preferences.json —— 4.0.0 新文件，暂无跨格式风险。
//
// 清单语义（更新器侧实现 = src/LinkPocket.Update/SettingsChain.cs）：
// 版本对不上目标（含降级）→ 丢弃该文件（设置可重置，不阻塞更新）；无版本字段 → 原样携带并记录。

var outputPath = args.Length > 0 ? Path.GetFullPath(args[0]) : null;
if (outputPath is null)
{
    Console.Error.WriteLine("usage: linkpocket-settings-manifest <output file>");
    return 2;
}

// (data\ 内相对路径, 该存储当前的格式版本) —— 版本一律取自常量，不手写数字
var stores = new (string File, int Version)[]
{
    ("ui-preferences.json", UiPreferences.CurrentVersion),
    ("ai/preferences.json", AiPreferenceStore.CurrentVersion),
    ("ai/providers.json", AiProviderStore.CurrentVersion),
    ("ai/credentials.json", AiCredentialStore.CurrentVersion),
    ("ai/skills.json", AiSkillStore.CurrentVersion),
};

var manifest = new
{
    version = 1,
    stores = Array.ConvertAll(stores, s => new { file = s.File, version = s.Version }),
};

var directory = Path.GetDirectoryName(outputPath);
if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
File.WriteAllText(outputPath, JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }),
    new UTF8Encoding(false));
Console.WriteLine($"settings manifest -> {outputPath} ({stores.Length} stores)");
return 0;
