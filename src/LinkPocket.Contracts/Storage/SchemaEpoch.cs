namespace LinkPocket.Contracts;

/// <summary>
/// 主库**纪元戳**（库头 <c>PRAGMA user_version</c>）的单一事实源：应用（<c>SchemaMigrator</c>）、
/// 更新器、工具共用同一枚常量——更新器据它判定"同纪元直搬 / 更旧走桥 / 更新降级拒绝"。
/// </summary>
/// <remarks>
/// 4.0.0 起 <c>user_version</c> 只作**纪元标记**（结构 = v8 终态逐字）；结构再次变更时随新纪元一起抬——
/// 旧纪元的库一律拒绝，跨版本搬运只走导出/导入（内部资产/文档/UPDATE.md §3）。
/// </remarks>
public static class SchemaEpoch
{
    /// <summary>当前主库格式纪元。9 = 4.0.0 起。</summary>
    public const int EpochVersion = 9;
}
