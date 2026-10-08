using System.Data;
using System.Data.Common;
using System.Globalization;
using LinkPocket.Contracts;
using LinkPocket.Kernel;
using Microsoft.EntityFrameworkCore;

namespace LinkPocket.Data;

/// <summary>
/// 跨进程变更流的实现（**读附属库的审计尾**）：<c>audit_log.id</c> 是单调递增主键，天然就是流的水位。
///
/// <para><b>为什么读审计而不是新增变更表</b>：审计表是"一切副作用可审计"不变量早已落地的东西，
/// 每行都带 <c>command / caller / changes_json（含 touched + events + human_summary）/ batch_id / dry_run</c>——
/// 精确变更流需要的字段**一个不少**，加表只会有第二份真相源（还要每次提交去 bump）。
/// 审计在**附属库**（用户库只放书签数据），读它对用户库零影响。</para>
///
/// <para><b>归属过滤</b>：<paramref name="callerFilter"/> = 只看某个调用方的行。跨进程变更流传
/// <see cref="CallerRef.ExternalAgent"/> 的文本形态，于是"别的进程干的活"与"本进程自己干的活"
/// （界面 <c>ui:-</c> / 应用内助手 <c>agent:&lt;会话&gt;</c>）逐字可辨 —— 后者本来就有进程内事件，
/// 混进来会让用户自己点一下也弹"外部变更"。</para>
///
/// <para><b>降级</b>（本类的失败口径 = 观测面纪律：如实记日志 + 返回空，由调用方退化为整体失效）：
/// 审计被裁剪/重建到水位之下（用户删掉附属库、或 <c>audit.prune</c> 清空）→ 水位**归零**重来
/// （不归零的话新的行 id 永远追不上旧水位，变更流会永久哑掉）；读库异常 / 载荷不可解析 →
/// 该行按"无事件"如实上报，调用方据此整体失效。</para>
/// </summary>
public sealed class OpsChangeFeed : IChangeFeed
{
    /// <summary>单次拉取上限（水位只推进到已消费处，剩下的留到下一次轮询）。</summary>
    public const int DefaultLimit = 500;

    private const string Columns =
        "id, at, command, caller, success, dry_run, is_nested, batch_id, changes_json";

    private readonly Func<DbContext> _opsContextFactory;
    private readonly string? _callerFilter;
    private readonly object _gate = new();
    private long _watermark;

    /// <param name="opsContextFactory">附属库上下文工厂（组合根接线：<c>OpsDbContextFactory.CreateDbContext</c>）。</param>
    /// <param name="callerFilter">只看该调用方的行（<c>null</c> = 全部）；
    /// 跨进程变更流传 <c>CallerRef.ExternalAgent.ToString()</c>。</param>
    public OpsChangeFeed(Func<DbContext> opsContextFactory, string? callerFilter = null)
    {
        _opsContextFactory = opsContextFactory ?? throw new ArgumentNullException(nameof(opsContextFactory));
        _callerFilter = string.IsNullOrWhiteSpace(callerFilter) ? null : callerFilter;
        _watermark = MaximumId();
    }

    public long Watermark
    {
        get { lock (_gate) { return _watermark; } }
    }

    public IReadOnlyList<ExternalChange> Poll(int limit)
    {
        if (limit <= 0) limit = DefaultLimit;
        lock (_gate)
        {
            try
            {
                var max = MaximumId();
                // 水位**高于**当前最大 id = 审计被重建/裁剪到水位之下（用户删掉附属库、或 audit.prune 清空）。
                // 此时水位必须归零重来：否则新的行 id 永远追不上旧水位，变更流会**永久哑掉**。
                // 归零后本次照常读取（新行会被报出来），最坏情况是把重建后的既有行也报一遍——可接受。
                if (max < _watermark) _watermark = 0;
                if (max <= _watermark) return [];

                var rows = Read(_watermark, limit);
                if (rows.Count > 0) _watermark = rows[^1].Id;
                return rows;
            }
            catch (Exception ex) when (ex is DbException or InvalidOperationException)
            {
                // 读不到 = "无法精确"：如实记日志并返回空（调用方退化整体失效），绝不静默吞
                LpLog.Warn($"change feed read failed (precise invalidation unavailable): {ex.Message}", ex,
                    category: "data.ops");
                return [];
            }
        }
    }

    /// <summary>当前最大 id（只看过滤后的集合；空表 = 0）。</summary>
    private long MaximumId() => ScalarLong("SELECT COALESCE(MAX(id), 0) FROM audit_log" + Where());

    private IReadOnlyList<ExternalChange> Read(long afterId, int limit)
    {
        using var context = _opsContextFactory();
        var connection = context.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open) connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT {Columns} FROM audit_log WHERE id > @after" +
            (_callerFilter is null ? string.Empty : " AND caller = @caller") +
            " ORDER BY id LIMIT @limit";
        AddParameter(command, "@after", afterId);
        if (_callerFilter is not null) AddParameter(command, "@caller", _callerFilter);
        AddParameter(command, "@limit", (long)limit);

        var rows = new List<ExternalChange>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) rows.Add(Map(reader));
        return rows;
    }

    private long ScalarLong(string sql)
    {
        using var context = _opsContextFactory();
        var connection = context.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open) connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        if (_callerFilter is not null) AddParameter(command, "@caller", _callerFilter);
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private string Where() => _callerFilter is null ? string.Empty : " WHERE caller = @caller";

    private static ExternalChange Map(DbDataReader reader)
    {
        var changesJson = reader.IsDBNull(8) ? null : reader.GetString(8);
        var view = ChangeSetPayload.Parse(changesJson);
        return new ExternalChange(
            Id: reader.GetInt64(0),
            At: DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.None),
            Command: reader.GetString(2),
            Caller: reader.GetString(3),
            Success: reader.GetInt64(4) != 0,
            DryRun: reader.GetInt64(5) != 0,
            IsNested: reader.GetInt64(6) != 0,
            BatchId: reader.IsDBNull(7) ? null : reader.GetString(7),
            Touched: view?.Touched ?? [],
            Events: view?.Events ?? [],
            HumanSummary: view?.HumanSummary);
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
