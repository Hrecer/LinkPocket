using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Input;
using LinkPocket.Api;
using LinkPocket.Models;
using LinkPocket.Services;

namespace LinkPocket.ViewModels
{
    /// <summary>
    /// 智能列表结果页 ViewModel（阶段 9 MVVM）：数据装载（LoadAsync）之外，
    /// 行选中态、右侧详情栏与页面动作命令（详情/打开网站/删除）也在此——
    /// 视图（Views/SmartListsPage）只负责表格装配与渲染。
    /// 动作语义与搜索页同一套：「详情/编辑」= 进入浏览页链接详情页；
    /// 删除 = 确认后移入回收站并重载当前列表（Reloaded 事件驱动视图重绑）。
    /// </summary>
    public class SmartListResultViewModel : INotifyPropertyChanged
    {
        /// <summary>后端 API（经传输层代理，由组合根注入）。</summary>
        private readonly ILinkPocketApi Api;
        private readonly INavigationService? _navigation;
        private readonly IDialogService? _dialogs;
        private readonly Func<string?, string> _resolveFolderPath;
        private bool _isDeleting;

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

        // —— 选中态与详情栏（阶段 9 MVVM 自页面下沉） ——

        private LinkItem? _selectedItem;
        /// <summary>当前选中行（视图 RowClick 调 <see cref="SelectItem"/>）。</summary>
        public LinkItem? SelectedItem
        {
            get => _selectedItem;
            private set { _selectedItem = value; OnPropertyChanged(); }
        }

        /// <summary>右侧详情栏（复用搜索页同一 DetailSidebar 数据契约）。</summary>
        public SearchDetailsViewModel Details { get; } = new();

        public SmartListResultViewModel(ILinkPocketApi api, string listId, string title,
            INavigationService? navigation, IDialogService? dialogs, Func<string?, string> resolveFolderPath)
        {
            Api = api;
            _listId = listId;
            Title = title;
            _navigation = navigation;
            _dialogs = dialogs;
            _resolveFolderPath = resolveFolderPath;

            OpenInBrowserCommand = new RelayCommand(
                () => { if (SelectedItem is { } item) _navigation?.OpenLinkInBrowser(item.LinkId); },
                () => SelectedItem != null);
            OpenWebsiteCommand = new RelayCommand(() => _ = OpenSelectedWebsiteAsync(), () => SelectedItem != null);
            DeleteCommand = new RelayCommand(() => _ = DeleteSelectedAsync(), () => SelectedItem != null);

            Details.OpenCommand = OpenInBrowserCommand;
            Details.RenameCommand = OpenInBrowserCommand;
            Details.OpenWebsiteCommand = OpenWebsiteCommand;
            Details.DeleteCommand = DeleteCommand;
        }

        public ICommand OpenInBrowserCommand { get; }
        public ICommand OpenWebsiteCommand { get; }
        public ICommand DeleteCommand { get; }

        /// <summary>视图在用户点击行时调用（表格行选中 → 选中态 + 详情栏）。</summary>
        public void SelectItem(LinkItem item)
        {
            SelectedItem = item;
            Details.UpdateFrom(item, _resolveFolderPath(item.ListId));
        }

        /// <summary>清空选中与详情栏（返回卡片页/重载列表时由视图或本类调用）。</summary>
        public void ClearSelection()
        {
            SelectedItem = null;
            Details.Clear();
        }

        public async Task LoadAsync()
        {
            IsLoading = true;
            HasData = false;
            Items.Clear();

            try
            {
                var limit = _listId == "most_visited" ? 20 : 100;
                List<LinkDto> links = await Api.GetSmartListAsync(_listId, limit);

                TotalCount = links.Count;

                if (links.Count == 0)
                {
                    EmptyMessage = _listId switch
                    {
                        "recently_added" => "最近 7 天没有添加新书签",
                        "recently_visited" => "最近 7 天没有访问过书签",
                        "recently_edited" => "最近 7 天没有编辑过书签",
                        "most_visited" => "暂无访问记录",
                        _ => "暂无数据"
                    };
                    return;
                }

                var items = links.Select(ConvertToLinkItem).ToList();
                foreach (var item in items) Items.Add(item);
                HasData = true;
            }
            finally
            {
                IsLoading = false;
            }
        }

        /// <summary>「打开网站」：默认浏览器打开并记录一次访问（与搜索页侧栏同口径）。</summary>
        private async Task OpenSelectedWebsiteAsync()
        {
            var item = SelectedItem;
            if (item == null) return;
            try { Process.Start(new ProcessStartInfo(item.Url) { UseShellExecute = true }); }
            catch { /* 无法打开时保持静默 */ }
            try
            {
                await Api.RecordVisitAsync(item.LinkId);
                // 统计行原位刷新（代次校验：选中未变才写回）
                if (SelectedItem?.LinkId == item.LinkId)
                    Details.UpdateFrom(item, _resolveFolderPath(item.ListId));
            }
            catch { /* 记账失败不打断 */ }
        }

        /// <summary>删除 = 确认后移入回收站（可恢复），随后重载当前列表刷新结果。</summary>
        private async Task DeleteSelectedAsync()
        {
            if (_isDeleting) return;
            var item = SelectedItem;
            if (item == null) return;

            var name = string.IsNullOrEmpty(item.Title) ? item.Url : item.Title;
            if (_dialogs == null || !_dialogs.Confirm("删除链接", $"将链接「{name}」移入回收站吗？")) return;

            _isDeleting = true;
            try
            {
                await Api.TrashLinkAsync(item.LinkId);
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
                _isDeleting = false;
            }
        }

        private static LinkItem ConvertToLinkItem(LinkDto link) => new()
        {
            LinkId = link.LinkId,
            Url = link.Url,
            Title = link.Title ?? "",
            Description = link.Description ?? "",
            FaviconUrl = link.FaviconUrl ?? "",
            ListId = link.ListId,
            LastVisitedAt = link.LastVisitedAt,
            VisitCount = link.VisitCount,
            IsImportant = link.IsImportant,
            CreatedAt = link.CreatedAt,
            UpdatedAt = link.UpdatedAt
        };

        protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
