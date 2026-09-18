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

namespace LinkPocket.ViewModels
{
    /// <summary>
    /// 回收站页数据模型（v2 层级化回收站）：
    /// - TreeNodes：被删文件夹单元树（纯展示层级，节点不可打开/导航）；
    /// - Entries：平铺条目（folder 单元根 + 单独删除的书签），按删除时间倒序；
    /// - 本期无还原：只有「永久删除」（整单元 or 单条）。
    /// 阶段 9 MVVM：页面动作命令（打开单元/返回/永久删除）在此，视图只做装配与渲染。
    /// </summary>
    public class RecycleBinViewModel : INotifyPropertyChanged
    {
        /// <summary>引擎客户端门面（分层 API 面，由组合根注入）。</summary>
        private readonly EngineClient _client;

        /// <summary>
        /// UI 端口槽位（组合根持有）：MainWindow 构造时才登记实现，晚于本 VM 的创建，
        /// 因此命令执行时惰性读取槽位（不缓存实例）。
        /// </summary>
        private readonly Services.UiPortProvider _ports;

        private bool _isLoading;
        private bool _hasError;
        private string _errorMessage = string.Empty;
        private TrashEntryDto? _selectedEntry;

        public RecycleBinViewModel(EngineClient client, Services.UiPortProvider ports)
        {
            _client = client;
            _ports = ports;

            EnterUnitCommand = new RelayCommand<TrashEntryDto>(entry => _ = EnterUnitGuardedAsync(entry));
            BackCommand = new RelayCommand(() => _ = BackGuardedAsync());
            PurgeCommand = new RelayCommand(() => _ = PurgeGuardedAsync());
        }

        private IDialogService? Dialogs => _ports.Dialogs;

        // ===== 页面动作命令（阶段 9 MVVM 自页面下沉；确认/失败提示统一走对话框端口）=====

        /// <summary>进入被删文件夹单元（双击文件夹行 / 树节点动作）。</summary>
        public ICommand EnterUnitCommand { get; }

        /// <summary>返回回收站根平铺视图。</summary>
        public ICommand BackCommand { get; }

        /// <summary>永久删除当前选中条目：确认后执行（folder = 整单元含子树，不可恢复）。</summary>
        public ICommand PurgeCommand { get; }

        private async Task EnterUnitGuardedAsync(TrashEntryDto? folderEntry)
        {
            if (folderEntry == null || folderEntry.EntryType != "folder") return;
            try
            {
                await EnterUnitAsync(folderEntry);
            }
            catch (Exception ex)
            {
                Dialogs?.Alert("打开失败", $"无法打开该文件夹单元：{ex.Message}");
            }
        }

        private async Task BackGuardedAsync()
        {
            try
            {
                await BackToRootAsync();
            }
            catch (Exception ex)
            {
                Dialogs?.Alert("返回失败", ex.Message);
            }
        }

        private async Task PurgeGuardedAsync()
        {
            var entry = SelectedEntry;
            if (entry == null) return;

            var name = string.IsNullOrEmpty(entry.Name) ? (entry.Url ?? "") : entry.Name;
            var message = entry.EntryType == "folder"
                ? $"确定要永久删除文件夹「{name}」吗？\n文件夹内的全部内容将一并删除，不可恢复。"
                : $"确定要永久删除「{name}」吗？\n此操作不可恢复。";
            if (Dialogs == null || !Dialogs.Confirm("永久删除", message, "永久删除", "delete-forever")) return;

            try
            {
                await PurgeSelectedAsync();
            }
            catch (Exception ex)
            {
                Dialogs?.Alert("永久删除失败", ex.Message);
            }
        }

        /// <summary>回收站文件夹树（纯视觉层级：节点不可打开，仅展示被删文件夹结构与计数）。</summary>
        public ObservableCollection<TrashFolderNode> TreeNodes { get; } = new();

        /// <summary>平铺条目：folder 单元根 + 单独删除的书签，按删除时间倒序。</summary>
        public ObservableCollection<TrashEntryDto> Entries { get; } = new();

        public bool IsLoading
        {
            get => _isLoading;
            set { _isLoading = value; OnPropertyChanged(); }
        }

        public bool HasError
        {
            get => _hasError;
            set { _hasError = value; OnPropertyChanged(); }
        }

        public string ErrorMessage
        {
            get => _errorMessage;
            set { _errorMessage = value; OnPropertyChanged(); }
        }

        public bool HasItems => Entries.Count > 0;

        /// <summary>状态栏口径：根 = 「回收站 · N 项」；单元内 = 「单元名 · N 项」。</summary>
        public string StatusText => (IsInUnit ? CurrentUnitName : "回收站") + $" · {Entries.Count} 项";

        /// <summary>当前选中条目（单选；点空白清除）。</summary>
        public TrashEntryDto? SelectedEntry
        {
            get => _selectedEntry;
            set { _selectedEntry = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasSelection)); }
        }

        public bool HasSelection => SelectedEntry != null;

        // ===== 「打开目录」：进入被删文件夹单元浏览其内容（回收站设计变更 2026-09-17）=====

        /// <summary>当前所在单元（null = 回收站根平铺视图）。</summary>
        public string? CurrentUnitId { get; private set; }

        /// <summary>当前单元名（面包屑 + 状态栏展示）。</summary>
        public string CurrentUnitName { get; private set; } = string.Empty;

        /// <summary>是否处于单元浏览态（控制返回钮 / 面包屑）。</summary>
        public bool IsInUnit => CurrentUnitId != null;

        /// <summary>进入被删文件夹单元：表格切换为该单元内容（直接子单元 + 子树内书签快照）。</summary>
        public async Task EnterUnitAsync(TrashEntryDto folderEntry)
        {
            if (folderEntry.EntryType != "folder") return;
            var contents = await _client.TrashUnitContentsAsync(folderEntry.Id);
            CurrentUnitId = folderEntry.Id;
            CurrentUnitName = string.IsNullOrEmpty(folderEntry.Name) ? "未命名文件夹" : folderEntry.Name;
            FillEntries(contents);
        }

        /// <summary>返回回收站根平铺视图（重新加载，顺带反映外部数据变化）。</summary>
        public async Task BackToRootAsync()
        {
            if (CurrentUnitId == null) return;
            CurrentUnitId = null;
            CurrentUnitName = string.Empty;
            OnPropertyChanged(nameof(IsInUnit));
            OnPropertyChanged(nameof(CurrentUnitName));
            await LoadAsync();
        }

        private void FillEntries(List<TrashEntryDto> contents)
        {
            Entries.Clear();
            foreach (var entry in contents) Entries.Add(entry);
            SelectedEntry = null;
            OnPropertyChanged(nameof(HasItems));
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(IsInUnit));
            OnPropertyChanged(nameof(CurrentUnitName));
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public async Task LoadAsync()
        {
            IsLoading = true;
            HasError = false;
            ErrorMessage = string.Empty;
            SelectedEntry = null;

            try
            {
                if (IsInUnit)
                {
                    // 单元内刷新：保持所在单元，仅重取内容
                    var unitContents = await _client.TrashUnitContentsAsync(CurrentUnitId!);
                    Entries.Clear();
                    foreach (var entry in unitContents) Entries.Add(entry);
                    OnPropertyChanged(nameof(HasItems));
                    OnPropertyChanged(nameof(StatusText));
                }
                else
                {
                    var entries = await _client.TrashListAsync();
                    var folderDtos = await _client.TrashTreeAsync();

                    Entries.Clear();
                    foreach (var entry in entries) Entries.Add(entry);
                    OnPropertyChanged(nameof(HasItems));
                    OnPropertyChanged(nameof(StatusText));

                    RebuildTree(folderDtos);
                }
            }
            catch (Exception ex)
            {
                HasError = true;
                ErrorMessage = $"加载回收站失败: {ex.Message}";
            }
            finally
            {
                IsLoading = false;
            }
        }

        /// <summary>按 parent_trash_folder_id 组装被删文件夹树（TrashFolderNode，纯展示）。</summary>
        private void RebuildTree(List<TrashFolderDto> folders)
        {
            TreeNodes.Clear();

            var nodeById = new Dictionary<string, TrashFolderNode>();
            foreach (var f in folders)
            {
                nodeById[f.TrashFolderId] = new TrashFolderNode
                {
                    TrashFolderId = f.TrashFolderId,
                    ParentTrashFolderId = f.ParentTrashFolderId,
                    Name = f.Name,
                    LinkCount = f.LinkCount
                };
            }

            foreach (var f in folders)
            {
                var node = nodeById[f.TrashFolderId];
                if (f.ParentTrashFolderId != null && nodeById.TryGetValue(f.ParentTrashFolderId, out var parent))
                    parent.Children.Add(node);
                else
                    TreeNodes.Add(node);
            }
        }

        /// <summary>永久删除当前选中条目（folder = 整单元含子树；link = 单条）。无还原，调用方负责确认。
        /// trash.purge 为破坏性命令：首次调用拿引擎确认令牌，确认后带令牌重发（EngineConfirm 编排）。</summary>
        public async Task PurgeSelectedAsync()
        {
            var entry = SelectedEntry;
            if (entry == null) return;
            var isFolder = entry.EntryType == "folder";
            // 破坏性两阶段：首次（无令牌）→ LP.SEC.003 拿 token → 带令牌重发
            await EngineConfirm.RunAsync(token => _client.TrashPurgeAsync(entry.Id, isFolder,
                new CallOptions { ConfirmToken = token }));
            SelectedEntry = null;
        }

        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
