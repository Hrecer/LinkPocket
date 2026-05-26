using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using LinkPocket.Data;
using LinkPocket.Models;
using LinkPocket.Services;

namespace LinkPocket.ViewModels
{
    public class RecycleBinViewModel : INotifyPropertyChanged
    {
        private readonly LinkService _linkService;
        private bool _isLoading;
        private bool _hasError;
        private string _errorMessage = string.Empty;

        public RecycleBinViewModel(LinkService linkService)
        {
            _linkService = linkService;
        }

        public ObservableCollection<LinkItem> Items { get; } = new();

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

        public bool HasItems => Items.Count > 0;

        public HashSet<int> SelectedIds { get; } = new();

        public event PropertyChangedEventHandler? PropertyChanged;

        public async Task LoadAsync()
        {
            IsLoading = true;
            HasError = false;
            ErrorMessage = string.Empty;
            Items.Clear();
            SelectedIds.Clear();

            try
            {
                var deletedLinks = await _linkService.GetDeletedLinksAsync();
                foreach (var link in deletedLinks)
                    Items.Add(ConvertToItem(link));
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

        public async Task RestoreSelectedAsync()
        {
            var toRestore = Items.Where(i => SelectedIds.Contains(i.Id)).ToList();
            if (toRestore.Count == 0) return;
            foreach (var item in toRestore)
            {
                try { await _linkService.RestoreLinkAsync(item.Id); } catch { }
            }
            SelectedIds.Clear();
        }

        public async Task PermanentDeleteSelectedAsync()
        {
            var toDelete = Items.Where(i => SelectedIds.Contains(i.Id)).ToList();
            if (toDelete.Count == 0) return;
            foreach (var item in toDelete)
            {
                try { await _linkService.PermanentDeleteLinkAsync(item.Id); } catch { }
            }
            SelectedIds.Clear();
        }

        public void SelectSingle(int id)
        {
            SelectedIds.Clear();
            SelectedIds.Add(id);
        }

        public void ToggleMultiSelect(int id)
        {
            if (SelectedIds.Contains(id))
                SelectedIds.Remove(id);
            else
                SelectedIds.Add(id);
        }

        public void ClearSelection() => SelectedIds.Clear();
        public bool IsSelected(int id) => SelectedIds.Contains(id);

        private static LinkItem ConvertToItem(TrashedLink link) => new()
        {
            Id = link.Id,
            LinkId = link.LinkId,
            Url = link.Url,
            Title = link.Title ?? "",
            OriginalTitle = link.OriginalTitle ?? "",
            Description = link.Description ?? "",
            FaviconUrl = link.FaviconUrl ?? "",
            ListId = null,
            LastVisitedAt = link.LastVisitedAt,
            VisitCount = link.VisitCount,
            IsImportant = link.IsImportant,
            CreatedAt = link.CreatedAt,
            UpdatedAt = link.UpdatedAt
        };

        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
