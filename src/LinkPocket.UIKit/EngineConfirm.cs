using System.Text.Json;
using LinkPocket.Contracts;

namespace LinkPocket.Services;

/// <summary>
/// 破坏性命令的两阶段确认编排（引擎能力门 LP.SEC.003 的 UI 侧配合）：
/// 首次无令牌调用 → 引擎抛 <see cref="EngineErrors.ConfirmRequired"/>（Details 附
/// confirm_token + ttl）→ 调用方完成 UI 确认后，本组件用该令牌重发同一命令。
/// 职责边界：本组件只管「拿令牌 → 带令牌重试」，UI 弹窗（ConfirmDialog / 输入确认文字）
/// 由调用方负责——不吞异常、不擅自弹窗、令牌取不到就原样把错误抛给上层（观测面纪律）。
/// </summary>
public static class EngineConfirm
{
    /// <summary>
    /// 执行破坏性命令并完成两阶段确认。
    /// <paramref name="invoke"/> 接受「确认令牌」，无令牌为 null：首次调用即拿不到令牌
    /// （非破坏性路径一轮成功；破坏性路径抛 ConfirmRequired）。带令牌重发由本组件内部完成。
    /// </summary>
    public static async Task<T> RunAsync<T>(Func<string?, Task<T>> invoke)
    {
        try
        {
            return await invoke(null);
        }
        catch (EngineException ex) when (ex.Error.Code == EngineErrors.ConfirmRequired)
        {
            var token = ExtractToken(ex);
            return await invoke(token);
        }
    }

    /// <summary>从 LP.SEC.003 的 Details 里取 confirm_token；取不到 = 不静默兜底，原样抛回。</summary>
    private static string ExtractToken(EngineException ex)
    {
        if (ex.Error.Details is { } details
            && details.ValueKind == JsonValueKind.Object
            && details.TryGetProperty("confirm_token", out var token)
            && token.ValueKind == JsonValueKind.String)
        {
            return token.GetString()!;
        }

        throw ex;   // Observability：没有令牌可重试，把原始引擎错误交给调用方展示
    }
}