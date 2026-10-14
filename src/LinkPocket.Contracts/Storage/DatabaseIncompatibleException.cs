namespace LinkPocket.Contracts;

/// <summary>
/// 库文件被**明确拒绝**（纪元戳不符 / 外来库）：只有「当前纪元的库」与「空文件」两种输入能被接受，
/// v8 及更早的库文件一律拒绝——不迁移、不读取、不转换、不删除（处置权归用户）；
/// 迁移唯一通道 = 导出/导入（旧版导出备份 → 本版导入）。
///
/// <para>抛出方 = <c>LinkPocket.Data.SchemaMigrator</c>（建库/打开时机）；界面启动序捕获它，
/// 给出专门的一次性指引弹窗（通用"启动失败"弹窗说不清该怎么办）。</para>
/// </summary>
public sealed class DatabaseIncompatibleException : InvalidOperationException
{
    /// <summary>被拒绝的库文件路径（供界面指引直接回显，用户好找文件）。</summary>
    public string DatabasePath { get; }

    public DatabaseIncompatibleException(string message, string databasePath) : base(message)
        => DatabasePath = databasePath;
}
