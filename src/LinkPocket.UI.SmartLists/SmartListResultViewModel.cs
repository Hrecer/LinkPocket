using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Input;
using LinkPocket.Contracts;
using LinkPocket.Models;
using LinkPocket.Services;
using LinkPocket.I18n;

namespace LinkPocket.ViewModels
{
    /// <summary>
    /// 智能列表结果页 ViewModel（MVVM）：数据装载（LoadAsync）之外，
    /// 行选中态、右侧详情栏与页面动作命令（详情/打开网站/删除）也在此——
    /// 视图（Views/SmartListsPage）只负责表格装配与渲染。
    /// 动作语义与搜索页同一套：「详情/编辑」= 进入浏览页链接详情页；
    /// 删除 = 确认后移入回收站并重载当前列表（Reloaded 事件驱动视图重绑）。
    /// 选中由全站共享的 <see cref="ListSelection"/> 承载（本页为**单选中**：结果页 = 只读页，
    /// 可读、可选、不可操作——不引入删除/Ctrl+A 等操作键）。
    /// </summary>
    public class SmartListResultViewModel : INotifyPropertyChanged
    {
        /// <summary>引擎客户端门面（分层 API 面，由组合根注入）。</summary>
        private readonly EngineClient _client;
        private readonly INavigationService? _navigation;
        private readonly IDialogService? _dialogs;
        private readonly IContentLocator? _locator;
        private readonly Func<string?, LocValue> _resolveFolderPath;
        private bool _isDeleting;

        /// <summary>删除进行中：命令可用性与防重入共用同一开关（删除过程中禁用再次触发）。</summary>
        public bool IsDeleting
        {
            get => _isDeleting;
            private set
            {
                if (_isDeleting == value) return;
                _isDeleting = value;
                OnPropertyChanged();
                CommandRefresh.Request();   // 立即重估（选中变化 → 命令可用性同步更新）
            }
        }

        private readonly string _listId;
        private bool _isLoading;
        private bool _hasData;
        private ObservableCollection<LinkItem> _items = new();
        private LocValue _title = Loc.K("smartlists.title");
        private LocValue _emptyMessage = Loc.K("smartlists.empty");
        private int _totalCount;

        public event PropertyChangedEventHandler? PropertyChanged;

        /// <summary>删除重载完成后通知视图重绑表格（排序复位 + 行集替换 + 清表格选中）。</summary>
        public event EventHandler? Reloaded;

        /// <summary>宿主（SmartListViewModel.RefreshCurrentAsync 等）在跨页事件刷新后调用，
        /// 触发视图重绑——与删除后重载同一出口（事件只能在声明类内触发，故提供公开方法）。</summary>
        public void NotifyReloaded() => Reloaded?.Invoke(this, EventArgs.Empty);

        public string ListId => _listId;

        /// <summary>结果页标题（键；语言一变自己重算）。</summary>
        public LocValue Title
        {
            get => _title;
            set { if (!_title.Equals(value)) { _title = value; OnPropertyChanged(); } }
        }

        private LocValue _subtitle;
        /// <summary>列表语义的灰色提示（结果页标题右侧展示，与入口卡片副标题同一数据源）。</summary>
        public LocValue Subtitle
        {
            get => _subtitle;
            set { if (!_subtitle.Equals(value)) { _subtitle = value; OnPropertyChanged(); } }
        }

        public bool IsLoading
        {
            get => _isLoading;
            set { _isLoading = value; OnPropertyChanged(); }
        }

        public bool HasData
        {
            get => _hasData;
            set { _hasData = value; OnPropertyChanged(); }
        }

        public ObservableCollection<LinkItem> Items
        {
            get => _items;
            set { _items = value; OnPropertyChanged(); }
        }

        /// <summary>空态文案（键 + 参数；语言一变自己重算）。</summary>
        public LocValue EmptyMessage
        {
            get => _emptyMessage;
            set { if (!_emptyMessage.Equals(value)) { _emptyMessage = value; OnPropertyChanged(); } }
        }

        public int TotalCount
        {
            get => _totalCount;
            set
            {
                if (_totalCount == value) return;
                _totalCount = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(TotalCountText));
            }
        }

        /// <summary>
        /// 「共 N 项」整句（含变量的句子由模型出文本，语序随语言走）。
        /// </summary>
        /// <remarks>
        /// ⚠️ <b>交出去的是文案值（键 + 参数），不是取好词的字符串</b>：返回 <c>string</c> 会把这句话
        /// <b>冻结在读取那一刻的语言</b>上（绑定只认"属性值变了没有"，而普通属性不发通知），
        /// 换语言后屏幕上留着上一种语言的计数句。界面侧一律写 <c>{loc:Value TotalCountText}</c>。
        /// </remarks>
        public LocValue TotalCountText => Loc.K("smartlists.count.total", _totalCount);

        // —— 选中态与详情栏（MVVM 自页面下沉；共享 ListSelection 核心，单选中） ——

        /// <summary>**选中核心（全站共享实现）**：结果页为单选中。</summary>
        public ListSelection Selection { get; } = new();

        /// <summary>视图注入：当前结果的**视觉顺序**（共享表格的当前排序）——↑/↓ 据此计算。</summary>
        public Func<IReadOnlyList<string>>? OrderProvider { get; set; }

        /// <summary>把某行滚入视口（移动选中后由视图订阅执行）。</summary>
        public event EventHandler<LinkItem>? FocusRowRequested;

        /// <summary>当前选中行（按结果集顺序取首项；本页恒为单选中）。</summary>
        private LinkItem? PrimarySelected =>
            Items.FirstOrDefault(r => Selection.Contains(r.LinkId));

        /// <summary>是否存在选中（命令 CanExecute 用）。</summary>
        public bool HasSelection => Selection.HasAny;

        /// <summary>右侧详情栏（复用搜索页同一 DetailSidebar 数据契约）。</summary>
        public SearchDetailsViewModel Details { get; } = new();

        public SmartListResultViewModel(EngineClient client, string listId, LocValue title,
            INavigationService? navigation, IDialogService? dialogs, Func<string?, LocValue> resolveFolderPath,
            IContentLocator? locator = null)
        {
            _client = client;
            _listId = listId;
            Title = title;
            _navigation = navigation;
            _dialogs = dialogs;
            _resolveFolderPath = resolveFolderPath;
            _locator = locator;

            Selection.Changed += OnSelectionChanged;

            // 「详情」= 打开浏览页的链接详情页（Enter 同此命令）
            OpenInBrowserCommand = new RelayCommand(
                () => { if (PrimarySelected is { } item) _navigation?.OpenLinkInBrowser(item.LinkId); },
                () => Selection.HasAny && !IsDeleting);
            // 「跳转」= 进浏览页对应目录并选中该行（经定位组件；与「详情」互不替代）。
            // 本页为单选中，故"有选中"即"有唯一目标"。
            JumpCommand = new RelayCommand(() => _ = JumpAsync(), () => Selection.HasAny && !IsDeleting);
            OpenWebsiteCommand = new RelayCommand(() => _ = OpenSelectedWebsiteAsync(), () => Selection.HasAny && !IsDeleting);
            // 删除过程中（IsDeleting）禁用删除与其余行动作 —— 防重入不再只靠方法内 if
            DeleteCommand = new RelayCommand(() => _ = DeleteSelectedAsync(), () => Selection.HasAny && !IsDeleting);
            // 只读页的键位延伸（↑/↓/End/F5；不引入任何会改数据的键）
            MoveSelectionCommand = new RelayCommand<object?>(p => MoveSelection(ParseDirection(p)));
            SelectLastCommand = new RelayCommand(SelectLast);
            // F5 = 重新查询（**导航加载口径**）：亮加载遮罩、结束后播行入场动画 + 保留选中
            RefreshCommand = new RelayCommand(() => _ = ReloadAsync(navigating: true));
            // 点空白清选中（BlankClick 挂在结果区；与 Esc 分层共用同一"出口"语义）
            ClearSelectionCommand = new RelayCommand(() => Selection.Clear());

            Details.OpenCommand = OpenInBrowserCommand;
            Details.RenameCommand = OpenInBrowserCommand;
            Details.OpenWebsiteCommand = OpenWebsiteCommand;
            Details.DeleteCommand = DeleteCommand;
            Details.JumpCommand = JumpCommand;   // 右栏「跳转」图标钮 = 同一条定位入口（绝不另写一份）
            // 只读结果页的右栏动作面收窄：**不显示「编辑」与「删除」两个按钮**
            // ——结果页是只读的查看面（键位集也是只读集，无 Delete/Ctrl+A）。命令仍接好（如需恢复显示位即可用）。
            Details.HideEditAndDeleteActions();
        }

        public ICommand OpenInBrowserCommand { get; }
        /// <summary>「跳转」= 进浏览页对应目录并选中该行（定位组件；与「详情」互不替代）。</summary>
        public ICommand JumpCommand { get; }
        public ICommand OpenWebsiteCommand { get; }
        public ICommand DeleteCommand { get; }
        public ICommand MoveSelectionCommand { get; }
        public ICommand SelectLastCommand { get; }
        public ICommand RefreshCommand { get; }
        public ICommand ClearSelectionCommand { get; }

        /// <summary>视图在用户点击行时调用（单选中：只读页只有"读 + 选"）。</summary>
        public void ClickItem(LinkItem item) => Selection.SelectSingle(item.LinkId);

        /// <summary>清空选中与详情栏（返回卡片页/重载列表时由视图或本类调用）。</summary>
        public void ClearSelection() => Selection.Clear();

        /// <summary>选中的**唯一投影点**：集合 → 属性通知 + 右栏（恒为单选详情态）。</summary>
        private void OnSelectionChanged()
        {
            OnPropertyChanged(nameof(HasSelection));
            var item = PrimarySelected;
            Details.UpdateFrom(item, item == null ? LocValue.Empty : _resolveFolderPath(item.ListId));
            CommandRefresh.Request();
        }

        // —— ↑/↓ / End（与浏览页主栏同一套共享语义；顺序由视图注入） ——

        private static int ParseDirection(object? p)
        {
            var s = (p as string is string str ? str : p?.ToString()) ?? string.Empty;
            return s.Contains("up") ? -1 : s.Contains("down") ? 1 : 0;
        }

        private void MoveSelection(int delta)
        {
            var target = Selection.Move(delta, OrderProvider?.Invoke() ?? Array.Empty<string>());
            FocusOn(target);
        }

        private void SelectLast()
        {
            var target = Selection.SelectLast(OrderProvider?.Invoke() ?? Array.Empty<string>());
            FocusOn(target);
        }

        private void FocusOn(string? linkId)
        {
            if (linkId == null) return;
            var item = Items.FirstOrDefault(r => r.LinkId == linkId);
            if (item != null) FocusRowRequested?.Invoke(this, item);
        }

        public async Task LoadAsync()
        {
            IsLoading = true;
            try
            {
                var limit = _listId == "most_visited" ? 20 : 100;
                List<LinkDto> links = await _client.LinkSmartListAsync(_listId, limit);

                TotalCount = links.Count;

                if (links.Count == 0)
                {
                    EmptyMessage = _listId switch
                    {
                        "recently_added" => Loc.K("smartlists.empty.added"),
                        "recently_visited" => Loc.K("smartlists.empty.visited"),
                        "recently_edited" => Loc.K("smartlists.empty.edited"),
                        "most_visited" => Loc.K("smartlists.empty.mostVisited"),
                        _ => Loc.K("smartlists.empty")
                    };
                    if (_items.Count > 0) Items = new ObservableCollection<LinkItem>();
                    HasData = false;
                    return;
                }

                var items = links.Select(LinkItem.FromDto).ToList();
                // 内容未变 → **保持集合实例不动**（视图不重绑、不整表重建）：切页进入的静默刷新常是这种情况，
                // 而整表重建（≤100 行 × 单元格，同步主线程）正是"切页偶发卡顿"的主要来源。
                // 同时不再"先清空再填"——加载期间保留旧内容，避免闪空（与"进入保内容"口径一致）。
                if (!LinkItem.SameSequence(_items, items))
                    Items = new ObservableCollection<LinkItem>(items);   // 一次性整体替换（逐条 Add 会 N 次 CollectionChanged）
                HasData = true;
            }
            finally
            {
                IsLoading = false;
            }
        }

        /// <summary>
        /// 重新查询：重拉当前列表并通知视图重绑（与删除后重载同一条出口）。
        /// <paramref name="navigating"/> = **用户发起**（F5）：亮加载遮罩、结束后播行入场动画；
        /// 删除后的重载、跨页事件刷新等一律 false（静默）——与浏览页的导航加载口径一致。
        /// 选中保留由视图重绑时统一处理（只剔除已不在结果里的 ID）。
        /// </summary>
        private async Task ReloadAsync(bool navigating = false)
        {
            if (IsLoading) return;
            if (navigating) IsNavigating = true;
            try
            {
                await LoadAsync();
            }
            catch (Exception ex)
            {
                LpLog.Error("smart list re-query failed", ex);
            }
            finally
            {
                if (navigating)
                {
                    IsNavigating = false;
                    RefreshCompleted?.Invoke(this, EventArgs.Empty);
                }
            }
            Reloaded?.Invoke(this, EventArgs.Empty);
        }

        private bool _isNavigating;

        /// <summary>
        /// 用户发起的重新查询在途（界面**加载遮罩的唯一来源**）：本页只有 F5 为 true；
        /// 删除后的重载、入口对齐刷新一律 false（不亮遮罩、不播动画）——见 BEHAVIOR-CONTRACT §1.5。
        /// </summary>
        public bool IsNavigating
        {
            get => _isNavigating;
            private set { if (_isNavigating == value) return; _isNavigating = value; OnPropertyChanged(); }
        }

        /// <summary>一次**用户发起**的重新查询（F5）结束：视图据此播行入场动画并收回焦点。</summary>
        public event EventHandler? RefreshCompleted;

        /// <summary>
        /// **「跳转」**：进浏览页对应目录并选中该行（定位组件 <see cref="IContentLocator"/>；
        /// **不是**打开详情页——后者是 <see cref="OpenInBrowserCommand"/>）。本页为单选中，故"有选中"即"有唯一目标"。
        /// 失败（目标刚被移走 / 无界面宿主 / 组件不可用）一律如实提示，绝不静默。
        /// </summary>
        private async Task JumpAsync()
        {
            var item = PrimarySelected;
            if (item == null) return;
            if (_locator == null)
            {
                LpLog.Error("jump failed: the locator component is unavailable", null);   // 观测面：失败留痕
                return;
            }

            var result = await _locator.LocateLinkAsync(item.LinkId);
            if (result.IsSuccess) return;

            _dialogs?.Alert(Loc.T("common.jump"), (result.Message ?? result.Status switch
            {
                LocateStatus.NotFound => Loc.K("locate.notFoundLink"),
                LocateStatus.RowMissing => Loc.K("locate.rowMissing"),
                LocateStatus.Failed => Loc.K("locate.failedRetry"),
                _ => Loc.K("locate.incomplete"),
            }).Resolve());
        }

        /// <summary>「打开网站」：默认浏览器打开并记录一次访问（与搜索页侧栏同口径）。</summary>
        private async Task OpenSelectedWebsiteAsync()
        {
            var item = PrimarySelected;
            if (item == null) return;
            Services.LinkLauncher.Open(item.Url);
            try
            {
                await _client.LinkVisitRecordAsync(item.LinkId);
                // 统计行原位刷新（代次校验：选中未变才写回）；记账已落库 → 本地同步 VisitCount 再渲染
                if (Selection.Count == 1 && Selection.Contains(item.LinkId))
                {
                    item.VisitCount++;
                    Details.UpdateFrom(item, _resolveFolderPath(item.ListId));
                }
            }
            catch { /* 记账失败不打断 */ }
        }

        /// <summary>删除 = 确认后移入回收站（可恢复），随后重载当前列表刷新结果。</summary>
        private async Task DeleteSelectedAsync()
        {
            if (IsDeleting) return;   // 命令可用性已拦，这里双保险（键盘路径等不经 CanExecute 时）
            var item = PrimarySelected;
            if (item == null) return;

            var name = string.IsNullOrEmpty(item.Title) ? item.Url : item.Title;
            if (_dialogs == null || !_dialogs.Confirm(Loc.T("common.title.deleteLink"), Loc.T("browser.confirm.linkToTrash", name))) return;

            IsDeleting = true;
            try
            {
                await _client.LinkTrashAsync(item.LinkId);
                ClearSelection();

                // 重载当前列表（结果集直接从 API 重拉，杜绝本地残留）
                await LoadAsync();
                Reloaded?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                LpLog.Error("smart list link deletion failed", ex);
            }
            finally
            {
                IsDeleting = false;
            }
        }

        protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
