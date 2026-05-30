using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace LinkPocket.ViewModels
{
    public class SettingsViewModel : INotifyPropertyChanged
    {
        private bool _autoFetchMetadata = true;
        private string _exportDirectory = string.Empty;
        private bool _isExporting;
        private string _exportStatusMessage = string.Empty;
        private bool _isExportOverlayVisible;

        public event PropertyChangedEventHandler? PropertyChanged;

        public SettingsViewModel()
        {
        }

        public bool AutoFetchMetadata
        {
            get => _autoFetchMetadata;
            set { _autoFetchMetadata = value; OnPropertyChanged(); }
        }

        public string ExportDirectory
        {
            get => _exportDirectory;
            set { _exportDirectory = value; OnPropertyChanged(); }
        }

        public bool IsExporting
        {
            get => _isExporting;
            set { _isExporting = value; OnPropertyChanged(); }
        }

        public string ExportStatusMessage
        {
            get => _exportStatusMessage;
            set { _exportStatusMessage = value; OnPropertyChanged(); }
        }

        public bool IsExportOverlayVisible
        {
            get => _isExportOverlayVisible;
            set { _isExportOverlayVisible = value; OnPropertyChanged(); }
        }

        protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}