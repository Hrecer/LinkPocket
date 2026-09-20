using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using LinkPocket.Contracts;
using LinkPocket.Theming.Themes;

namespace LinkPocket.Theming.Preferences;

/// <summary>用户界面偏好（主题 + 字体）。落 <c>{BaseDirectory}/ui-preferences.json</c>。</summary>
/// <remarks>
/// <para>
/// <b>为什么不进数据库</b>（方案 §6.3）：偏好不是书库内容——不进备份文件、不进 schema、
/// 不加引擎命令（因此 catalog 不需要重生成）。它与 db / favicons / logs 同目录，
/// 遵守"绝不清理 bin"的既有纪律。
/// </para>
/// <para>
/// <b>版本字段</b>：格式演进时用来判定"能不能读"。版本不符 → 当作损坏处理（回退默认 + 如实暴露），
/// 不做兼容迁移（本仓零兼容红线）。
/// </para>
/// </remarks>
public sealed record UiPreferences
{
    /// <summary>当前偏好文件格式版本。</summary>
    public const int CurrentVersion = 1;

    /// <summary>格式版本。</summary>
    public int Version { get; init; } = CurrentVersion;

    /// <summary>主题选择（出厂默认 / 预设 id / 用户自选）。</summary>
    public ThemePreference Theme { get; init; } = new();

    /// <summary>字体选择。</summary>
    public FontPreference Fonts { get; init; } = new();

    /// <summary>默认偏好（= 出厂默认主题 + 默认字体）。</summary>
    public static UiPreferences Default { get; } = new();
}

/// <summary>主题偏好：内置 id 或用户自选配色。</summary>
public sealed record ThemePreference
{
    /// <summary>内置主题 id（出厂默认或预设）；用户自选时为 <c>null</c>。</summary>
    public string? Id { get; init; }

    /// <summary>用户自选的 4–5 个颜色（<c>#RRGGBB</c>）；内置主题时为 null。</summary>
    public IReadOnlyList<string>? Colors { get; init; }

    /// <summary>用户自选时钉住的中性色相（可空 = 按配色派生）。</summary>
    public double? NeutralHue { get; init; }

    /// <summary>是否使用用户自选配色。</summary>
    [JsonIgnore]
    public bool IsCustom => Colors is { Count: > 0 };
}

/// <summary>字体偏好：界面字体与等宽字体各一个族名（可为导入字体的族名）。</summary>
public sealed record FontPreference
{
    /// <summary>界面字体族名（缺省 = 系统雅黑链）。</summary>
    public string Ui { get; init; } = Fonts.FontLoader.DefaultUiFamily;

    /// <summary>等宽字体族名（缺省 = Consolas 链）。</summary>
    public string Mono { get; init; } = Fonts.FontLoader.DefaultMonoFamily;
}

/// <summary>
/// 偏好的**唯一持久化入口**：原子写 + 损坏如实暴露 + 缺失即默认。
/// </summary>
/// <remarks>
/// <para>
/// <b>原子写</b>：先写 <c>.tmp</c> 再 <c>File.Replace</c>（同目录同卷，替换是原子的）。
/// 直接覆写原文件在崩溃/断电时会留下半截 JSON —— 下次启动就"偏好丢失且报损坏"。
/// </para>
/// <para>
/// <b>失败必须暴露</b>（观测面纪律）：文件损坏 / 版本不符 / 读取异常 → 记日志并**回退默认**，
/// 由调用方向用户提示一次"已回退默认外观"。绝不静默兜底、也绝不静默崩。
/// </para>
/// </remarks>
public static class UiPreferenceStore
{
    private const string LogCategory = "app.theme";
    private const string FileName = "ui-preferences.json";

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>偏好文件路径（与 db / logs 同目录）。</summary>
    public static string FilePath => Path.Combine(AppContext.BaseDirectory, FileName);

    /// <summary>读取偏好；文件不存在 = 默认值；损坏 = 默认值 + 记日志（<paramref name="failed"/> 为 true）。</summary>
    /// <param name="failed">true = 读取失败（已回退默认），调用方应提示用户一次。</param>
    public static UiPreferences Load(out bool failed)
    {
        failed = false;
        var path = FilePath;
        if (!File.Exists(path)) return UiPreferences.Default;

        try
        {
            var text = File.ReadAllText(path);
            var prefs = JsonSerializer.Deserialize<UiPreferences>(text, Json);
            if (prefs is null)
            {
                LpLog.Error($"界面偏好文件内容为空，已回退默认外观：{path}", category: LogCategory);
                failed = true;
                return UiPreferences.Default;
            }
            if (prefs.Version != UiPreferences.CurrentVersion)
            {
                LpLog.Error($"界面偏好文件版本不符（期望 {UiPreferences.CurrentVersion}，实际 {prefs.Version}），已回退默认外观：{path}", category: LogCategory);
                failed = true;
                return UiPreferences.Default;
            }
            return prefs;
        }
        catch (Exception ex)
        {
            LpLog.Error($"界面偏好文件损坏，已回退默认外观：{path}", ex, LogCategory);
            failed = true;
            return UiPreferences.Default;
        }
    }

    /// <summary>保存偏好（原子替换）。失败**抛出**——写不进去必须让调用方知道。</summary>
    public static void Save(UiPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        var path = FilePath;
        var tmp = path + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(tmp, JsonSerializer.Serialize(preferences, Json));

            if (File.Exists(path))
                File.Replace(tmp, path, destinationBackupFileName: null);
            else
                File.Move(tmp, path);
        }
        catch (Exception ex)
        {
            LpLog.Error($"保存界面偏好失败：{path}", ex, LogCategory);
            TryDelete(tmp);
            throw;
        }
    }

    /// <summary>删除偏好文件（回到默认外观）。文件不存在视为成功。</summary>
    public static void Clear()
    {
        TryDelete(FilePath);
        TryDelete(FilePath + ".tmp");
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            // 删除失败不阻断（下次启动仍会读到旧偏好），但如实留痕
            LpLog.Warn($"删除界面偏好文件失败：{path}（{ex.Message}）", category: LogCategory);
        }
    }
}
