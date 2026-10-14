using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LinkPocket.Contracts;

namespace LinkPocket.Update;

/// <summary>
/// 更新器的运行痕迹：`update.log`（时序行，实时追加——崩了也知道走到哪一步）+
/// `update-result.json`（终局摘要，供新版首启的更新页展示）。
/// </summary>
public sealed class UpdateReport(string logPath)
{
    public string Mode { get; set; } = "unknown";
    public string? FromVersion { get; set; }
    public string? ToVersion { get; set; }
    public bool Ok { get; private set; }
    public string? Error { get; private set; }
    public List<string> Notes { get; } = [];
    public Dictionary<string, long> Counts { get; } = [];

    public void Stage(string line)
    {
        try
        {
            File.AppendAllText(logPath, $"{DateTime.Now:HH:mm:ss.fff}  {line}{Environment.NewLine}", new UTF8Encoding(false));
        }
        catch
        {
            // 日志写不进去绝不阻断更新本身
        }
    }

    public void Complete() => Ok = true;

    public void Fail(string message)
    {
        Ok = false;
        Error = message;
    }

    public void WriteResultJson(string path)
    {
        try
        {
            var payload = new ResultPayload
            {
                Ok = Ok,
                Mode = Mode,
                FromVersion = FromVersion,
                ToVersion = ToVersion,
                Error = Error,
                FinishedAt = DateTime.UtcNow.ToString("o"),
                Counts = Counts.Count > 0 ? Counts : null,
                Notes = Notes.Count > 0 ? Notes : null,
            };
            AtomicFile.WriteAllText(path, JsonSerializer.Serialize(payload, ResultJson));
        }
        catch
        {
            // 结果落盘失败不改变退出码语义；控制台输出已经说过发生了什么
        }
    }

    private static readonly JsonSerializerOptions ResultJson = new() { WriteIndented = true };

    private sealed class ResultPayload
    {
        [JsonPropertyName("ok")] public bool Ok { get; set; }
        [JsonPropertyName("mode")] public string Mode { get; set; } = "unknown";
        [JsonPropertyName("from_version")] public string? FromVersion { get; set; }
        [JsonPropertyName("to_version")] public string? ToVersion { get; set; }
        [JsonPropertyName("error")] public string? Error { get; set; }
        [JsonPropertyName("finished_at")] public string FinishedAt { get; set; } = "";
        [JsonPropertyName("counts")] public Dictionary<string, long>? Counts { get; set; }
        [JsonPropertyName("notes")] public List<string>? Notes { get; set; }
    }
}
