using System.Linq;
using System.Windows;
using LinkPocket.I18n;

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

        // 无边框圆角窗口：WindowChrome 提供标题区拖拽（与其余弹窗同配置）
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
    /// chipBrushKey = 图标 chip 底色资源键：删除类默认**次强调容器**（App.Support.Container，与次操作同一套
    /// 呈现——破坏性动作不设专门警示色），信息/成功传 "TintPanel"。
    /// </summary>
    public static bool Show(string title, string message, string? confirmText = null,
        string iconKind = "delete-outline", string chipBrushKey = Theming.Tokens.AppTokens.SupportContainer)
    {
        var dlg = new ConfirmDialog
        {
            Title = title
        };
        dlg.TitleText.Text = title;
        dlg.MessageText.Text = message;
        dlg.ConfirmLabel.Text = confirmText ?? Loc.T("common.ok");
        dlg.IconGlyph.Kind = iconKind;
        // 底色走**资源引用**：一次性取画刷赋值会在换主题后停在旧主题（表头同根因）；
        // 取不到键也不静默兜一个 Transparent（那会把"主题未装配"伪装成"正常但不着色"）。
        dlg.ChipBorder.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, chipBrushKey);

        var owner = Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive)
                    ?? Application.Current?.MainWindow;
        if (owner != null && owner != dlg) dlg.Owner = owner;

        return dlg.ShowDialog() == true;
    }
}
