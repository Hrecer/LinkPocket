using System.ComponentModel;
using System.Runtime.CompilerServices;
using LinkPocket.Models;
using LinkPocket.Services;
using LinkPocket.Contracts;

namespace LinkPocket.Managers
{
    /// <summary>
    /// 选中状态（文件夹 / 书签 / 多选）。
    /// <para><b>日志级别口径（S4 降噪）</b>：选中是**每次点击、每次方向键**都发生的高频动作，
    /// 记录一律走 <see cref="LogLevel.Debug"/>（分类 <c>ui.selection</c>）——缺省 info 级下不刷屏，
    /// 排障时把级别提到 debug 即可拿到完整选择轨迹。消息内容不因降级而删减（降级 ≠ 丢信息）。</para>
    /// </summary>
    public class SelectionManager : INotifyPropertyChanged
    {
        /// <summary>选择轨迹的来源分类（<c>logs.query { category: "ui.selection" }</c> 可按此过滤）。</summary>
        private const string Category = "ui.selection";

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

        /// <summary>是否选中了一个真实文件夹（根目录「全部书签」不是文件夹，选中它等于没选文件夹）。</summary>
        public bool HasSelectedFolder => !string.IsNullOrEmpty(_selectedFolderId);
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

            LpLog.Debug($"[选择管理器] SelectFolder: {old} → {folderId}, 已清除书签选中和多选", Category);
        }

        /// <summary>
        /// 正常点击选中书签（保留文件夹上下文）
        /// </summary>
        public void SelectLink(string linkId)
        {
            _multiSelectFolderId = null;
            SelectedLinkId = linkId;
            LpLog.Debug($"[选择管理器] SelectLink: {linkId}, 保留文件夹={_selectedFolderId}", Category);
        }

        /// <summary>
        /// 取消书签选中（保留文件夹选中）
        /// </summary>
        public void ClearLinkSelection()
        {
            SelectedLinkId = null;
            LpLog.Debug($"[选择管理器] ClearLinkSelection, 保留文件夹={_selectedFolderId}", Category);
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

            LpLog.Debug($"[选择管理器] ClearAll: 全部清除", Category);
        }

        /// <summary>
        /// Ctrl+点击处理：同文件夹约束 + 提升单选中到多选 + 自动清除文件夹选中
        /// </summary>
        public CtrlClickResult HandleCtrlClick(LinkItem targetLink, string selectedFolderId, string? selectedLinkId, string? previousLinkListId, out string? newSelectedLinkId)
        {
            newSelectedLinkId = null;

            var linkListId = targetLink.ListId ?? string.Empty;

            LpLog.Debug($"[选择管理器] HandleCtrlClick: target={targetLink.LinkId}, ListId={linkListId}, " +
                $"selectedLinkId={_selectedLinkId ?? "null"}, prevLinkListId={previousLinkListId ?? "null"}, " +
                $"multiSelectFolderId={_multiSelectFolderId ?? "null"}", Category);

            if (_multiSelectFolderId != null)
            {
                if (linkListId != _multiSelectFolderId)
                {
                    LpLog.Debug($"[选择管理器] → 阻止跨目录: link({linkListId}) ≠ multiSelect({_multiSelectFolderId})", Category);
                    return CtrlClickResult.BlockedCrossDirectory;
                }
                LpLog.Debug($"[选择管理器] → 允许(同文件夹多选)", Category);
                return CtrlClickResult.Allowed;
            }

            if (!string.IsNullOrEmpty(_selectedLinkId))
            {
                var prevListId = previousLinkListId ?? string.Empty;
                if (prevListId != linkListId)
                {
                    LpLog.Debug($"[选择管理器] → 阻止跨目录提升: prev({prevListId}) ≠ cur({linkListId})", Category);
                    return CtrlClickResult.BlockedCrossDirectory;
                }
                _multiSelectFolderId = linkListId;
                ClearFolderForMultiSelect();
                LpLog.Debug($"[选择管理器] → 提升: {_selectedLinkId} 加入多选, 作用域={linkListId}", Category);
                return CtrlClickResult.Promoted;
            }

            _multiSelectFolderId = linkListId;
            ClearFolderForMultiSelect();
            LpLog.Debug($"[选择管理器] → 允许(新建多选), 作用域={linkListId}", Category);
            return CtrlClickResult.Allowed;
        }

        /// <summary>
        /// 进入多选模式时清除文件夹选中（避免同时选中文件夹+书签）
        /// </summary>
        private void ClearFolderForMultiSelect()
        {
            if (!string.IsNullOrEmpty(_selectedFolderId))
            {
                var old = _selectedFolderId;
                _selectedFolderId = string.Empty;
                OnPropertyChanged(nameof(SelectedFolderId));
                OnPropertyChanged(nameof(HasSelectedFolder));
                SelectedFolderChanged?.Invoke(this, EventArgs.Empty);
                LpLog.Debug($"[选择管理器] ClearFolderForMultiSelect: {old} → null (进入多选模式)", Category);
            }
        }

        /// <summary>
        /// 结束多选模式
        /// </summary>
        public void NotifyMultiSelectEnded()
        {
            if (!string.IsNullOrEmpty(_multiSelectFolderId))
            {
                LpLog.Debug($"[选择管理器] NotifyMultiSelectEnded: {_multiSelectFolderId} → null", Category);
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
            LpLog.Debug($"[选择管理器] ClearMultiSelectOnly → null", Category);
        }

        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
