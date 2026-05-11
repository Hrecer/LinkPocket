using System;
using System.Windows;
using LinkPocket.Services;
using LinkPocket.ViewModels;

namespace LinkPocket.Views
{
    public partial class AddLinkDialog : Window
    {
        private readonly AddLinkViewModel _viewModel;
        
        public AddLinkDialog()
        {
            try
            {
                InitializeComponent();
            }
            catch (Exception ex)
            {
                Logger.Error("AddLinkDialog XAML解析失败", ex);
                throw;
            }
            
            try
            {
                _viewModel = new AddLinkViewModel();
                DataContext = _viewModel;
                
                _viewModel.Saved += (s, e) => DialogResult = true;
                _viewModel.Cancelled += (s, e) => DialogResult = false;
                
                Logger.Info("AddLinkDialog 初始化完成");
            }
            catch (Exception ex)
            {
                Logger.Error("AddLinkDialog ViewModel初始化失败", ex);
                throw;
            }
        }

        public static bool? ShowDialog(Window owner)
        {
            var dialog = new AddLinkDialog { Owner = owner };
            return dialog.ShowDialog();
        }
    }
}
