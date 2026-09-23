using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
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
                    SortKey = r => (IComparable)((DedupGroupRow)r).Url,
                    CellFactory = r =>
                    {
                        var cell = new TextBlock
                        {
                            Text = ((DedupGroupRow)r).Url,
                            FontSize = 12,
                            VerticalAlignment = VerticalAlignment.Center,
                            TextTrimming = TextTrimming.CharacterEllipsis
                        };
                        // 字体/文字色一律走**资源引用**：一次性取值（FindResource 后赋值）会在
                        // 换主题或换字体后停在旧值上 —— 与表头底色同一根因。
                        cell.SetResourceReference(TextElement.FontFamilyProperty, Theming.Tokens.AppTokens.FontMono);
                        cell.SetResourceReference(TextElement.ForegroundProperty, "App.Text.Primary");
                        return cell;
                    }
                },
                new DataTableColumn
                {
                    Field = "count", LabelKey = "ui.noun.duplicateCount", Width = 90,
                    SortKey = r => (IComparable)((DedupGroupRow)r).Count,
                    CellFactory = r =>
                    {
                        var countText = new TextBlock
                        {
                            Text = $"×{((DedupGroupRow)r).Count}",
                            FontSize = 12, FontWeight = FontWeights.SemiBold
                        };
                        countText.SetResourceReference(TextElement.ForegroundProperty, "App.Text.OnContainer");
                        var chip = new Border
                        {
                            CornerRadius = new CornerRadius(8),
                            Padding = new Thickness(8, 2, 8, 2),
                            HorizontalAlignment = HorizontalAlignment.Left,
                            Child = countText
                        };
                        chip.SetResourceReference(Border.BackgroundProperty, "App.Accent.Container");
                        return chip;
                    }
                },
                new DataTableColumn
                {
                    Field = "locations", LabelKey = "ui.noun.locatedIn", Width = -2,
                    SortKey = r => (IComparable)((DedupGroupRow)r).LocationsSummary.Resolve(),
                    CellFactory = r => TextCell(((DedupGroupRow)r).LocationsSummary)
                },
            };

            PaneTable.RowClick += (_, item) => EnterDetail((DedupGroupRow)item);
            PaneTable.RowDoubleClick += (_, item) => EnterDetail((DedupGroupRow)item);
            ShowDedupPlaceholder();
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

            // 内容未变（重扫/切回常见）→ **不重设 ItemsSource**：工厂模式重设 = 整表重建（同步主线程）
            if (!DedupGroupRow.SameSequence(previous, groups)) PaneTable.ItemsSource = groups;
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
                    Field = "check", LabelKey = "", Width = 44,
                    CellFactory = BuildCheckCell
                },
                new DataTableColumn
                {
                    Field = "title", LabelKey = "ui.noun.name", Width = -1,
                    SortKey = r => (IComparable)(string.IsNullOrEmpty(((LinkDto)r).Title) ? ((LinkDto)r).Url : ((LinkDto)r).Title),
                    CellFactory = r => BuildNameCell((LinkDto)r)
                },
                new DataTableColumn
                {
                    // 与搜索页/智能列表同口径：路径最宽，右侧时间列压缩到刚好够用
                    Field = "path", LabelKey = "ui.noun.location", Width = -3,
                    SortKey = r => (IComparable)VmTools.ResolvePath((LinkDto)r).Resolve(),
                    CellFactory = r =>
                    {
                        return TextCell(VmTools.ResolvePath((LinkDto)r));
                    }
                },
                new DataTableColumn
                {
                    Field = "updated_at", LabelKey = "ui.noun.updatedAt", Width = 130,
                    SortKey = r => (IComparable)((LinkDto)r).UpdatedAt,
                    CellFactory = r => TextCell(UiClock.Text(((LinkDto)r).UpdatedAt.ToLocalTime()))
                },
                new DataTableColumn
                {
                    Field = "last_visited_at", LabelKey = "ui.noun.lastVisited", Width = 130,
                    SortKey = r => (IComparable)(((LinkDto)r).LastVisitedAt ?? DateTime.MinValue),
                    CellFactory = r => ((LinkDto)r).LastVisitedAt is { } visited
                        ? TextCell(UiClock.Text(visited.ToLocalTime()))
                        : TextCell(Loc.K("clock.never"))
                },
                new DataTableColumn
                {
                    Field = "visit_count", LabelKey = "ui.noun.visitCount", Width = 84,
                    SortKey = r => (IComparable)((LinkDto)r).VisitCount,
                    CellFactory = r => TextCell(Loc.K("count.viewsN", ((LinkDto)r).VisitCount))
                },
                // 重复组明细**没有**「操作」列 / 行内跳转按钮：
                // 对比页是"看差异"的只读视图；「跳转」（顶部 URL 组药丸 / 右栏图标钮）走定位组件 IContentLocator
                // ——与「ID 跳转」工具同一套语义（进目录 + 选中行），本页不自带定位算法。
            };

            // 外部托管选中（SelectionEnabled=False）：单选中由 ToolsViewModel.DetailSelection（共享核心）承载，
            // 行绘制 + 右栏统一经 ApplyDetailSelectionProjection 一处投影
            DetailTable.RowClick += (_, item) => VmTools.DetailSelection.SelectSingle(((LinkDto)item).LinkId);
        }

        /// <summary>当前明细行的数据镜像（EnterDetail 赋值；选中投影 / ↑↓ 顺序 / 右栏都读它）。</summary>
        private List<LinkDto> _detailLinks = new();

        /// <summary>
        /// 展开明细：组状态记录在 VM（EnterGroup），本方法只做视觉切换。
        /// <paramref name="preserveSelection"/> = true 时**保留仍在新结果里的选中**（F5 重查 / 入口对齐重进）；
        /// 换组进入时为 false（清选中）。
        /// </summary>
        private void EnterDetail(DedupGroupRow row, bool preserveSelection = false)
        {
            var previous = preserveSelection ? VmTools.DetailSelection.Ids.ToList() : null;

            VmTools.EnterGroup(row);   // 进组即清选中（换组语义）；需要保留的由下面按新结果重新落回
            _detailLinks = row.Links;

            DetailUrlText.Text = row.Url;
            DetailHintText.SetText(Loc.K("tools.dedup.groupHint", row.Count));
            DetailTable.ItemsSource = null;
            DetailTable.ItemsSource = row.Links;
            DetailTable.EmptyContent = null!;

            if (previous is { Count: > 0 })
                VmTools.DetailSelection.Set(previous.Where(id => row.Links.Any(l => l.LinkId == id)));

            ApplyDetailSelectionProjection();   // 行重建后同步行绘制与右栏（保留的选中在此重投）
            UpdateDeleteState();
            MainPanel.Visibility = Visibility.Collapsed;
            DetailPanel.Visibility = Visibility.Visible;
            // 焦点收回**页面根**（原先 Focus 明细面板容器：容器拿到键盘焦点后会画一条原生焦点虚线框，
            // 表现为明细页出现黑虚线）。焦点在页内 = 快捷键照常路由（ShortcutHost 不变式）。
            PageFocus.Restore(this);
        }

        private void GoBackToList()
        {
            VmTools.LeaveGroup();
            _detailLinks = new List<LinkDto>();
            DetailTable.ItemsSource = null;
            DetailPanel.Visibility = Visibility.Collapsed;
            MainPanel.Visibility = Visibility.Visible;
        }

        private void DetailBack_Click(object sender, RoutedEventArgs e) => GoBackToList();

        private void CopyUrl_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrEmpty(VmTools.CurrentGroupUrl))
            {
                try { Clipboard.SetText(VmTools.CurrentGroupUrl); } catch { }
            }
        }

        /// <summary>勾选单元：MD3 圆形勾选（选中 = Primary 实心 + 白勾，未选 = 描边圆）。守卫规则在 VM。</summary>
        private FrameworkElement BuildCheckCell(object data)
        {
            var link = (LinkDto)data;
            var checkedNow = VmTools.CheckedIds.Contains(link.LinkId);

            var outline = new Border
            {
                Width = 20, Height = 20, CornerRadius = new CornerRadius(10),
                BorderThickness = new Thickness(1.6),
                Background = Brushes.Transparent,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            outline.SetResourceReference(Border.BorderBrushProperty, "App.Text.Secondary");
            var fill = new Border
            {
                Width = 20, Height = 20, CornerRadius = new CornerRadius(10),
                Visibility = checkedNow ? Visibility.Visible : Visibility.Collapsed,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Child = new Path
                {
                    Data = Geometry.Parse("M1,4.6 L3.6,7.1 L8,1.8"),
                    Stroke = Brushes.White,
                    StrokeThickness = 1.7,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round,
                    StrokeLineJoin = PenLineJoin.Round,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };
            fill.SetResourceReference(Border.BackgroundProperty, "App.Accent.Fill");

            var host = new Grid { Width = 22, Height = 22 };
            host.Children.Add(outline);
            host.Children.Add(fill);

            var deleteTip = Loc.T("tools.dedup.deleteChecked");
            var button = new Button
            {
                Content = host,
                Width = 30, Height = 26,
                Cursor = Cursors.Hand,
                FocusVisualStyle = null,
                Style = (Style)FindResource("RowIconButton"),
                ToolTip = deleteTip
            };
            button.Click += async (_, _) =>
            {
                if (!VmTools.ToggleChecked(link.LinkId))
                {
                    await FlashSelectionInfo(Loc.K("tools.dedup.keepOne"));
                    return;
                }

                var nowChecked = VmTools.CheckedIds.Contains(link.LinkId);
                fill.Visibility = nowChecked ? Visibility.Visible : Visibility.Collapsed;
                UpdateDeleteState();
            };

            return button;
        }

        private FrameworkElement BuildNameCell(LinkDto link)
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

            var iconGrid = new Grid { Width = 18, Height = 18, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
            var faviconBmp = FaviconService.LoadFromCache(link.FaviconUrl);
            var faviconImg = new Image
            {
                Source = faviconBmp,
                Stretch = Stretch.Uniform,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            RenderOptions.SetBitmapScalingMode(faviconImg, BitmapScalingMode.HighQuality);
            if (faviconBmp == null) faviconImg.Visibility = Visibility.Collapsed;

            var earthIcon = new M3Icon
            {
                Kind = "earth", Width = 16, Height = 16,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            earthIcon.SetResourceReference(TextElement.ForegroundProperty, "App.Text.Muted");
            if (faviconBmp != null) earthIcon.Visibility = Visibility.Collapsed;
            iconGrid.Children.Add(faviconImg);
            iconGrid.Children.Add(earthIcon);

            if (!string.IsNullOrWhiteSpace(link.FaviconUrl) && faviconBmp == null)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await FaviconService.PrefetchAndCacheAsync(link.FaviconUrl);
                        var cached = FaviconService.LoadFromCache(link.FaviconUrl);
                        if (cached != null)
                        {
                            Dispatcher.Invoke(() =>
                            {
                                faviconImg.Source = cached;
                                faviconImg.Visibility = Visibility.Visible;
                                earthIcon.Visibility = Visibility.Collapsed;
                            });
                        }
                    }
                    catch { }
                });
            }

            var textStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var nameText = new TextBlock
            {
                FontSize = 13.5, FontWeight = FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            // 标题是用户数据；没有标题时画「无标题」（文案值，跟语言走）
            if (string.IsNullOrWhiteSpace(link.Title)) nameText.SetText(Loc.K("tools.untitled"));
            else nameText.Text = link.Title;
            nameText.SetResourceReference(TextElement.ForegroundProperty, "App.Text.Primary");
            textStack.Children.Add(nameText);
            var urlText = new TextBlock
            {
                Text = link.Url,
                FontSize = 11.5, Margin = new Thickness(0, 3, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            urlText.SetResourceReference(TextElement.ForegroundProperty, "App.Text.Secondary");
            textStack.Children.Add(urlText);

            panel.Children.Add(iconGrid);
            panel.Children.Add(textStack);
            return panel;
        }

        /// <summary>
        /// 数据单元格（用户数据：URL / 标题 / 时间戳）。
        /// ⚠️ 走 <see cref="LocValue.Literal"/> 而不是 <c>LocValue.Of</c>：后者把这串当**文案键**查表，
        /// 查不到就画出 <c>⟨…⟩</c> 缺键哨兵——时间戳与 URL 不是键，那样渲染出来的日期是错的。
        /// </summary>
        private TextBlock TextCell(string text) => TextCell(LocValue.Literal(text));

        /// <summary>
        /// 两个长度形态的单元格（日期/计数这类结构化列：放不下时换短式，不缩字号）。
        /// 参数用全限定名：UIKit 的代码写文字门面 <c>LinkPocket.Views.LocText</c> 与本类型同名，
        /// 而本文件两个命名空间都 using 了。
        /// </summary>
        private TextBlock TextCell(LinkPocket.I18n.LocText text)
            => LocFitResolver.BuildCell(text);

        /// <summary>文案单元格（键 + 参数；语言一变自己重算）。</summary>
        private TextBlock TextCell(LocValue text)
        {
            var cell = new TextBlock
            {
                FontSize = 12.5,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            cell.SetText(text);
            cell.SetResourceReference(TextElement.ForegroundProperty, "App.Text.Secondary");
            return cell;
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

            var count = VmTools.CheckedIds.Count;
            if (!ConfirmDialog.Show(Loc.T("tools.dedup.deleteTitle"), Loc.T("tools.dedup.deleteConfirm", count), Loc.T("common.delete"), "delete-outline"))
                return;

            try
            {
                // 业务在 VM：逐条移入回收站 → 刷新目录树计数 → 重算当前组
                var rest = await VmTools.DeleteCheckedAsync();
                if (rest != null)
                {
                    DetailHintText.SetText(Loc.K("tools.dedup.groupHint", rest.Count));
                    DetailTable.ItemsSource = null;
                    DetailTable.ItemsSource = rest;
                    UpdateDeleteState();
                }
                else
                {
                    GoBackToList();
                    await RunDedupAsync();
                }
            }
            catch (Exception ex)
            {
                LpLog.Error("duplicate deletion failed", ex);
                await FlashSelectionInfo(Loc.K("tools.dedup.deleteFailed"));
            }
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
            if (!result.IsSuccess && DetailPanel.Visibility != Visibility.Visible)
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
