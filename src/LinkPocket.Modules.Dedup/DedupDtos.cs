namespace LinkPocket.Modules.Dedup;

/// <summary>查重模块结果 DTO 组。</summary>
/// <summary>一个同址组。</summary>
public sealed record DedupGroup(
    string Url,
    int Count,
    IReadOnlyList<LinkPocket.Contracts.LinkDto> Links);

/// <summary>保留策略。</summary>
public static class DedupStrategy
{
    public const string KeepMostVisited = "keep_most_visited";
    public const string KeepNewest = "keep_newest";
    public const string KeepExplicit = "keep_explicit";

    public static readonly IReadOnlySet<string> All =
        new HashSet<string>([KeepMostVisited, KeepNewest, KeepExplicit], StringComparer.Ordinal);
}

/// <summary>计划中的一个组的处置。</summary>
public sealed record DedupPlanGroup(
    string Url,
    LinkPocket.Contracts.LinkDto Keep,
    IReadOnlyList<LinkPocket.Contracts.LinkDto> Trash);

/// <summary>完整计划（plan 纯干跑产出；apply 内部重建同一计划后执行）。</summary>
public sealed record DedupPlan(
    string Strategy,
    IReadOnlyList<DedupPlanGroup> Groups,
    int TotalToTrash);
