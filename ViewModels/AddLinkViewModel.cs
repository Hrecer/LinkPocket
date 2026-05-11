using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LinkPocket.Data;
using LinkPocket.Models;
using LinkPocket.Services;

namespace LinkPocket.ViewModels
{
    public partial class AddLinkViewModel : ObservableObject, INotifyDataErrorInfo
    {
        private readonly LinkService _linkService;
        private readonly FolderService _folderService;
        
        [ObservableProperty]
        private string _url = string.Empty;
        
        [ObservableProperty]
        private string _title = string.Empty;
        
        [ObservableProperty]
        private string _description = string.Empty;
        
        [ObservableProperty]
        private int? _selectedListId;
        
        [ObservableProperty]
        private ObservableCollection<FolderItem> _folders = new();

        [ObservableProperty]
        private bool _isLoading;
        
        [ObservableProperty]
        private bool _hasError;
        
        [ObservableProperty]
        private string _errorMessage = string.Empty;

        public string SaveButtonText => IsLoading ? "保存中..." : "保存";

        public event EventHandler<DataErrorsChangedEventArgs>? ErrorsChanged;
        private readonly Dictionary<string, List<string>> _errors = new();

        public AddLinkViewModel()
        {
            var db = new LinkPocketDbContext();
            _linkService = new LinkService(db);
            _folderService = new FolderService(db);
            
            SaveCommand = new AsyncRelayCommand(SaveAsync, CanSave);
            CancelCommand = new RelayCommand(Cancel);
            
            _ = LoadInitialDataAsync();
        }

        public IAsyncRelayCommand SaveCommand { get; }
        public ICommand CancelCommand { get; }

        private async Task SaveAsync()
        {
            if (!Validate()) return;
            
            try
            {
                IsLoading = true;
                HasError = false;
                
                await _linkService.CreateLinkAsync(
                    url: Url.Trim(),
                    title: string.IsNullOrEmpty(Title?.Trim()) ? null : Title.Trim(),
                    description: string.IsNullOrEmpty(Description?.Trim()) ? null : Description.Trim(),
                    listId: SelectedListId,
                    rating: 0,
                    isImportant: false,
                    tagIds: null,
                    autoFetchMetadata: false
                );

                Saved?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                HasError = true;
                ErrorMessage = $"保存失败: {ex.Message}";
            }
            finally
            {
                IsLoading = false;
            }
        }

        private void Cancel()
        {
            Cancelled?.Invoke(this, EventArgs.Empty);
        }

        private async Task LoadInitialDataAsync()
        {
            try
            {
                var folders = await _folderService.GetTreeAsync();
                Folders.Clear();
                FlattenFolders(folders.Select(ConvertToFolderItem).ToList(), Folders, 0);
            }
            catch (Exception ex)
            {
                Logger.Error("加载目录数据失败", ex);
            }
        }

        private bool Validate()
        {
            ClearErrors();
            bool isValid = true;
            
            if (string.IsNullOrWhiteSpace(Url))
            {
                AddError("url", "URL不能为空");
                isValid = false;
            }
            else if (!IsValidUrl(Url))
            {
                AddError("url", "请输入有效的URL格式");
                isValid = false;
            }
            
            if (string.IsNullOrWhiteSpace(Title))
            {
                AddError("title", "标题不能为空");
                isValid = false;
            }
            
            return isValid;
        }

        private bool IsValidUrl(string url)
        {
            return Uri.TryCreate(url, UriKind.Absolute, out var uriResult) 
                   && (uriResult.Scheme == Uri.UriSchemeHttp || uriResult.Scheme == Uri.UriSchemeHttps);
        }

        public System.Collections.IEnumerable GetErrors(string? propertyName)
        {
            if (string.IsNullOrEmpty(propertyName))
            {
                var result = new System.Collections.ArrayList();
                foreach (var errorList in _errors.Values)
                    foreach (var error in errorList)
                        result.Add(error);
                return result;
            }
            
            if (_errors.TryGetValue(propertyName ?? "", out var errors))
            {
                var result = new System.Collections.ArrayList();
                foreach (var error in errors)
                    result.Add(error);
                return result;
            }
            
            return System.Array.Empty<string>();
        }

        public bool HasErrors => _errors.Count > 0;

        private void AddError(string propertyName, string error)
        {
            if (!_errors.ContainsKey(propertyName))
                _errors[propertyName] = new List<string>();
            _errors[propertyName].Add(error);
            ErrorsChanged?.Invoke(this, new DataErrorsChangedEventArgs(propertyName));
        }

        private void ClearErrors(string? propertyName = null)
        {
            if (propertyName == null)
            {
                _errors.Clear();
                ErrorsChanged?.Invoke(this, new DataErrorsChangedEventArgs(""));
            }
            else
            {
                _errors.Remove(propertyName);
                ErrorsChanged?.Invoke(this, new DataErrorsChangedEventArgs(propertyName));
            }
        }

        private FolderItem ConvertToFolderItem(Data.Folder folder) => new()
        {
            Id = folder.Id,
            Name = folder.Name,
            ParentName = folder.Parent?.Name,
            LinkCount = folder.LinkCount,
            Children = folder.Children.Select(ConvertToFolderItem).ToList()
        };

        private void FlattenFolders(List<FolderItem> folders, ObservableCollection<FolderItem> result, int level)
        {
            foreach (var folder in folders)
            {
                result.Add(folder);
                if (folder.Children != null && folder.Children.Count > 0)
                    FlattenFolders(folder.Children.ToList(), result, level + 1);
            }
        }

        partial void OnUrlChanged(string value)
        {
            ClearErrors("url");
            ValidateCanExecute();
        }

        partial void OnTitleChanged(string value)
        {
            ClearErrors("title");
            ValidateCanExecute();
        }

        partial void OnIsLoadingChanged(bool value)
        {
            OnPropertyChanged(nameof(SaveButtonText));
            ValidateCanExecute();
        }

        private void ValidateCanExecute()
        {
            ((AsyncRelayCommand)SaveCommand).NotifyCanExecuteChanged();
        }

        private bool CanSave() => !IsLoading && !HasErrors;

        public event EventHandler? Saved;
        public event EventHandler? Cancelled;
    }
}
