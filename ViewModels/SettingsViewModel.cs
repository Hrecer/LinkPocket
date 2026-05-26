using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace LinkPocket.ViewModels
{
    public class SettingsViewModel : INotifyPropertyChanged
    {
        private bool _autoFetchMetadata = true;

        public event PropertyChangedEventHandler? PropertyChanged;

        public SettingsViewModel()
        {
        }

        public bool AutoFetchMetadata
        {
            get => _autoFetchMetadata;
            set { _autoFetchMetadata = value; OnPropertyChanged(); }
        }

        protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
