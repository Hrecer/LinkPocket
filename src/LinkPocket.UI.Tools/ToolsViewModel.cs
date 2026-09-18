using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using LinkPocket.Api;
using LinkPocket.Contracts;
using LinkPocket.Services;

namespace LinkPocket.ViewModels;

/// <summary>一个重复组（同一 URL 的多条链接）在去重主表里的行数据。</summary>
public sealed class DedupGroupRow
{
    public string Url { get; init; } = string.Empty;
    public List<LinkDto> Links { get; init; } = new();
    public int Count => Links.Count;
    public string LocationsSummary { get; init; } = string.Empty;
}

/// <summary>
/// 工具页 ViewModel（阶段 9 MVVM）：三个工具的业务逻辑全部在此——
/// 去重（扫描/分组/位置缓存/勾选守卫/删除并重算）、ID 跳转（统一走
/// <see cref="IContentLocator"/> 组件）、书签导入导出（预检/导入/导出+自校验，
/// 算法全在引擎契约，本类只转发命令调用）。视图（Views/ToolsPage）只做
/// 表格装配、状态渲染与文件对话框——界面不持有任何命令调用与业务规则。
/// </summary>
public sealed class ToolsViewModel
{
    private readonly EngineClient _api;
    private readonly IContentLocator? _locator;
    private readonly Func<string?, Task<string>> _resolveLinkPath;
    private readonly Func<Task> _refreshFolderTree;

    public ToolsViewModel(EngineClient api, IContentLocator? locator,
        Func<string?, Task<string>> resolveLinkPath, Func<Task> refreshFolderTree)
    {
        _api = api;
        _locator = locator;
        _resolveLinkPath = resolveLinkPath;
        _refreshFolderTree = refreshFolderTree;
    }

    // ============================================================
    // —— 去重 ——
    // ============================================================

    /// <summary>本会话是否已跑过查重（外部数据变更后据此决定自动重跑）。</summary>
    public bool HasRunDedup { get; private set; }

    /// <summary>重复组（主表行集；扫描为空时为空列表）。</summary>
    public List<DedupGroupRow> Groups { get; private set; } = new();

    /// <summary>当前展开明细的组内链接（回主表后为 null）。</summary>
    public List<LinkDto>? CurrentGroupLinks { get; private set; }

    /// <summary>当前展开明细的组 URL。</summary>
    public string CurrentGroupUrl { get; private set; } = string.Empty;

    /// <summary>明细里勾选待删除的链接 ID（视图据此渲染勾选态与按钮可用性）。</summary>
    public HashSet<string> CheckedIds { get; } = new();

    private Dictionary<string, string> _pathCache = new();

    /// <summary>
    /// 全库扫描重复 URL 并分组。抛出异常 = 读取数据失败（视图展示错误空态）；
    /// 返回的列表可能为空（没有重复）。
    /// </summary>
    public async Task<List<DedupGroupRow>> RunDedupAsync()
    {
        var links = await _api.LinkAllAsync();

        var groups = links
            .GroupBy(l => l.Url ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .OrderByDescending(g => g.Count())
            .ToList();

        HasRunDedup = true;

        if (groups.Count == 0)
        {
            Groups = new List<DedupGroupRow>();
            return Groups;
        }

        // 位置解析（每个链接一次；同一 URL 组内共享缓存）
        _pathCache = new Dictionary<string, string>();
        foreach (var link in groups.SelectMany(g => g))
        {
            if (!_pathCache.ContainsKey(link.LinkId))
                _pathCache[link.LinkId] = await _resolveLinkPath(link.ListId);
        }

        Groups = groups.Select(g => new DedupGroupRow
        {
            Url = g.Key,
            Links = g.ToList(),
            LocationsSummary = BuildLocationsSummary(g.ToList())
        }).ToList();
        return Groups;
    }

    /// <summary>清除查重结果（回到未运行态）。</summary>
    public void ClearDedup()
    {
        HasRunDedup = false;
        Groups = new List<DedupGroupRow>();
        _pathCache.Clear();
        LeaveGroup();
    }

    /// <summary>位置摘要：首条所在位置，多处时补「等 N 处」（不罗列全部，避免单元格噪音）。</summary>
    private string BuildLocationsSummary(List<LinkDto> links)
    {
        var paths = links
            .Select(l => _pathCache.TryGetValue(l.LinkId, out var p) ? p : "全部书签")
            .Distinct()
            .ToList();
        return paths.Count == 1 ? paths[0] : $"{paths[0]} 等 {paths.Count} 处";
    }

    /// <summary>展开某组明细：记录当前组并清空勾选。</summary>
    public void EnterGroup(DedupGroupRow row)
    {
        CurrentGroupUrl = row.Url;
        CurrentGroupLinks = row.Links;
        CheckedIds.Clear();
    }

    /// <summary>退出明细回主表：清当前组与勾选。</summary>
    public void LeaveGroup()
    {
        CheckedIds.Clear();
        CurrentGroupLinks = null;
        CurrentGroupUrl = string.Empty;
    }

    /// <summary>
    /// 切换某条链接的勾选态。返回 false = 触发「至少保留一条」守卫（视图闪提示，不改状态）。
    /// </summary>
    public bool ToggleChecked(string linkId)
    {
        if (CheckedIds.Contains(linkId))
        {
            CheckedIds.Remove(linkId);
            return true;
        }
        var total = CurrentGroupLinks?.Count ?? 0;
        if (total > 0 && CheckedIds.Count >= total - 1) return false;
        CheckedIds.Add(linkId);
        return true;
    }

    /// <summary>明细表某行的「位置」列（与主表同一缓存）。</summary>
    public string ResolvePath(LinkDto link)
        => _pathCache.TryGetValue(link.LinkId, out var p) ? p : "全部书签";

    /// <summary>
    /// 删除勾选的重复链接，随后重算当前 URL 的重复组：
    /// 返回非 null = 仍有多条（就地刷新明细，值为新组内链接）；
    /// 返回 null = 已剩一条及以下（视图应回主表并重跑查重）。
    /// </summary>
    public async Task<List<LinkDto>?> DeleteCheckedAsync()
    {
        if (CheckedIds.Count == 0) return CurrentGroupLinks;

        foreach (var linkId in CheckedIds.ToList())
            await _api.LinkTrashAsync(linkId);

        // 刷新目录树计数（事件防抖链路不动；查重重跑由视图在回表后承担）
        await _refreshFolderTree();

        var all = await _api.LinkAllAsync();
        var rest = all
            .Where(l => string.Equals(l.Url ?? string.Empty, CurrentGroupUrl, StringComparison.OrdinalIgnoreCase))
            .ToList();

        CheckedIds.Clear();
        if (rest.Count > 1)
        {
            foreach (var link in rest)
            {
                if (!_pathCache.ContainsKey(link.LinkId))
                    _pathCache[link.LinkId] = await _resolveLinkPath(link.ListId);
            }
            CurrentGroupLinks = rest;
            return rest;
        }

        LeaveGroup();
        return null;
    }

    // ============================================================
    // —— ID 跳转（统一走 IContentLocator 组件） ——
    // ============================================================

    /// <summary>跳转统一入口：全部经定位组件（进入目标目录并选中该行），本类不做任何定位算法。</summary>
    public async Task<LocateResult> JumpAsync(string id)
    {
        if (_locator == null)
            return new LocateResult(LocateStatus.NoHost, null, null, id, "定位组件不可用");
        return await _locator.LocateAsync(id);
    }

    // ============================================================
    // —— 书签导入 / 导出（算法全在引擎契约里，本类只转发命令调用） ——
    // ============================================================

    /// <summary>导入/导出流程忙态（互斥：同一时刻只允许一个流程在跑）。</summary>
    public bool BookmarkBusy { get; private set; }

    /// <summary>尝试开始一个书签流程；已有流程在跑时返回 false。</summary>
    public bool TryBeginBookmarkFlow()
    {
        if (BookmarkBusy) return false;
        BookmarkBusy = true;
        return true;
    }

    /// <summary>结束书签流程（视图在 finally 中调用）。</summary>
    public void EndBookmarkFlow() => BookmarkBusy = false;

    /// <summary>最近一次成功导出的产物路径（「打开所在文件夹」用）。</summary>
    public string LastExportPath { get; set; } = string.Empty;

    /// <summary>导入前只读预检：格式识别 + 条目统计（不写任何数据）。</summary>
    public Task<BookmarkFileInspectionDto> InspectBookmarkAsync(string filePath)
        => _api.BookmarksInspectAsync(filePath);

    /// <summary>导入：追加式还原文件夹层级，返回导入条数（引擎命令的结果 JsonElement）。</summary>
    public async Task<int> ImportBookmarksAsync(string filePath)
    {
        var result = await _api.BookmarksImportAsync(filePath);
        return ReadCount(result.Data, "links_created") + ReadCount(result.Data, "folders_created");
    }

    /// <summary>
    /// 导出：写到 <paramref name="directory"/> 下的时间戳文件名，并对产物自校验
    /// （把刚写出的文件再解析一遍，用产物自身的数据报数）。返回产物路径与校验信息。
    /// 引擎命令输出的 file_path 才是权威产物路径（含同名自动编号），以它为准。
    /// </summary>
    public async Task<(string OutputPath, BookmarkFileInspectionDto Info)> ExportBookmarksAsync(string directory)
    {
        var suggestedPath = System.IO.Path.Combine(directory,
            $"LinkPocket_书签导出_{DateTime.Now:yyyyMMdd_HHmmss}.html");
        var result = await _api.BookmarksExportAsync(suggestedPath);
        var outputPath = ReadString(result.Data, "file_path") ?? suggestedPath;
        var info = await _api.BookmarksInspectAsync(outputPath);
        LastExportPath = outputPath;
        return (outputPath, info);
    }

    private static int ReadCount(JsonElement? data, string property)
    {
        if (data is { } d && d.ValueKind == JsonValueKind.Object
            && d.TryGetProperty(property, out var el) && el.ValueKind == JsonValueKind.Number)
            return el.GetInt32();
        return 0;
    }

    private static string? ReadString(JsonElement? data, string property)
    {
        if (data is { } d && d.ValueKind == JsonValueKind.Object
            && d.TryGetProperty(property, out var el) && el.ValueKind == JsonValueKind.String)
            return el.GetString();
        return null;
    }
}
