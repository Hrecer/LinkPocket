using System.Linq;
using System.Windows;

namespace LinkPocket.Views;

/// <summary>
/// 统一确认弹窗（MD3 Expressive）：删除/危险操作的唯一确认入口。
/// 浏览页（BrowserViewModel）、搜索侧栏（MainWindow.ShowConfirmDialog）、
/// 协调器（IDialogService.ConfirmDeleteFolder）全部走这里，不再各自手搓弹窗。
/// 静态 Show 调用；owner 自动取当前激活窗口。
/// </summary>
public partial class ConfirmDialog : Window
{
    public ConfirmDialog()
    {
        InitializeComponent();

        // 取消/确认（按钮在底部，不与顶部拖拽热区重叠）
        CancelBtn.Click += (_, _) => DialogResult = false;
        ConfirmBtn.Click += (_, _) => DialogResult = true;

        // 无边框圆角窗口：WindowChrome 提供标题区拖拽（与 InputDialog 同配置）
        var chrome = new System.Windows.Shell.WindowChrome
        {
            CaptionHeight = 76,
            GlassFrameThickness = new Thickness(0),
            UseAeroCaptionButtons = false
        };
        System.Windows.Shell.WindowChrome.SetWindowChrome(this, chrome);
    }

    /// <summary>
    /// 显示确认/提示弹窗。confirmText 确认键文案（删除类传「删除」），iconKind 须在 LpIcons 字形表内；
    /// chipBrushKey = 图标 chip 底色资源键：删除/警告默认 WarnBg（奶油黄），信息/成功传 "TintPanel"。
    /// </summary>
    public static bool Show(string title, string message, string confirmText = "确定",
        string iconKind = "delete-outline", string chipBrushKey = "WarnBg")
    {
        var dlg = new ConfirmDialog
        {
            Title = title
        };
        dlg.TitleText.Text = title;
        dlg.MessageText.Text = message;
        dlg.ConfirmLabel.Text = confirmText;
        dlg.IconGlyph.Kind = iconKind;
        if (Application.Current.TryFindResource(chipBrushKey) is System.Windows.Media.Brush chip)
            dlg.ChipBorder.Background = chip;

        var owner = Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive)
                    ?? Application.Current?.MainWindow;
        if (owner != null && owner != dlg) dlg.Owner = owner;

        return dlg.ShowDialog() == true;
    }
}
