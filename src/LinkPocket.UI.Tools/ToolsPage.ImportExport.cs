using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using LinkPocket.Contracts;
using LinkPocket.Input;
using LinkPocket.Models;
using LinkPocket.Services;
using LinkPocket.ViewModels;
using Material3.Wpf;
using LinkPocket.I18n;
using LinkPocket.UIKit;

namespace LinkPocket.Views

{
public partial class ToolsPage : UserControl
{
        // ============================================================
        // —— 书签导入 / 导出（协议调用在 ToolsViewModel；本页只做选文件/选目录、预检展示、结果展示） ——
        // ============================================================

        private void SegImport_Click(object sender, RoutedEventArgs e)
        {
            if (VmTools.BookmarkBusy) { SyncBookmarkSegments(); return; }
            SetBookmarkMode(importing: true);
        }

        private void SegExport_Click(object sender, RoutedEventArgs e)
        {
            if (VmTools.BookmarkBusy) { SyncBookmarkSegments(); return; }
            SetBookmarkMode(importing: false);
        }

        private void SetBookmarkMode(bool importing)
        {
            SegImport.IsChecked = importing;
            SegExport.IsChecked = !importing;
            BookmarkImportCard.Visibility = importing ? Visibility.Visible : Visibility.Collapsed;
            BookmarkExportCard.Visibility = importing ? Visibility.Collapsed : Visibility.Visible;
            MoveSegIndicator(animated: true);
        }

        private void SyncBookmarkSegments()
        {
            var importing = BookmarkImportCard.Visibility == Visibility.Visible;
            SegImport.IsChecked = importing;
            SegExport.IsChecked = !importing;
            MoveSegIndicator(animated: false);
        }

}
}
