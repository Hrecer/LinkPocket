using System.Text.Json;
using LinkPocket.Contracts;
using LinkPocket.Data;
using Microsoft.EntityFrameworkCore;

namespace LinkPocket.Engine;

/// <summary>
/// 命名批处理（宏/技能库）持久化（方案 4.3 IMacroStore）：schema v2 的 macros 表
/// （name 主键 / script_json / created_at / updated_at）。宏 = 命名批脚本，
/// 经 macro.run 在撤销/批同一条管道内按事务批语义执行。
/// </summary>
public sealed class MacroStore : IMacroStore
{
    private readonly Func<LinkPocketDbContext> _dbFactory;

    public MacroStore(Func<LinkPocketDbContext> dbFactory)
        => _dbFactory = dbFactory ?? throw new ArgumentNullException(nameof(dbFactory));

    public async Task SaveAsync(string name, string scriptJson, CancellationToken ct)
    {
        // 保存前校验脚本可解析（无效脚本禁止入库——宏是技能库，坏脚本比缺脚本更危险）
        try
        {
            _ = JsonSerializer.Deserialize<BatchScript>(scriptJson, EngineJson.ScriptOptions)
                ?? throw new EngineException(EngineErrors.Of(EngineErrors.ProtocolMalformed, $"宏「{name}」的脚本不是合法的批脚本"));
        }
        catch (JsonException)
        {
            // 坏 JSON 是输入问题而非内部错误：报 ProtocolMalformed，不冒泡成 LP.INTERNAL
            throw new EngineException(EngineErrors.Of(EngineErrors.ProtocolMalformed, $"宏「{name}」的脚本不是合法的批脚本"));
        }

        using var db = _dbFactory();
        var connection = db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open) await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO macros (name, script_json, created_at, updated_at)
            VALUES (@name, @script_json, @now, @now)
            ON CONFLICT(name) DO UPDATE SET script_json = excluded.script_json, updated_at = excluded.updated_at
            """;
        AddParam(command, "@name", name);
        AddParam(command, "@script_json", scriptJson);
        AddParam(command, "@now", DateTimeOffset.Now.ToString("O"));
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<string?> GetAsync(string name, CancellationToken ct)
    {
        using var db = _dbFactory();
        var connection = db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open) await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT script_json FROM macros WHERE name = @name";
        AddParam(command, "@name", name);
        var result = await command.ExecuteScalarAsync(ct);
        return result as string;
    }

    public async Task<IReadOnlyList<(string Name, string ScriptJson, DateTimeOffset UpdatedAt)>> ListAsync(CancellationToken ct)
    {
        using var db = _dbFactory();
        var connection = db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open) await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name, script_json, updated_at FROM macros ORDER BY name";
        var list = new List<(string, string, DateTimeOffset)>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            list.Add((reader.GetString(0), reader.GetString(1),
                DateTimeOffset.ParseExact(reader.GetString(2), "O", System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind)));
        return list;
    }

    public async Task<bool> DeleteAsync(string name, CancellationToken ct)
    {
        using var db = _dbFactory();
        var connection = db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open) await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM macros WHERE name = @name";
        AddParam(command, "@name", name);
        return await command.ExecuteNonQueryAsync(ct) > 0;
    }

    private static void AddParam(System.Data.Common.DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }
}
