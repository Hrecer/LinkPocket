namespace LinkPocket.Contracts;

/// <summary>
/// 一条工具声明（<c>Name</c> 命令名 / <c>Description</c> 面向模型的说明 / <c>ParametersJson</c> = JSON Schema 原文）。
/// <para>归属契约层：它是"agent 工具面"的中性形态，被内置助手（发给模型）与外部 Agent 网关（转成 MCP 工具）
/// 共同消费——两条通道对该命令的描述与参数 schema 因此逐字一致。构建入口 = <see cref="AiToolCatalog"/>。</para>
/// </summary>
public sealed record AiToolSpec(string Name, string Description, string ParametersJson);
