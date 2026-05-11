using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using LinkPocket.Data;
using LinkPocket.Models;
using LinkPocket.Services;

namespace LinkPocket.ViewModels
{
    public class TrashViewModel : INotifyPropertyChanged
    {
        private readonly LinkService _linkService;
        private bool _isLoading;
        private bool _hasError;
        private string? _errorMessage;
        private ObservableCollection<LinkItem> _deletedLinks = new();
        private int _selectedCount = 0;

        public event PropertyChangedEventHandler? PropertyChanged;

        public TrashViewModel(LinkService linkService)
        {
            _linkService = linkService;
            LoadCommand = new RelayCommand(async () => await LoadTrashAsync());
            RestoreCommand = new RelayCommand<int>(async id => await RestoreAsync(id));
            PermanentDeleteCommand = new RelayCommand<int>(async id => await PermanentDeleteAsync(id));
            ClearAllCommand = new RelayCommand(async () => await ClearAllAsync());
            RefreshCommand = new RelayCommand(async () => await LoadTrashAsync());
        }

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

        public string? ErrorMessage
        {
            get => _errorMessage;
            set { _errorMessage = value; OnPropertyChanged(); }
        }

        public ObservableCollection<LinkItem> DeletedLinks
        {
            get => _deletedLinks;
            set { _deletedLinks = value; OnPropertyChanged(); }
        }

        public bool HasData => _deletedLinks.Count > 0;

        public int SelectedCount
        {
            get => _selectedCount;
            set { _selectedCount = value; OnPropertyChanged(); }
        }

        public ICommand LoadCommand { get; }
        public ICommand RestoreCommand { get; }
        public ICommand PermanentDeleteCommand { get; }
        public ICommand ClearAllCommand { get; }
        public ICommand RefreshCommand { get; }

        public async Task LoadTrashAsync()
        {
            try
            {
                IsLoading = true;
                HasError = false;
                ErrorMessage = null;

                var links = await _linkService.GetSmartListAsync("trash");

                var linkItems = links.Select(ConvertToLinkItem).ToList();
                DeletedLinks = new ObservableCollection<LinkItem>(linkItems);
            }
            catch (Exception ex)
            {
                HasError = true;
                ErrorMessage = ex.Message;
                Logger.Error("加载回收站失败", ex);
            }
            finally
            {
                IsLoading = false;
            }
        }

        private async Task RestoreAsync(int id)
        {
            var result = MessageBox.Show(
                "确定要恢复这个链接吗？",
                "确认恢复",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
            {
                try
                {
                    await _linkService.RestoreLinkAsync(id);
                    DeletedLinks.Remove(DeletedLinks.First(l => l.Id == id));
                    Logger.Info($"链接 {id} 已从回收站恢复");
                }
                catch (Exception ex)
                {
                    Logger.Error("恢复链接失败", ex);
                    MessageBox.Show($"恢复失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private async Task PermanentDeleteAsync(int id)
        {
            var result = MessageBox.Show(
                "确定要永久删除这个链接吗？此操作不可撤销！",
                "确认永久删除",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (result == MessageBoxResult.Yes)
            {
                try
                {
                    await _linkService.PermanentDeleteLinkAsync(id);
                    DeletedLinks.Remove(DeletedLinks.First(l => l.Id == id));
                    Logger.Info($"链接 {id} 已被永久删除");
                }
                catch (Exception ex)
                {
                    Logger.Error("永久删除链接失败", ex);
                    MessageBox.Show($"删除失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private async Task ClearAllAsync()
        {
            if (!DeletedLinks.Any()) return;

            var result = MessageBox.Show(
                $"确定要清空回收站吗？这将永久删除 {DeletedLinks.Count} 个链接！\n\n此操作不可撤销！",
                "确认清空回收站",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (result == MessageBoxResult.Yes)
            {
                try
                {
                    foreach (var link in DeletedLinks.ToList())
                    {
                        await _linkService.PermanentDeleteLinkAsync(link.Id);
                    }

                    DeletedLinks.Clear();
                    Logger.Info("回收站已清空");
                }
                catch (Exception ex)
                {
                    Logger.Error("清空回收站失败", ex);
                    MessageBox.Show($"清空失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                    
                    await LoadTrashAsync();
                }
            }
        }

        private LinkItem ConvertToLinkItem(Data.Link link) => new()
        {
            Id = link.Id,
            LinkId = link.LinkId,
            Url = link.Url,
            Title = link.Title ?? "",
            OriginalTitle = link.OriginalTitle ?? "",
            Description = link.Description ?? "",
            FaviconUrl = link.FaviconUrl ?? "",
            ListId = link.ListId,
            LastVisitedAt = link.LastVisitedAt,
            VisitCount = link.VisitCount,
            Rating = link.Rating,
            IsImportant = link.IsImportant,
            IsDeleted = true,
            DeletedAt = link.DeletedAt,
            CreatedAt = link.CreatedAt,
            UpdatedAt = link.UpdatedAt
        };

        protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
