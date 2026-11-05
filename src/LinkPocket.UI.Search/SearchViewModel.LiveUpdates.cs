using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using LinkPocket.Contracts;
using LinkPocket.I18n;
using LinkPocket.Models;
using LinkPocket.Views;

namespace LinkPocket.ViewModels;

/// <summary>
/// 搜索页的**活结果集**：完整加载后按精确变更（ChangeSet.Touched / 外部变更流 Touched）
/// 只更新真正变化的那几行；插入/删除/排序位置变化用增量集合通知，不重建整表。
///
/// <para><b>正确性兜底</b>：增量只处理“能安全局部处理”的链接变更。文件夹变更（整棵子树/路径范围）、
/// 变更 ID 超限、拿不到精确信息（Precise=false）、查询期间失败——一律退回全量快照。
/// 增量是加速器，不是正确性来源。</para>
/// </summary>
public sealed partial class SearchViewModel
{
    /// <summary>单批最多局部对账的链接数（与引擎 search.reconcile 的上限一致）；超出走全量。</summary>
    private const int MaxLiveLinkChanges = 512;

    private readonly HashSet<string> _pendingLinkIds = new(StringComparer.Ordinal);
    private bool _pendingFullRefresh;
    private bool _isActive;
    private bool _applyingChanges;

    /// <summary>列表结构即将变化（插入/删除/移动）：视图在此抓“第一条可见行 + 行内偏移”。</summary>
    public event EventHandler? LiveUpdateStarting;

    /// <summary>列表结构变化完成：视图据此把同一行滚回原阅读位置。</summary>
    public event EventHandler? LiveUpdateCompleted;

    /// <summary>
    /// 接收一次防抖变更批次。无论搜索页是否活跃都接收：隐藏时只入队，进入时一次对账。
    /// </summary>
    public void NotifyDataChanges(IReadOnlyList<EntityRef> touched, IReadOnlyList<string> events, bool precise)
    {
        if (string.IsNullOrWhiteSpace(LastQuery)) return;

        var relevantEvent = events.Any(e =>
            e == DomainEventNames.LinksChanged ||
            e == DomainEventNames.FoldersChanged ||
            e == DomainEventNames.TrashChanged);
        var hasLink = touched.Any(t => t.Type == "link");
        var hasFolder = touched.Any(t => t.Type == "folder");
        var hasDatabase = touched.Any(t => t.Type == "database");
        var onlyFavicon = touched.Count > 0 && touched.All(t => t.Type == "favicon_cache");

        // 与搜索数据面无关的事件（宏 / 暂存 / 图标缓存 / 回收站里本就不在结果中的快照）直接忽略。
        if (!hasLink && !hasFolder && !hasDatabase)
        {
            if (onlyFavicon) return;
            if (!relevantEvent)
            {
                // 探针只报告“有提交”而拿不到任何实体/事件（例如绕过引擎的直写）：
                // 必须保守全刷，绝不能不处理。
                if (precise) return;
                _pendingFullRefresh = true;
            }
            else if (precise && touched.Count > 0)
            {
                return;   // 例如 favicon_cache + links.changed：行数据没变
            }
            else
            {
                // 有相关事件但拿不到实体 / 完全无实体：无法安全局部处理。
                _pendingFullRefresh = true;
            }
        }
        else
        {
            if (!precise)
            {
                _pendingFullRefresh = true;
            }
            else if (hasFolder || hasDatabase || events.Contains(DomainEventNames.FoldersChanged))
            {
                // 文件夹改名/移动/删除影响整棵子树的路径显示与 path 范围命中；数据库级操作同理。
                _pendingFullRefresh = true;
            }
            else
            {
                foreach (var entity in touched)
                {
                    if (entity.Type == "link" && !string.IsNullOrWhiteSpace(entity.Id))
                        _pendingLinkIds.Add(entity.Id);
                }
                if (_pendingLinkIds.Count > MaxLiveLinkChanges)
                    _pendingFullRefresh = true;
            }
        }

        if (_pendingFullRefresh) _pendingLinkIds.Clear();
        if (_isActive && !IsLoadingAll && IsCompleteLiveSet)
            _ = ApplyPendingChangesAsync();
    }

    /// <summary>删除等页面动作已经拿到明确链接 ID：直接走同一条增量对账（不等 300ms 事件）。</summary>
    private Task ApplyLinkChangesAsync(IReadOnlyList<string> linkIds)
    {
        foreach (var id in linkIds) _pendingLinkIds.Add(id);
        if (_pendingLinkIds.Count > MaxLiveLinkChanges) _pendingFullRefresh = true;
        return ApplyPendingChangesAsync();
    }

    /// <summary>是否还有排队/在途的活结果集对账（量具与测试等待“落地”用）。</summary>
    public bool HasPendingLiveChanges => _pendingFullRefresh || _pendingLinkIds.Count > 0 || _applyingChanges;

    /// <summary>完整结果集 = 没有占位/失败/未补齐；只有此时才允许增量插入/删除。</summary>
    private bool IsCompleteLiveSet
        => !_loadFailed
           && !IsLoadingAll
           && TotalHitCount == Results.Count
           && _loadedCount == TotalHitCount;

    private void ClearPendingChanges()
    {
        _pendingLinkIds.Clear();
        _pendingFullRefresh = false;
    }

    /// <summary>对当前排队变更做一次对账；新变更在这期间到达会留在队列，下一轮再处理。</summary>
    private async Task ApplyPendingChangesAsync()
    {
        if (_applyingChanges) return;
        if (string.IsNullOrWhiteSpace(LastQuery)) { ClearPendingChanges(); return; }
        if (!IsCompleteLiveSet) return;   // 补页完成后 CompleteLoad 会再调用

        if (_pendingFullRefresh)
        {
            ClearPendingChanges();
            await RefreshResultsAsync(forceReplace: true);
            return;
        }
        if (_pendingLinkIds.Count == 0) return;

        var ids = _pendingLinkIds.ToArray();
        _pendingLinkIds.Clear();
        var gen = _searchGen;
        _applyingChanges = true;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var result = await Task.Run(() => _api.SearchReconcileAsync(
                LastQuery, SearchTitle, SearchUrl, SearchDesc, SearchPath, ids), CancellationToken.None);
            if (gen != _searchGen) return;

            if (result.NeedsFullRefresh)
            {
                _pendingFullRefresh = true;
                ClearPendingChanges();
                await RefreshResultsAsync(forceReplace: true);
                return;
            }

            ApplyReconciledLinks(result.Matched, ids);
            LpLog.Info($"search live update: checked={ids.Length} applied={result.Matched.Count} {watch.ElapsedMilliseconds}ms",
                category: "search");
        }
        catch (OperationCanceledException)
        {
            // 新查询接管：旧代次结果不得落回。
        }
        catch (Exception ex)
        {
            LpLog.Error("search live update failed; falling back to a full snapshot", ex);
            if (gen == _searchGen)
            {
                _pendingFullRefresh = true;
                ClearPendingChanges();
                await RefreshResultsAsync(forceReplace: true);
            }
        }
        finally
        {
            _applyingChanges = false;
            if (gen == _searchGen && !IsLoadingAll && IsCompleteLiveSet
                && (_pendingFullRefresh || _pendingLinkIds.Count > 0))
                _ = ApplyPendingChangesAsync();
        }
    }

    /// <summary>把对账结果落到活结果集：更新 / 移除 / 插入 / 按同一套排序键移动。</summary>
    private void ApplyReconciledLinks(IReadOnlyList<LinkDto> matched, IReadOnlyCollection<string> requestedIds)
    {
        var matchedById = matched
            .GroupBy(d => d.LinkId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        var toRemove = requestedIds
            .Where(id => !matchedById.ContainsKey(id) && _itemsById.ContainsKey("l:" + id))
            .ToArray();
        var toInsert = matched.Where(d => !_itemsById.ContainsKey("l:" + d.LinkId)).ToArray();
        var toUpdate = matched.Where(d => _itemsById.ContainsKey("l:" + d.LinkId)).ToArray();
        var willMove = toUpdate.Any(d => SortKeyChanged(_itemsById["l:" + d.LinkId], d));

        var structural = toRemove.Length > 0 || toInsert.Length > 0 || willMove;
        if (structural) LiveUpdateStarting?.Invoke(this, EventArgs.Empty);

        foreach (var id in toRemove)
        {
            var key = "l:" + id;
            if (_itemsById.TryGetValue(key, out var row))
            {
                Results.Remove(row);
                _itemsById.Remove(key);
            }
        }

        foreach (var dto in toUpdate)
        {
            var key = "l:" + dto.LinkId;
            if (!_itemsById.TryGetValue(key, out var item) || item.IsFolder) continue;
            var row = (SearchResultRow)item;
            var move = SortKeyChanged(row, dto);
            row.Refill(dto, LastQuery);
            if (move) MoveRow(row);
        }

        foreach (var dto in toInsert)
        {
            var row = new SearchResultRow();
            row.Fill(dto, LastQuery);
            var index = FindInsertIndex(row);
            Results.Insert(index, row);
            _itemsById["l:" + dto.LinkId] = row;
        }

        if (structural)
        {
            RebuildRowOrderMaps();
            LiveUpdateCompleted?.Invoke(this, EventArgs.Empty);
        }

        TotalHitCount = Results.Count;
        _loadedCount = Results.Count;
        OnPropertyChanged(nameof(TotalHitCount));
        OnPropertyChanged(nameof(LoadedCount));
        OnPropertyChanged(nameof(LoadingNotice));

        if (Results.Count == 0)
            EmptyState = new SearchEmptyState("emoticon-sad-outline",
                Loc.K("search.state.noResults", LastQuery),
                Loc.K("search.state.noResultsHint"),
                "SecondaryContainer", "OnSecondaryContainer");
        else if (EmptyState is { IconKind: "emoticon-sad-outline" })
            EmptyState = new SearchEmptyState("magnify", Loc.K("search.state.prompt"),
                Loc.K("search.state.promptHint"),
                "PrimaryContainer", "OnPrimaryContainer");

        var changed = Selection.RemoveMissing(id => _itemsById.ContainsKey(id));
        if (changed || Selection.HasAny) OnSelectionChanged();
    }

    /// <summary>整批结构变化后按当前行序重建行号索引（引用级 O(N)，不重建行对象/容器）。</summary>
    private void RebuildRowOrderMaps()
    {
        _idsByRow = new string?[Results.Count];
        for (var i = 0; i < Results.Count; i++) _idsByRow[i] = Results[i].Key;
        RebuildOrderedIds();
    }

    /// <summary>当前行在其排序键下是否应换位置：只看当前排序列是否被这次更新改动。</summary>
    private bool SortKeyChanged(LinkItem row, LinkDto dto)
    {
        return _sortField switch
        {
            "title" => !string.Equals(row.Title, dto.Title ?? string.Empty, StringComparison.Ordinal),
            "url" => !string.Equals(row.Url, dto.Url ?? string.Empty, StringComparison.Ordinal),
            "created_at" => row.CreatedAt != dto.CreatedAt,
            "updated_at" => row.UpdatedAt != dto.UpdatedAt,
            "last_visited_at" => row.LastVisitedAt != dto.LastVisitedAt,
            "visit_count" => row.VisitCount != dto.VisitCount,
            "is_important" => row.IsImportant != dto.IsImportant,
            "path" => !string.Equals(row.ListId, dto.ListId, StringComparison.Ordinal)
                      || !string.Equals(row.DisplayTitle, dto.Title ?? dto.Url ?? string.Empty, StringComparison.Ordinal),
            _ => false,
        };
    }

    private void MoveRow(LinkItem row)
    {
        var current = Results.IndexOf(row);
        if (current < 0) return;
        var folderCount = CountLeadingFolders();
        if (current < folderCount) return;   // 链接不会进入文件夹块
        Results.RemoveAt(current);
        var target = FindInsertIndex(row);
        Results.Insert(target, row);
    }

    private int CountLeadingFolders()
    {
        var count = 0;
        while (count < Results.Count && Results[count].IsFolder) count++;
        return count;
    }

    /// <summary>二分查找该行在链接块中的插入位置（文件夹块恒在最前）。</summary>
    private int FindInsertIndex(LinkItem row)
    {
        var start = CountLeadingFolders();
        var lo = start;
        var hi = Results.Count;
        var comparer = LiveOrdering;
        while (lo < hi)
        {
            var mid = lo + (hi - lo) / 2;
            if (comparer.Compare(Results[mid], row) <= 0) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }

    private IComparer<LinkItem> LiveOrdering =>
        SearchOrdering.Create(_sortField, _sortAscending, _resolveFolderOrder);

    /// <summary>活结果集行号查询（视图恢复滚动锚点用；只在结构变化完成后调用一次）。</summary>
    public int IndexOfRowKey(string key) => Array.IndexOf(_idsByRow, key);
}
