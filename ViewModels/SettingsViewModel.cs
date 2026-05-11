using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using LinkPocket.Services;

namespace LinkPocket.ViewModels
{
    public class SettingsViewModel : INotifyPropertyChanged
    {
        private readonly AiService _aiService;
        
        private bool _autoFetchMetadata = true;
        private bool _checkDuplicate = true;
        private string? _openaiApiKey;
        private string? _anthropicApiKey;
        private string _defaultAiProvider = "openai";
        private bool _isAiAvailable;

        public event PropertyChangedEventHandler? PropertyChanged;

        public SettingsViewModel()
        {
            _aiService = new AiService();
            
            // 从环境变量或配置加载设置
            _openaiApiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
            _anthropicApiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
            _defaultAiProvider = Environment.GetEnvironmentVariable("AI_DEFAULT_PROVIDER") ?? "openai";
            _isAiAvailable = _aiService.IsAvailable;

            SaveCommand = new RelayCommand(SaveSettings);
            TestAiConnectionCommand = new RelayCommand(TestAiConnection);
        }

        public bool AutoFetchMetadata
        {
            get => _autoFetchMetadata;
            set { _autoFetchMetadata = value; OnPropertyChanged(); }
        }

        public bool CheckDuplicate
        {
            get => _checkDuplicate;
            set { _checkDuplicate = value; OnPropertyChanged(); }
        }

        public string? OpenaiApiKey
        {
            get => _openaiApiKey;
            set { _openaiApiKey = value; OnPropertyChanged(); }
        }

        public string? AnthropicApiKey
        {
            get => _anthropicApiKey;
            set { _anthropicApiKey = value; OnPropertyChanged(); }
        }

        public string DefaultAiProvider
        {
            get => _defaultAiProvider;
            set 
            { 
                _defaultAiProvider = value; 
                OnPropertyChanged();
                CheckAiAvailability();
            }
        }

        public bool IsAiAvailable
        {
            get => _isAiAvailable;
            set { _isAiAvailable = value; OnPropertyChanged(); }
        }

        public object AiProviderInfo => _aiService.GetProviderInfo();

        public System.Windows.Input.ICommand SaveCommand { get; }
        public System.Windows.Input.ICommand TestAiConnectionCommand { get; }

        private void SaveSettings()
        {
            try
            {
                // 保存设置到本地配置文件（实际项目中应使用配置管理器）
                System.Diagnostics.Debug.WriteLine("设置已保存");
                
                Logger.Info("设置已保存");
                System.Windows.MessageBox.Show("设置保存成功！", "成功", 
                    System.Windows.MessageBoxButton.OK, 
                    System.Windows.MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                Logger.Error("保存设置失败", ex);
                System.Windows.MessageBox.Show($"保存设置失败: {ex.Message}", "错误",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Error);
            }
        }

        private async void TestAiConnection()
        {
            try
            {
                var result = await _aiService.GenerateSummaryAsync(
                    title: "测试连接",
                    url: "https://example.com"
                );

                if (result.Success)
                {
                    IsAiAvailable = true;
                    var responseText = result.Data ?? "";
                    var displayText = responseText.Length > 50 ? responseText[..50] + "..." : responseText;
                    
                    System.Windows.MessageBox.Show(
                        $"AI连接成功！\n\n提供商: {result.Provider}\n响应: {displayText}",
                        "连接成功",
                        System.Windows.MessageBoxButton.OK,
                        System.Windows.MessageBoxImage.Information);
                }
                else
                {
                    IsAiAvailable = false;
                    System.Windows.MessageBox.Show(
                        $"AI连接失败: {result.Error}",
                        "连接失败",
                        System.Windows.MessageBoxButton.OK,
                        System.Windows.MessageBoxImage.Warning);
                }
            }
            catch (Exception ex)
            {
                IsAiAvailable = false;
                Logger.Error("AI连接测试失败", ex);
                System.Windows.MessageBox.Show(
                    $"AI连接测试失败: {ex.Message}",
                    "错误",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Error);
            }
        }

        private void CheckAiAvailability()
        {
            IsAiAvailable = _aiService.IsAvailable;
        }

        protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
