using System.Linq;
using System.Windows;

namespace LinkPocket.Views;

/// <summary>
/// 标准文本输入对话框（新建文件夹 / 重命名共用）。
/// 视图完全由 <see cref="ViewModels.InputDialogViewModel"/> 数据驱动；
/// code-behind 只负责组装 VM、桥接 RequestClose → DialogResult。
/// 通过静态 Show 调用；BrowserViewModel 经 BrowserViewModel.Prompt 委托使用，
/// 保持 ViewModel 不直接依赖控件类型。
/// </summary>
public partial class InputDialog : Window
{
    private readonly ViewModels.InputDialogViewModel _vm;

    public InputDialog(ViewModels.InputDialogViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        _vm.RequestClose += confirmed => DialogResult = confirmed;

        // 无边框圆角窗口：WindowChrome 提供标题区拖拽（无右上角关闭按钮，取消走底部按钮）
        var chrome = new System.Windows.Shell.WindowChrome
        {
            CaptionHeight = 76,
            GlassFrameThickness = new Thickness(0),
            UseAeroCaptionButtons = false
        };
        System.Windows.Shell.WindowChrome.SetWindowChrome(this, chrome);
    }

    /// <summary>显示输入框。返回去掉首尾空白的输入内容；用户取消返回 null。</summary>
    public static string? Show(string title, string defaultValue = "", string prompt = "", string iconKind = "pencil")
    {
        var dlg = new InputDialog(new ViewModels.InputDialogViewModel
        {
            Title = title,
            Prompt = string.IsNullOrEmpty(prompt) ? title : prompt,
            IconKind = iconKind
        });
        var owner = Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive)
                    ?? Application.Current?.MainWindow;
        if (owner != null && owner != dlg) dlg.Owner = owner;

        dlg._vm.Value = defaultValue ?? "";

        dlg.Loaded += (_, _) =>
        {
            dlg.ValueBox.SelectAll();
            dlg.ValueBox.Focus();
        };

        return dlg.ShowDialog() == true ? dlg._vm.Value.Trim() : null;
    }
}
