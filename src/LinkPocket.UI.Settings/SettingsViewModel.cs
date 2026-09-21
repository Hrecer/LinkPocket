using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace LinkPocket.ViewModels
{
    /// <summary>
    /// 设置页视图模型。
    ///
    /// 书签导入 / 导出已合并进工具页，相关状态（导出目录、导出进度遮罩）
    /// 一并迁移——该流程现在由 <c>Views/ToolsPage</c> 自行管理，这里只保留与设置页相关的开关。
    /// </summary>
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
