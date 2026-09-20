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
        private readonly Func<string?, string> _resolveFolderPath;
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
                System.Windows.Input.CommandManager.InvalidateRequerySuggested();   // CanExecute 依赖（RelayCommand 走 WPF 查询）
            }
        }

        private readonly string _listId;
        private bool _isLoading;
        private bool _hasData;
        private ObservableCollection<LinkItem> _items = new();
        private string _title = "智能列表";
        private string _emptyMessage = "暂无数据";
        private int _totalCount;

        public event PropertyChangedEventHandler? PropertyChanged;

        /// <summary>删除重载完成后通知视图重绑表格（排序复位 + 行集替换 + 清表格选中）。</summary>
        public event EventHandler? Reloaded;

        /// <summary>宿主（SmartListViewModel.RefreshCurrentAsync 等）在跨页事件刷新后调用，
        /// 触发视图重绑——与删除后重载同一出口（事件只能在声明类内触发，故提供公开方法）。</summary>
        public void NotifyReloaded() => Reloaded?.Invoke(this, EventArgs.Empty);

        public string ListId => _listId;

        public string Title
        {
            get => _title;
            set { _title = value; OnPropertyChanged(); }
        }

        private string _subtitle = "";
        /// <summary>列表语义的灰色提示（结果页标题右侧展示，与入口卡片副标题同一数据源）。</summary>
        public string Subtitle
        {
            get => _subtitle;
            set { _subtitle = value; OnPropertyChanged(); }
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

        public string EmptyMessage
        {
            get => _emptyMessage;
            set { _emptyMessage = value; OnPropertyChanged(); }
        }

        public int TotalCount
        {
            get => _totalCount;
            set { _totalCount = value; OnPropertyChanged(); }
        }

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

        public SmartListResultViewModel(EngineClient client, string listId, string title,
            INavigationService? navigation, IDialogService? dialogs, Func<string?, string> resolveFolderPath)
        {
            _client = client;
            _listId = listId;
            Title = title;
            _navigation = navigation;
            _dialogs = dialogs;
            _resolveFolderPath = resolveFolderPath;

            Selection.Changed += OnSelectionChanged;

            OpenInBrowserCommand = new RelayCommand(
                () => { if (PrimarySelected is { } item) _navigation?.OpenLinkInBrowser(item.LinkId); },
                () => Selection.HasAny && !IsDeleting);
            OpenWebsiteCommand = new RelayCommand(() => _ = OpenSelectedWebsiteAsync(), () => Selection.HasAny && !IsDeleting);
            // 删除过程中（IsDeleting）禁用删除与其余行动作 —— 防重入不再只靠方法内 if（#8/9）
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
        }

        public ICommand OpenInBrowserCommand { get; }
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
            Details.UpdateFrom(item, item == null ? "" : _resolveFolderPath(item.ListId));
            System.Windows.Input.CommandManager.InvalidateRequerySuggested();
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
            HasData = false;
            Items.Clear();

            try
            {
                var limit = _listId == "most_visited" ? 20 : 100;
                List<LinkDto> links = await _client.LinkSmartListAsync(_listId, limit);

                TotalCount = links.Count;

                if (links.Count == 0)
                {
                    EmptyMessage = _listId switch
                    {
                        "recently_added" => "最近 7 天没有添加新书签",
                        "recently_visited" => "最近 7 天没有访问过书签",
                        "recently_edited" => "最近 7 天没有修改过书签",
                        "most_visited" => "暂无访问记录",
                        _ => "暂无数据"
                    };
                    return;
                }

                var items = links.Select(LinkItem.FromDto).ToList();
                // 一次性整体替换（#6）：逐条 Add 会触发 N 次 CollectionChanged，重排行 N 次
                Items = new ObservableCollection<LinkItem>(items);
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
                Logger.Error("智能列表重新查询失败", ex);
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

        /// <summary>「打开网站」：默认浏览器打开并记录一次访问（与搜索页侧栏同口径）。</summary>
        private async Task OpenSelectedWebsiteAsync()
        {
            var item = PrimarySelected;
            if (item == null) return;
            try { Process.Start(new ProcessStartInfo(item.Url) { UseShellExecute = true }); }
            catch { /* 无法打开时保持静默 */ }
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
            if (_dialogs == null || !_dialogs.Confirm("删除链接", $"将链接「{name}」移入回收站吗？")) return;

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
                Logger.Error("智能列表删除链接失败", ex);
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
