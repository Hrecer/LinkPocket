using System.ComponentModel;
using System.Runtime.CompilerServices;
using LinkPocket.Models;
using LinkPocket.Services;

namespace LinkPocket.Managers
{
    public class SelectionManager : INotifyPropertyChanged
    {
        private string _selectedFolderId = string.Empty;
        private string? _selectedLinkId;
        private string? _multiSelectFolderId;

        public string SelectedFolderId
        {
            get => _selectedFolderId;
            private set
            {
                if (_selectedFolderId != value)
                {
                    _selectedFolderId = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(HasSelectedFolder));
                    SelectedFolderChanged?.Invoke(this, EventArgs.Empty);
                }
            }
        }

        public string? SelectedLinkId
        {
            get => _selectedLinkId;
            set
            {
                if (_selectedLinkId != value)
                {
                    _selectedLinkId = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(HasSelectedLink));
                    SelectedLinkChanged?.Invoke(this, EventArgs.Empty);
                }
            }
        }

        public bool HasSelectedFolder => !string.IsNullOrEmpty(_selectedFolderId) && _selectedFolderId != "0";
        public bool HasSelectedLink => !string.IsNullOrEmpty(_selectedLinkId);
        public bool IsInMultiSelectMode => !string.IsNullOrEmpty(_multiSelectFolderId);
        public string? MultiSelectFolderId => _multiSelectFolderId;

        public event PropertyChangedEventHandler? PropertyChanged;
        public event EventHandler? SelectedFolderChanged;
        public event EventHandler? SelectedLinkChanged;
        public event EventHandler? MultiSelectStateChanged;

        public enum CtrlClickResult
        {
            BlockedCrossDirectory,
            Promoted,
            Allowed
        }

        /// <summary>
        /// 选中文件夹：清除书签选中和多选状态
        /// </summary>
        public void SelectFolder(string folderId)
        {
            var old = _selectedFolderId;
            _selectedFolderId = folderId;
            _multiSelectFolderId = null;
            SelectedLinkId = null;

            OnPropertyChanged(nameof(SelectedFolderId));
            OnPropertyChanged(nameof(HasSelectedFolder));
            OnPropertyChanged(nameof(HasSelectedLink));
            SelectedFolderChanged?.Invoke(this, EventArgs.Empty);

            Logger.Info($"[选择管理器] SelectFolder: {old} → {folderId}, 已清除书签选中和多选");
        }

        /// <summary>
        /// 正常点击选中书签（保留文件夹上下文）
        /// </summary>
        public void SelectLink(string linkId)
        {
            _multiSelectFolderId = null;
            SelectedLinkId = linkId;
            Logger.Info($"[选择管理器] SelectLink: {linkId}, 保留文件夹={_selectedFolderId}");
        }

        /// <summary>
        /// 取消书签选中（保留文件夹选中）
        /// </summary>
        public void ClearLinkSelection()
        {
            SelectedLinkId = null;
            Logger.Info($"[选择管理器] ClearLinkSelection, 保留文件夹={_selectedFolderId}");
        }

        /// <summary>
        /// 清除所有选中（文件夹+书签+多选）
        /// </summary>
        public void ClearAll()
        {
            _selectedFolderId = string.Empty;
            _selectedLinkId = null;
            _multiSelectFolderId = null;

            OnPropertyChanged(nameof(SelectedFolderId));
            OnPropertyChanged(nameof(HasSelectedFolder));
            OnPropertyChanged(nameof(SelectedLinkId));
            OnPropertyChanged(nameof(HasSelectedLink));
            SelectedFolderChanged?.Invoke(this, EventArgs.Empty);
            SelectedLinkChanged?.Invoke(this, EventArgs.Empty);
            MultiSelectStateChanged?.Invoke(this, EventArgs.Empty);

            Logger.Info($"[选择管理器] ClearAll: 全部清除");
        }

        /// <summary>
        /// Ctrl+点击处理：同文件夹约束 + 提升单选中到多选 + 自动清除文件夹选中
        /// </summary>
        public CtrlClickResult HandleCtrlClick(LinkItem targetLink, string selectedFolderId, string? selectedLinkId, string? previousLinkListId, out string? newSelectedLinkId)
        {
            newSelectedLinkId = null;

            var linkListId = targetLink.ListId ?? string.Empty;

            Logger.Info($"[选择管理器] HandleCtrlClick: target={targetLink.LinkId}, ListId={linkListId}, " +
                $"selectedLinkId={_selectedLinkId ?? "null"}, prevLinkListId={previousLinkListId ?? "null"}, " +
                $"multiSelectFolderId={_multiSelectFolderId ?? "null"}");

            if (_multiSelectFolderId != null)
            {
                if (linkListId != _multiSelectFolderId)
                {
                    Logger.Info($"[选择管理器] → 阻止跨目录: link({linkListId}) ≠ multiSelect({_multiSelectFolderId})");
                    return CtrlClickResult.BlockedCrossDirectory;
                }
                Logger.Info($"[选择管理器] → 允许(同文件夹多选)");
                return CtrlClickResult.Allowed;
            }

            if (!string.IsNullOrEmpty(_selectedLinkId))
            {
                var prevListId = previousLinkListId ?? string.Empty;
                if (prevListId != linkListId)
                {
                    Logger.Info($"[选择管理器] → 阻止跨目录提升: prev({prevListId}) ≠ cur({linkListId})");
                    return CtrlClickResult.BlockedCrossDirectory;
                }
                _multiSelectFolderId = linkListId;
                ClearFolderForMultiSelect();
                Logger.Info($"[选择管理器] → 提升: {_selectedLinkId} 加入多选, 作用域={linkListId}");
                return CtrlClickResult.Promoted;
            }

            _multiSelectFolderId = linkListId;
            ClearFolderForMultiSelect();
            Logger.Info($"[选择管理器] → 允许(新建多选), 作用域={linkListId}");
            return CtrlClickResult.Allowed;
        }

        /// <summary>
        /// 进入多选模式时清除文件夹选中（避免同时选中文件夹+书签）
        /// </summary>
        private void ClearFolderForMultiSelect()
        {
            if (!string.IsNullOrEmpty(_selectedFolderId) && _selectedFolderId != "0")
            {
                var old = _selectedFolderId;
                _selectedFolderId = string.Empty;
                OnPropertyChanged(nameof(SelectedFolderId));
                OnPropertyChanged(nameof(HasSelectedFolder));
                SelectedFolderChanged?.Invoke(this, EventArgs.Empty);
                Logger.Info($"[选择管理器] ClearFolderForMultiSelect: {old} → null (进入多选模式)");
            }
        }

        /// <summary>
        /// 结束多选模式
        /// </summary>
        public void NotifyMultiSelectEnded()
        {
            if (!string.IsNullOrEmpty(_multiSelectFolderId))
            {
                Logger.Info($"[选择管理器] NotifyMultiSelectEnded: {_multiSelectFolderId} → null");
                _multiSelectFolderId = null;
                MultiSelectStateChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        /// <summary>
        /// 强制清除多选模式（含事件通知）
        /// </summary>
        public void ClearMultiSelectOnly()
        {
            _multiSelectFolderId = null;
            MultiSelectStateChanged?.Invoke(this, EventArgs.Empty);
            Logger.Info($"[选择管理器] ClearMultiSelectOnly → null");
        }

        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
