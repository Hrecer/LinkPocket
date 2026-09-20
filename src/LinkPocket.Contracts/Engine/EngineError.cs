using System.Text.Json;

namespace LinkPocket.Contracts;

/// <summary>
/// 统一错误模型：机器可读稳定码 + 中文可读消息 + 结构化细节 + 可重试标志 + 关联 ID。
/// 校验类错误（LP.VAL.*）保证零副作用——参数在进写闸前全量校验完毕。
/// </summary>
public sealed record EngineError(
    string Code,
    string Message,
    JsonElement? Details,
    bool Retryable,
    string CorrelationId)
{
    public override string ToString()
        => $"{Code}: {Message}（correlation_id={CorrelationId}）";   // 日志直接 ToString 也能关联到调用
}

/// <summary>引擎调用失败异常：携带 <see cref="EngineError"/>，由管道在审计后抛出（Execute 捕获转 wire error，Query 直接上抛）。</summary>
public sealed class EngineException : Exception
{
    public EngineError Error { get; }

    public EngineException(EngineError error) : base(error.Message) => Error = error;
}

/// <summary>错误码表（稳定不变；工厂方法统一补齐 CorrelationId 与 Details）。</summary>
public static class EngineErrors
{
    public const string RequiredParam = "LP.VAL.001";
    public const string TypeMismatch = "LP.VAL.002";
    public const string EnumOutOfRange = "LP.VAL.003";
    public const string InvalidPath = "LP.VAL.004";
    public const string InvalidUrl = "LP.VAL.005";
    public const string EntityNotFound = "LP.STATE.001";
    public const string RootNotEntity = "LP.STATE.002";
    public const string CycleDetected = "LP.STATE.003";
    public const string BatchAborted = "LP.STATE.004";
    /// <summary>日志管道未装配（宿主未调 <c>EngineComposer.ConfigureLogging</c>）：<c>logs.*</c> 如实报错，
    /// 绝不返回空结果假装"没有日志"（观测面纪律）。</summary>
    public const string LogUnavailable = "LP.STATE.005";
    public const string ReadonlySession = "LP.SEC.001";
    public const string PathOutsideSandbox = "LP.SEC.002";
    public const string ConfirmRequired = "LP.SEC.003";
    public const string ConfirmExpired = "LP.SEC.004";
    public const string RateLimited = "LP.SEC.005";
    public const string DbError = "LP.ENG.001";
    public const string NetworkError = "LP.ENG.002";
    public const string Cancelled = "LP.ENG.003";
    public const string FileIoError = "LP.ENG.004";
    public const string UnknownCommand = "LP.SYS.001";
    public const string ProtocolMalformed = "LP.SYS.002";
    public const string Internal = "LP.SYS.003";

    public static EngineError Of(string code, string message, JsonElement? details = null,
        bool retryable = false, string? correlationId = null)
        => new(code, message, details, retryable, correlationId ?? "unknown");
}
