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
using LinkPocket.Api;
using LinkPocket.Models;
using LinkPocket.Services;

namespace LinkPocket.ViewModels
{
    public partial class AddLinkViewModel : ObservableObject, INotifyDataErrorInfo
    {
        /// <summary>后端 API（经传输层代理，见 AppServices）。</summary>
        private static ILinkPocketApi Api => AppServices.Api;
        
        [ObservableProperty]
        private string _url = string.Empty;
        
        [ObservableProperty]
        private string _title = string.Empty;
        
        [ObservableProperty]
        private string _description = string.Empty;
        
        [ObservableProperty]
        private string? _selectedListId;
        
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
                
                await Api.CreateLinkAsync(
                    url: DecodeUrl(Url.Trim()),
                    title: string.IsNullOrEmpty(Title?.Trim()) ? null : Title.Trim(),
                    description: string.IsNullOrEmpty(Description?.Trim()) ? null : Description.Trim(),
                    listId: SelectedListId,
                    isImportant: false,
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
                var folders = await Api.GetFolderTreeAsync();

                // 后端返回扁平列表（含 ParentId），在前端组装成树
                var byId = folders.ToDictionary(f => f.FolderId);
                var items = folders.ToDictionary(f => f.FolderId, f => new FolderItem
                {
                    Id = f.FolderId,
                    FolderId = f.FolderId,
                    Name = f.Name,
                    ParentName = f.ParentId != null && byId.TryGetValue(f.ParentId, out var p) ? p.Name : null,
                    LinkCount = f.LinkCount,
                    Children = new List<FolderItem>()
                });

                var roots = new List<FolderItem>();
                foreach (var f in folders)
                {
                    if (!string.IsNullOrEmpty(f.ParentId) && items.TryGetValue(f.ParentId, out var parent))
                        parent.Children!.Add(items[f.FolderId]);
                    else
                        roots.Add(items[f.FolderId]);
                }

                Folders.Clear();
                FlattenFolders(roots, Folders, 0);
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

        private static string DecodeUrl(string url)
        {
            try
            {
                return Uri.UnescapeDataString(url);
            }
            catch
            {
                return url;
            }
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
