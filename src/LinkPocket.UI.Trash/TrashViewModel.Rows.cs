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
/// TrashViewModel · 分区：主栏行重建与排序（partial——结构拆分，行为与公开 API 不变）。
/// </summary>
public partial class TrashViewModel
{
    /// <summary>
    /// 重建主栏：当前位置的直接内容（子单元 + 直接链接），按当前排序投影。
    /// 导航 = 快照内过滤（不逐单元取数）；深层内容随子单元再打开（与契约口径一致）。
    /// </summary>
    private void RebuildRows()
    {
        var loc = CurrentUnitId;
        var rows = new List<TrashRowViewModel>();

        foreach (var f in _unitList.Where(f => f.ParentTrashFolderId == loc))
        {
            rows.Add(new TrashRowViewModel(f.TrashFolderId, isFolder: true, f.Name)
            {
                LinkCount = f.LinkCount,
                Description = f.Description,
                OriginPath = f.OriginPath,
                DeletedAt = f.DeletedAt,
                Host = this,
            });
        }

        var key = loc ?? string.Empty;
        if (_linksByUnit.TryGetValue(key, out var links))
        {
            foreach (var l in links)
            {
                rows.Add(new TrashRowViewModel(l.Id, isFolder: false,
                    string.IsNullOrEmpty(l.Name) ? (l.Url ?? string.Empty) : l.Name)
                {
                    Url = l.Url,
                    Description = l.Description,
                    FaviconUrl = l.FaviconUrl,
                    OriginPath = l.OriginPath,
                    DeletedAt = l.DeletedAt,
                    Host = this,
                });
            }
        }

        SortRows(rows);

        // favicon 先补齐（等价比较要把图标算进去），再决定**要不要换集合**：
        // 内容完全一致时保持原集合不动——逐条 Clear/Add 会触发 N 次 CollectionChanged，
        // 视图随之整表重建，是"切页偶发卡顿"的主要来源（用户报障 2026-09-20）。
        foreach (var r in rows)
            if (!r.IsFolder) r.Favicon = FaviconService.LoadFromCache(r.FaviconUrl);

        if (!TrashRowViewModel.SameSequence(Rows, rows))
        {
            Rows.Clear();
            foreach (var r in rows) Rows.Add(r);
        }

        ApplySelectionToView();
        OnPropertyChanged(nameof(HasRows));
    }

    public bool HasRows => Rows.Count > 0;

    /// <summary>列头排序（快照内排序；默认删除时间倒序）：重排同一批数据、保留选中与所在位置。</summary>
    public void ApplySort(string field, bool ascending)
    {
        SortField = string.IsNullOrEmpty(field) ? "deleted_at" : field;
        SortAscending = ascending;
        RebuildRows();
    }

    private void SortRows(List<TrashRowViewModel> rows)
    {
        var sign = SortAscending ? 1 : -1;
        Comparison<TrashRowViewModel> byField = SortField switch
        {
            "name" => (a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCulture) * sign,
            "type" => (a, b) => string.Compare(a.TypeText, b.TypeText, StringComparison.CurrentCulture) * sign,
            "origin_path" => (a, b) => string.Compare(a.OriginText, b.OriginText, StringComparison.CurrentCulture) * sign,
            _ => (a, b) => a.DeletedAt.CompareTo(b.DeletedAt) * sign,
        };
        rows.Sort((a, b) =>
        {
            // 回收站主栏 = 平铺口径（单元与链接混排，保留 Windows 回收站语义：按删除时间倒序等）；
            // 分组只存在于左栏树（单元在前、链接在后），主栏不分组——用户定稿 2026-09-19
            var byValue = byField(a, b);
            return byValue != 0 ? byValue : string.CompareOrdinal(a.Id, b.Id);   // ID 兜底（次序确定）
        });
    }

    private void SetStatusText()
    {
        var where = IsInUnit ? CurrentUnitDisplayName : RootDisplayName;
        StatusText = $"{where} · {Rows.Count} 项";
        OnPropertyChanged(nameof(CurrentUnitDisplayName));
    }

    // ================= 导航 =================

}