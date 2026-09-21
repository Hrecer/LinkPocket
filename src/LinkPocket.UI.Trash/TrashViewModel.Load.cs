using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Input;
using LinkPocket.Contracts;
using LinkPocket.Services;

namespace LinkPocket.ViewModels;

/// <summary>
/// TrashViewModel · 分区：快照加载（trash.overview → 树/行/面包屑就位）（partial——结构拆分，行为与公开 API 不变）。
/// </summary>
public partial class TrashViewModel
{
    // ================= 快照与加载 =================

    private List<TrashFolderDto> _unitList = new();
    private Dictionary<string, TrashFolderDto> _unitById = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<TrashEntryDto>> _linksByUnit = new(StringComparer.Ordinal);

    /// <summary>排序口径（默认删除时间倒序）；列头点击切换。</summary>
    public string SortField { get; private set; } = "deleted_at";
    public bool SortAscending { get; private set; }

    private bool _loadPending;
    private bool _loadPendingNavigating;

    /// <summary>
    /// 重新拉取快照并重建视图（树 / 主栏 / 面包屑 / 选中与落点投影）。
    /// <paramref name="navigating"/> = 用户发起的加载（F5 / 切页进入）：亮遮罩与入场动画；
    /// 事件驱动的后台刷新一律静默（与浏览页同口径）。
    /// </summary>
    public async Task LoadAsync(bool navigating = false)
    {
        if (IsLoading)
        {
            _loadPending = true;   // 重入守卫：加载中又来请求 → 收尾补刷（navigating 按最后一次计）
            _loadPendingNavigating |= navigating;
            return;
        }

        IsLoading = true;
        HasError = false;
        ErrorMessage = string.Empty;
        if (navigating) IsNavigating = true;
        var wasNavigation = navigating;
        var selectedIds = Selection.Ids.ToList();     // 刷新保留选中（按 ID 重新投影；已删 ID 自动消失）

        try
        {
            var snapshot = await _client.TrashOverviewAsync();
            _unitList = snapshot.Folders;
            _unitById = snapshot.Folders.ToDictionary(f => f.TrashFolderId, StringComparer.Ordinal);
            _linksByUnit.Clear();
            foreach (var l in snapshot.Links)
            {
                var key = l.TrashFolderId ?? string.Empty;
                if (!_linksByUnit.TryGetValue(key, out var list)) _linksByUnit[key] = list = new List<TrashEntryDto>();
                list.Add(l);
            }
            _allLinks = snapshot.Links;

            // 当前位置已不存在（外部清空/永久删除）→ 回根，避免"停在一个不存在的单元里"
            if (CurrentUnitId != null && !_unitById.ContainsKey(CurrentUnitId))
                Controller.NavigateTo(null);

            RebuildTree();
            RebuildRows();
            RebuildBreadcrumbs();
            RemoveMissingFromSelection(selectedIds);
            // 详情覆盖层的条目已被还原/永久删除 → 覆盖层自动关闭（"条目消失即关"，状态在 VM、视图不持）
            if (IsDetailOverlayOpen && (_detailLinkId == null || !_allLinks.Any(l => l.Id == _detailLinkId)))
                CloseDetailOverlay();
            SetStatusText();
        }
        catch (Exception ex)
        {
            HasError = true;
            ErrorMessage = $"加载回收站失败: {ex.Message}";
            StatusText = ErrorMessage;
            LpLog.Error("回收站加载失败", ex);   // 观测面：失败必须留痕
        }
        finally
        {
            IsLoading = false;
            if (navigating) IsNavigating = false;
            CommandRefresh.Request();
            RefreshCompleted?.Invoke(this, wasNavigation);
            if (_loadPending)
            {
                _loadPending = false;
                var pendingNavigating = _loadPendingNavigating;
                _loadPendingNavigating = false;
                await LoadAsync(pendingNavigating);
            }
        }
    }

    private List<TrashEntryDto> _allLinks = new();

    /// <summary>全部链接快照（详情/提示用）。</summary>
    internal IReadOnlyList<TrashEntryDto> AllLinks => _allLinks;

}