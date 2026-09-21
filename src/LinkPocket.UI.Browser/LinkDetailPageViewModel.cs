using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows.Input;
using LinkPocket.Contracts;
using LinkPocket.Services;
using LinkPocket.I18n;

namespace LinkPocket.ViewModels;

/// <summary>
/// 浏览模块「链接详情页」视图模型：**数据与渲染全部交给共享 <see cref="LinkDetailPaneModel"/> 与
/// <c>Views.LinkDetailPane</c>**（与回收站只读详情页同一份界面），本类只负责加载与动作
/// （打开网站 / 编辑 / 删除 / 返回）——绝不自绘第二份详情界面。
/// 打开时记录一次访问（与链接页 ShowDetail 一致）；「打开网站」为显式按钮。
/// </summary>
public class LinkDetailPageViewModel : LinkDetailPaneModel
{
    /// <summary>引擎客户端门面（分层 API 面，由组合根注入）。</summary>
    private readonly EngineClient _client;

    private readonly BrowserViewModel _host;
    private string? _linkId;
    private int _generation;

    public LinkDetailPageViewModel(EngineClient client, BrowserViewModel host)
    {
        _client = client;
        _host = host;

        // 动作面（共享面内声明本页入口）：主按钮 = 打开网站；铅笔 = 编辑；垃圾桶 = 删除
        OpenLabel = Loc.K("common.open");
        OpenToolTip = Loc.K("detail.openInBrowser");
        OpenIconKind = "open-in-new";
        EditLabel = Loc.K("common.edit");
        DeleteActionLabel = Loc.K("common.delete");

        BackCommand = new RelayCommand(() => _ = BackAsync());
        // ⚠️ 不设 CanExecute：详情页打开的瞬间数据还在异步加载（Url 尚空），
        // 若按 CanExecute 禁用，按钮会先以 0.4 透明度渲染、加载完又突然恢复 → 肉眼可见的闪烁。
        // 命令内部对空 URL 有守卫，提前点击只是无操作。
        OpenWebsiteCommand = new RelayCommand(() => OpenWebsite());
        CopyUrlCommand = new RelayCommand(CopyUrl, () => !string.IsNullOrEmpty(Url));
        EditCommand = new RelayCommand(Edit, () => _linkId != null);
        DeleteCommand = new RelayCommand(() => _ = DeleteAsync(), () => _linkId != null);
        OpenCommand = OpenWebsiteCommand;   // 共享面主按钮 = 打开网站
        RenameCommand = EditCommand;        // 铅笔 = 编辑
        RaiseActionChanged();
    }

    /// <summary>编辑（按钮实例；同时挂到共享面的铅笔位）。</summary>
    public ICommand EditCommand { get; }

    /// <summary>打开详情页：加载链接数据并记录一次访问。</summary>
    public Task LoadAsync(string linkId) => LoadCoreAsync(linkId, recordVisit: true);

    /// <summary>编辑器保存后静默刷新（不重复记录访问）。</summary>
    public Task ReloadIfOpenAsync() => _linkId == null ? Task.CompletedTask : LoadCoreAsync(_linkId, recordVisit: false);

    private async Task LoadCoreAsync(string linkId, bool recordVisit)
    {
        var gen = ++_generation;
        _linkId = linkId;
        try
        {
            // 打开详情 = 一次查看。**必须先记账、再读取展示数据**，否则页面显示的是"上一次"的
            // 「最后查看 / 查看次数」，用户要退出详情页（Back 会重新读取）才看到 +1。
            // 另外：这里必须 await，不能丢到 Task.Run —— 后台线程和 UI 线程并发使用同一个
            // EF DbContext 是非线程安全的（UI 线程同时可能因事件防抖在刷新列表）。
            if (recordVisit)
            {
                try { await _client.LinkVisitRecordAsync(linkId); }
                catch { /* 链接可能已不存在：交给下面的读取判定 */ }
                if (gen != _generation) return;
            }

            LinkDto? link;
            try { link = await _client.LinkGetAsync(linkId); }   // 单点查询；不存在抛 EntityNotFound → 归一 null 走既有空档处理
            catch (EngineException) { link = null; }
            if (link == null || gen != _generation) { Close(); return; }

            // favicon 磁盘读取+解码移出 UI 线程（与 LinkEditor 同口径；页面渲染不因图标卡顿）
            var faviconUrl = link.FaviconUrl;
            var favicon = string.IsNullOrEmpty(faviconUrl)
                ? null
                : await Task.Run(() => FaviconService.LoadFromCache(faviconUrl));
            if (gen != _generation) return;

            SetContent(link.Title, link.Url, favicon, link.Description ?? "", BuildRows(link));
        }
        catch (Exception ex)
        {
            // 加载失败反馈：不留下永远空白的详情页。闭页前可见可读
            LpLog.Error("链接详情页加载失败", ex);
            SetContent(Loc.T("status.loadFailed"), string.Empty, null,
                "读取链接数据出错，请返回列表重试。\n" + ex.Message, Array.Empty<DetailSidebarRow>());
        }
    }

    /// <summary>信息行（与浏览页详情页字段一致；ID 行附复制按钮）。</summary>
    private IReadOnlyList<DetailSidebarRow> BuildRows(LinkDto link) => new List<DetailSidebarRow>
    {
        new() { IconKind = "folder-outline", LabelKey = "ui.noun.location", Value = _host.GetFolderPathDisplay(link.ListId) },
        new() { IconKind = "refresh", LabelKey = "ui.noun.updatedAt", Value = Fmt(link.UpdatedAt) },
        new() { IconKind = "history", LabelKey = "ui.noun.lastVisited",
                Value = link.LastVisitedAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "从未" },
        new() { IconKind = "chart-line", LabelKey = "ui.noun.viewTotal", Value = $"{link.VisitCount} 次" },
        new() { IconKind = "plus-circle-outline", LabelKey = "ui.noun.createdAt", Value = Fmt(link.CreatedAt) },
        new() { IconKind = "fingerprint", LabelKey = "ui.noun.id", Value = link.LinkId, IsMono = true,
                CopyCommand = new RelayCommand(() => CopyIdValue(link.LinkId)), CopyToolTip = "复制 ID" },
    };

    private static string Fmt(DateTime dt) => dt.Year <= 1 ? "—" : dt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");

    private void OpenWebsite() => _ = OpenWebsiteAsync();

    /// <summary>打开网站访问记账防重入：连点时不并发记账/重读。</summary>
    private bool _visitBusy;

    /// <summary>打开网站 = 又一次查看：先记账、再重新读取，页面上的统计立刻反映这一次。</summary>
    private async Task OpenWebsiteAsync()
    {
        if (string.IsNullOrEmpty(Url) || _linkId == null) return;   // 检查在打开前：无目标就不启动浏览器
        if (_visitBusy) return;   // 前一次访问记账进行中：连点跳过重复记账
        _visitBusy = true;
        try
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Url) { UseShellExecute = true });
            }
            catch (Exception ex) { LpLog.Error("打开网站失败（静默返回，页面保持）", ex); }   // 观测面：失败留痕

            try
            {
                await _client.LinkVisitRecordAsync(_linkId);
                await ReloadIfOpenAsync(); // 不重复计数，只把「含本次」的最新统计取回来
            }
            catch (Exception ex)
            {
                LpLog.Error("记录访问失败", ex);
            }
        }
        finally
        {
            _visitBusy = false;
        }
    }

    private void CopyUrl()
    {
        try
        {
            if (string.IsNullOrEmpty(Url)) return;
            System.Windows.Clipboard.SetText(Url);
            _host.StatusText = Loc.T("status.linkCopied");   // 复制反馈
        }
        catch { }
    }

    private void CopyIdValue(string id)
    {
        try
        {
            if (string.IsNullOrEmpty(id)) return;
            System.Windows.Clipboard.SetText(id);
            _host.StatusText = Loc.T("status.idCopied");   // 复制反馈
        }
        catch { }
    }

    private void Edit()
    {
        if (_linkId == null) return;
        _host.OpenEditorForEdit(_linkId); // 整页编辑器覆盖在详情页之上；保存后经 ReloadIfOpenAsync 回写
    }

    /// <summary>删除链接（移入回收站）：与右侧栏删除行为一致，删除后关闭详情页并刷新列表。</summary>
    private async Task DeleteAsync()
    {
        if (_linkId == null) return;
        // 删除确认唯一入口（行为契约 §1.3）：详情页删除与浏览页共用 ConfirmDialog 文案；
        // 优先走宿主对话框端口，无端口退化 ConfirmDialog 直用
        var dlg = _host.Dialogs;
        if (dlg != null)
        {
            if (!dlg.Confirm("删除链接", $"将链接「{Title}」移入回收站吗？")) return;
        }
        else if (!Views.ConfirmDialog.Show("删除链接", $"将链接「{Title}」移入回收站吗？", Loc.T("common.delete")))
        {
            return;
        }
        try
        {
            await _client.LinkTrashAsync(_linkId);
            Close();
            await _host.RefreshAsync();
        }
        catch (Exception ex)
        {
            LpLog.Error("链接详情页删除失败", ex);
        }
    }

    /// <summary>
    /// 「返回」列表：关闭详情页并在原目录原地刷新（保留原选中项）。
    /// 详情页打开时已在内核记入一次「查看」——链接与上级文件夹的
    /// 「最后查看 / 查看次数」都已沿父链刷新，这里让它们立刻反映到列表与右侧栏；
    /// 「最后更新」不受查看影响，仍严格由内容变动驱动。
    /// </summary>
    private async Task BackAsync()
    {
        // 从详情页返回：把该链接重新纳入选中集合（唯一事实来源）再原地刷新，投影自动恢复两栏高亮
        if (!string.IsNullOrEmpty(_linkId)) _host.RestoreSelection(_linkId);
        Close();
        await _host.RefreshPreservingSelectionAsync();
    }

    public void Close()
    {
        _linkId = null;
        _generation++;
        _host.CloseDetailPage();
    }
}
