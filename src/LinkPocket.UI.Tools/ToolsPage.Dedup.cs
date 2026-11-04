using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using LinkPocket.Contracts;
using LinkPocket.Input;
using LinkPocket.Models;
using LinkPocket.Services;
using LinkPocket.ViewModels;
using Material3.Wpf;
using LinkPocket.I18n;
using LinkPocket.UIKit;

namespace LinkPocket.Views

{
public partial class ToolsPage : UserControl
{
        // ============================================================
        // —— 去重：主表（重复组） ——
        // ============================================================

        private void SetupPaneTable()
        {
            PaneTable.Columns = new[]
            {
                new DataTableColumn
                {
                    Field = "url", LabelKey = "ui.noun.duplicateUrl", Width = -3,
                    SortKey = r => (IComparable)((DedupGroupRow)r).Url
                },
                new DataTableColumn
                {
                    Field = "count", LabelKey = "ui.noun.duplicateCount", Width = 90,
                    SortKey = r => (IComparable)((DedupGroupRow)r).Count
                },
                new DataTableColumn
                {
                    Field = "locations", LabelKey = "ui.noun.locatedIn", Width = -2,
                    SortKey = r => (IComparable)((DedupGroupRow)r).LocationsSummary.Resolve()
                },
            };

            // 默认排序与旧工厂模式一致：URL 升序（模板模式不内部排序，由下方 ApplyPaneSort 落实）
            PaneTable.SortField = "url";
            PaneTable.SortAscending = true;
            PaneTable.SortChanged += (_, _) => ApplyPaneSort();
            ShowDedupPlaceholder();
        }

        /// <summary>按表头当前排序重排主表（模板模式：只换 ItemsSource，视口行才实例化）。</summary>
        private void ApplyPaneSort()
        {
            if (_groups.Count == 0)
            {
                PaneTable.ItemsSource = null;
                return;
            }

            var ascending = PaneTable.SortAscending;
            var items = PaneTable.EffectiveSortField switch
            {
                "count" => ascending ? _groups.OrderBy(g => g.Count) : _groups.OrderByDescending(g => g.Count),
                "locations" => ascending
                    ? _groups.OrderBy(g => g.LocationsSummary.Resolve())
                    : _groups.OrderByDescending(g => g.LocationsSummary.Resolve()),
                _ => ascending
                    ? _groups.OrderBy(g => g.Url)
                    : _groups.OrderByDescending(g => g.Url),
            };
            PaneTable.ItemsSource = items.ToList();
        }

        /// <summary>
        /// 查重主流程：业务在 <see cref="ToolsViewModel.RunDedupAsync"/>，本方法只负责
        /// 加载/空态/结果四种视觉状态的切换与操作按钮文案（渲染检查经本方法反射驱动）。
        /// </summary>
        private async Task RunDedupAsync(bool navigating = false)
        {
            if (navigating) DetailBusyOverlay.IsBusy = true;   // 只有用户发起的重查才亮遮罩
            try
            {
                await RunDedupCoreAsync();
            }
            finally
            {
                if (navigating) DetailBusyOverlay.IsBusy = false;
            }
        }

        private async Task RunDedupCoreAsync()
        {
            // ⚠️ 扫描期间**不清表**（保留旧结果，内容未变时下方直接跳过重设）：清空 + 重新填充 =
            // 切页/重扫时的整表重建白烧 + 视觉闪空（低性能设备切页偶发卡顿）。
            PaneTable.EmptyContent = BuildState("refresh", Loc.K("tools.dedup.scanningTitle"), Loc.K("tools.dedup.scanningHint"));
            PaneSubtitle.SetText(DedupSubtitle);

            List<DedupGroupRow> groups;
            try
            {
                groups = await VmTools.RunDedupAsync();
            }
            catch (Exception ex)
            {
                LpLog.Error("duplicate scan failed", ex);
                PaneTable.EmptyContent = BuildState("alert-circle-outline", Loc.K("tools.dedup.readFailedTitle"), Loc.K("err.unexpected"));
                return;
            }

            var previous = _groups;   // 旧结果（视图镜像）：下面据此判断"要不要重设表格"
            _groups = groups;
            if (_dedupActionIcon != null) _dedupActionIcon.Kind = "refresh";
            if (_dedupActionText != null) _dedupActionText.SetText(Loc.K("tools.dedup.again"));
            if (_dedupClearBtn != null) _dedupClearBtn.IsEnabled = true;

            if (groups.Count == 0)
            {
                if (previous.Count > 0) PaneTable.ItemsSource = null;   // 结果全消失 → 清表让空态可见
                PaneTable.EmptyContent = BuildState("content-duplicate", Loc.K("tools.dedup.noneTitle"),
                    Loc.K("tools.dedup.noneHint"));
                PaneSubtitle.SetText(Loc.K("tools.dedup.none"));
                return;
            }

            // 内容未变（重扫/切回常见）→ **不重设 ItemsSource**：模板模式重设仍会重建视口行
            if (!DedupGroupRow.SameSequence(previous, groups)) ApplyPaneSort();
            PaneTable.EmptyContent = null!;
            PaneSubtitle.SetText(Loc.K("tools.dedup.foundSubtitle", groups.Count, groups.Sum(g => g.Count)));
        }

        // ============================================================
        // —— 去重：明细（组内各条） ——
        // ============================================================

        private void SetupDetailTable()
        {
            DetailTable.Columns = new[]
            {
                new DataTableColumn
                {
                    Field = "check", LabelKey = "", Width = 44
                },
                new DataTableColumn
                {
                    Field = "title", LabelKey = "ui.noun.name", Width = -1,
                    SortKey = r => (IComparable)TitleKey((DedupLinkRow)r)
                },
                new DataTableColumn
                {
                    // 与搜索页/智能列表同口径：路径最宽，右侧时间列压缩到刚好够用
                    Field = "path", LabelKey = "ui.noun.location", Width = -3,
                    SortKey = r => (IComparable)((DedupLinkRow)r).Path.Resolve()
                },
                new DataTableColumn
                {
                    Field = "updated_at", LabelKey = "ui.noun.updatedAt", Width = 130,
                    SortKey = r => (IComparable)((DedupLinkRow)r).Link.UpdatedAt
                },
                new DataTableColumn
                {
                    Field = "last_visited_at", LabelKey = "ui.noun.lastVisited", Width = 130,
                    SortKey = r => (IComparable)(((DedupLinkRow)r).Link.LastVisitedAt ?? DateTime.MinValue)
                },
                new DataTableColumn
                {
                    Field = "visit_count", LabelKey = "ui.noun.visitCount", Width = 84,
                    SortKey = r => (IComparable)((DedupLinkRow)r).Link.VisitCount
                },
                // 重复组明细**没有**「操作」列 / 行内跳转按钮：
                // 对比页是"看差异"的只读视图；「跳转」（顶部 URL 组药丸 / 右栏图标钮）走定位组件 IContentLocator
                // ——与「ID 跳转」工具同一套语义（进目录 + 选中行），本页不自带定位算法。
            };

            // 与旧工厂模式的默认排序一致：标题升序
            DetailTable.SortField = "title";
            DetailTable.SortAscending = true;
            DetailTable.SortChanged += (_, _) => ApplyDetailSort();
        }

        private static string TitleKey(DedupLinkRow row)
            => string.IsNullOrEmpty(row.Link.Title) ? row.Url : row.Link.Title;

        /// <summary>按表头当前排序重排明细（模板模式：只换 ItemsSource，视口行才实例化）。</summary>
        private void ApplyDetailSort()
        {
            if (_detailLinks.Count == 0)
            {
                DetailTable.ItemsSource = null;
                return;
            }

            var ascending = DetailTable.SortAscending;
            var items = DetailTable.EffectiveSortField switch
            {
                "path" => ascending
                    ? _detailLinks.OrderBy(r => r.Path.Resolve())
                    : _detailLinks.OrderByDescending(r => r.Path.Resolve()),
                "updated_at" => ascending
                    ? _detailLinks.OrderBy(r => r.Link.UpdatedAt)
                    : _detailLinks.OrderByDescending(r => r.Link.UpdatedAt),
                "last_visited_at" => ascending
                    ? _detailLinks.OrderBy(r => r.Link.LastVisitedAt ?? DateTime.MinValue)
                    : _detailLinks.OrderByDescending(r => r.Link.LastVisitedAt ?? DateTime.MinValue),
                "visit_count" => ascending
                    ? _detailLinks.OrderBy(r => r.Link.VisitCount)
                    : _detailLinks.OrderByDescending(r => r.Link.VisitCount),
                _ => ascending
                    ? _detailLinks.OrderBy(TitleKey)
                    : _detailLinks.OrderByDescending(TitleKey),
            };
            DetailTable.ItemsSource = items.ToList();
        }

        /// <summary>明细行数据镜像（模板绑定这一份；选中投影 / ↑↓ 顺序 / 右栏都读它）。</summary>
        private List<DedupLinkRow> _detailLinks = new();

        private List<DedupLinkRow> BuildDetailRows(IEnumerable<LinkDto> links)
            => links.Select(l => new DedupLinkRow(l, VmTools.ResolvePath(l))).ToList();

        /// <summary>明细加载代次：切组/返回会推进，迟到的旧加载结果直接丢弃。</summary>
        private int _detailGeneration;

        /// <summary>
        /// 展开明细：组状态记录在 VM（EnterGroup），先亮面板，再按需读取该组链接。
        /// <paramref name="preserveSelection"/> = true 时**保留仍在新结果里的选中**（F5 重查 / 入口对齐重进）；
        /// 换组进入时为 false（清选中）。
        /// </summary>
        private async Task EnterDetailAsync(DedupGroupRow row, bool preserveSelection = false)
        {
            var generation = ++_detailGeneration;
            var previous = preserveSelection ? VmTools.DetailSelection.Ids.ToList() : null;

            VmTools.EnterGroup(row);   // 进组即清选中（换组语义）；需要保留的由加载完成后重新落回
            _detailLinks = new List<DedupLinkRow>();

            DetailUrlText.Text = row.Url;
            DetailHintText.SetText(Loc.K("tools.dedup.groupHint", row.Count));
            DetailTable.EmptyContent = BuildState("refresh", Loc.K("tools.dedup.loadingGroupTitle"),
                Loc.K("tools.dedup.loadingGroupHint"));
            DetailTable.ItemsSource = null;
            ApplyDetailSelectionProjection();   // 清掉上一组的选中投影/右栏
            UpdateDeleteState();
            MainPanel.Visibility = Visibility.Collapsed;
            DetailPanel.Visibility = Visibility.Visible;
            // 焦点收回**页面根**（原先 Focus 明细面板容器：容器拿到键盘焦点后会画一条原生焦点虚线框，
            // 表现为明细页出现黑虚线）。焦点在页内 = 快捷键照常路由（ShortcutHost 不变式）。
            PageFocus.Restore(this);

            List<LinkDto>? links;
            try
            {
                links = await VmTools.LoadGroupLinksAsync(row);
            }
            catch (Exception ex)
            {
                LpLog.Error("load duplicate group links failed", ex);
                if (generation == _detailGeneration)
                    DetailTable.EmptyContent = BuildState("alert-circle-outline",
                        Loc.K("tools.dedup.readFailedTitle"), Loc.K("err.unexpected"));
                return;
            }

            // 加载期间用户已切组/返回：结果与当前面板无关，丢弃。
            if (generation != _detailGeneration || links is null) return;

            // 外部改动把该组删到只剩 1 条及以下：组已不再重复，回主表并按入口对齐重扫。
            if (links.Count <= 1)
            {
                GoBackToList();
                await RunDedupAsync();
                return;
            }

            _detailLinks = BuildDetailRows(links);
            DetailHintText.SetText(Loc.K("tools.dedup.groupHint", links.Count));
            DetailTable.EmptyContent = links.Count > 0
                ? null!
                : BuildState("content-duplicate", Loc.K("tools.dedup.noneTitle"), Loc.K("tools.dedup.noneHint"));
            ApplyDetailSort();

            if (previous is { Count: > 0 })
                VmTools.DetailSelection.Set(previous.Where(id => _detailLinks.Any(l => l.LinkId == id)));

            ApplyDetailSelectionProjection();   // 行重建后同步行绘制与右栏（保留的选中在此重投）
            UpdateDeleteState();
        }

        private void GoBackToList()
        {
            _detailGeneration++;   // 使进行中的组加载失效
            VmTools.LeaveGroup();
            _detailLinks = new List<DedupLinkRow>();
            DetailTable.EmptyContent = null!;
            DetailTable.ItemsSource = null;
            DetailPanel.Visibility = Visibility.Collapsed;
            MainPanel.Visibility = Visibility.Visible;
        }

        private void DetailBack_Click(object sender, RoutedEventArgs e) => GoBackToList();

        private void CopyUrl_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrEmpty(VmTools.CurrentGroupUrl))
            {
                try { Clipboard.SetText(VmTools.CurrentGroupUrl); } catch { /* 剪贴板被占用：复制失败不阻断 */ }
            }
        }

        // ============================================================
        // —— 模板行交互（模板模式下控件不代建行，事件由行模板路由到这里） ——
        // ============================================================

        private async void PaneRow_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement { DataContext: DedupGroupRow row })
                await EnterDetailAsync(row);
        }

        private void DetailRow_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            // 勾选按钮自己处理点击，不把"勾选"同时当成"选中行"
            if (e.OriginalSource is DependencyObject source && IsInsideButton(source)) return;
            if (sender is FrameworkElement { DataContext: DedupLinkRow row })
                VmTools.DetailSelection.SelectSingle(row.LinkId);
        }

        private static bool IsInsideButton(DependencyObject source)
        {
            for (DependencyObject? d = source; d != null; d = VisualTreeHelper.GetParent(d))
                if (d is Button) return true;
            return false;
        }

        /// <summary>明细勾选按钮：守卫规则在 VM（至少保留一条）；勾选态是行投影的属性。</summary>
        private async void DetailCheck_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement { DataContext: DedupLinkRow row }) return;
            if (!VmTools.ToggleChecked(row.LinkId))
            {
                await FlashSelectionInfo(Loc.K("tools.dedup.keepOne"));
                return;
            }

            row.IsChecked = VmTools.CheckedIds.Contains(row.LinkId);
            UpdateDeleteState();
        }

        private void UpdateDeleteState()
        {
            var count = VmTools.CheckedIds.Count;
            DeleteSelectedBtn.IsEnabled = count > 0;
            SelectionInfoText.SetText(count > 0 ? Loc.K("tools.checkedCount", count) : LocValue.Empty);
        }

        /// <summary>临时提示（不打断操作）：显示一句短提示后恢复勾选计数。</summary>
        private async Task FlashSelectionInfo(LocValue message)
        {
            SelectionInfoText.SetText(message);
            await Task.Delay(1600);
            if (DetailPanel.Visibility == Visibility.Visible) UpdateDeleteState();
        }

        private async void DeleteSelected_Click(object sender, RoutedEventArgs e)
        {
            if (VmTools.CheckedIds.Count == 0) return;

            // 编辑优先：勾选里含正在编辑的链接 → 直接提示，不弹确认框（避免"确认了却删不掉"）。
            if (VmTools.BlockDestructiveIfEditing(VmTools.CheckedIds)) return;

            var count = VmTools.CheckedIds.Count;
            if (!ConfirmDialog.Show(Loc.T("tools.dedup.deleteTitle"), Loc.T("tools.dedup.deleteConfirm", count), Loc.T("common.delete"), "delete-outline"))
                return;

            try
            {
                // 业务在 VM：单命令批量入站 → 局部更新当前组（不全库重读、不重跑查重）
                var rest = await VmTools.DeleteCheckedAsync();
                ApplyDeletionResult(rest);
            }
            catch (Exception ex)
            {
                LpLog.Error("duplicate deletion failed", ex);
                await FlashSelectionInfo(Loc.K("tools.dedup.deleteFailed"));
            }
        }

        /// <summary>
        /// 删除完成后的视图投影（VM 已把当前组局部更新；这里只换行集/摘要，不重扫）。
        /// 返回 null = 组已不再重复：回主表；否则就地刷新明细。
        /// </summary>
        private void ApplyDeletionResult(List<LinkDto>? rest)
        {
            _groups = VmTools.Groups;
            if (rest != null)
            {
                _detailLinks = BuildDetailRows(rest);
                DetailHintText.SetText(Loc.K("tools.dedup.groupHint", rest.Count));
                ApplyDetailSort();
                VmTools.DetailSelection.Set(VmTools.DetailSelection.Ids.Where(id => rest.Any(l => l.LinkId == id)));
                ApplyDetailSelectionProjection();
                UpdateDeleteState();
                ApplyPaneSort();
                PaneSubtitle.SetText(Loc.K("tools.dedup.foundSubtitle", _groups.Count, _groups.Sum(g => g.Count)));
                return;
            }

            PaneTable.ItemsSource = null;
            ApplyPaneSort();
            if (_groups.Count == 0)
            {
                PaneTable.EmptyContent = BuildState("content-duplicate", Loc.K("tools.dedup.noneTitle"),
                    Loc.K("tools.dedup.noneHint"));
                PaneSubtitle.SetText(Loc.K("tools.dedup.none"));
            }
            else
            {
                PaneTable.EmptyContent = null!;
                PaneSubtitle.SetText(Loc.K("tools.dedup.foundSubtitle", _groups.Count, _groups.Sum(g => g.Count)));
            }
            GoBackToList();
        }

        // ============================================================
        // —— ID 跳转（统一走 IContentLocator 组件；定位在 ToolsViewModel.JumpAsync） ——
        // ============================================================

        private void ResetIdJumpForm()
        {
            IdInput.Text = string.Empty;
            HideJumpHint();
        }

        private void HideJumpHint()
        {
            JumpHintChip.Visibility = Visibility.Collapsed;
            JumpHintText.Text = string.Empty;
        }

        private void ShowJumpHint(LocValue message)
        {
            JumpHintText.SetText(message);
            JumpHintChip.Visibility = Visibility.Visible;
        }

        private void Jump_Click(object sender, RoutedEventArgs e) => _ = JumpFromInputAsync();

        private async Task JumpFromInputAsync()
        {
            HideJumpHint();
            var id = IdInput.Text.Trim();
            if (string.IsNullOrEmpty(id))
            {
                ShowJumpHint(Loc.K("tools.id.prompt"));
                return;
            }

            var result = await JumpToIdAsync(id);
            if (result.IsSuccess)
            {
                // 已切到浏览页并选中目标：清空输入，避免下次进来还残留旧 ID
                ResetIdJumpForm();
            }
        }

        /// <summary>跳转统一入口：定位在 <see cref="ToolsViewModel.JumpAsync"/>（组件），本方法只负责提示渲染。</summary>
        private async Task<LocateResult> JumpToIdAsync(string id)
        {
            var result = await VmTools.JumpAsync(id);
            if (!result.IsSuccess && !result.UserNotified && DetailPanel.Visibility != Visibility.Visible)
            {
                ShowJumpHint(result.Message ?? result.Status switch
                {
                    LocateStatus.NotFound => Loc.K("locate.notFoundId"),
                    LocateStatus.RowMissing => Loc.K("locate.rowMissing"),
                    LocateStatus.Failed => Loc.K("locate.failedRetry"),
                    _ => Loc.K("locate.incomplete"),
                });
            }
            return result;
        }

}
}
