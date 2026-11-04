namespace LinkPocket.Modules.Dedup;

// DedupGroup 已提升到 LinkPocket.Contracts（UI 客户端经 dedup.scan 直接消费同一 DTO）。

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
