using System.Linq;
using System.Windows;
using System.Windows.Controls;
using LinkPocket.I18n;

namespace LinkPocket.Views;

/// <summary>
/// 导入方式选择弹窗（模态）：新增导入 / 清空后导入。
/// 复用 ConfirmDialog 视觉语言（TintBg 圆角卡 + 药丸按钮），ShowDialog 挡住后面无法操作。
/// 「清空后导入」为不可逆项：选中 = 次强调容器卡 + 需输入「我确认清空并导入」+ 确认键切换 TonalButton。
/// 静态 Show；owner 自动取当前激活窗口。
/// </summary>
public partial class ImportModeDialog : Window
{
    private static readonly LocValue ReplaceConfirmText = Loc.K("dialog.importMode.confirmPhrase");

    /// <summary>用户最终选择的导入方式：true = 清空后导入（危险），false = 新增导入。</summary>
    public bool ReplaceMode { get; private set; }

    public ImportModeDialog()
    {
        InitializeComponent();

        CancelBtn.Click += (_, _) => DialogResult = false;
        // ConfirmBtn 的 Click 已在 XAML 声明（ConfirmBtn_Click）——这里不再重复订阅（重复注册 = 每次点击两遍）

        // 无边框圆角窗口：WindowChrome 提供标题区拖拽（与 ConfirmDialog 同配置）
        var chrome = new System.Windows.Shell.WindowChrome
        {
            CaptionHeight = 76,
            GlassFrameThickness = new Thickness(0),
            UseAeroCaptionButtons = false
        };
        System.Windows.Shell.WindowChrome.SetWindowChrome(this, chrome);

        UpdateVisuals();
    }

    /// <summary>显示导入方式选择。返回是否确认；replaceMode 带出所选方式。</summary>
    public static bool Show(out bool replaceMode)
    {
        var dlg = new ImportModeDialog();
        var owner = Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive)
                    ?? Application.Current?.MainWindow;
        if (owner != null && owner != dlg) dlg.Owner = owner;

        var ok = dlg.ShowDialog() == true;
        replaceMode = dlg.ReplaceMode;
        return ok;
    }

    private bool _replace;

    private void AppendCard_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _replace = false;
        ReplaceConfirmInput.Text = string.Empty;
        UpdateVisuals();
    }

    private void ReplaceCard_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _replace = true;
        UpdateVisuals();
        ReplaceConfirmInput.Focus();
    }

    private void ReplaceConfirmInput_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        => UpdateVisuals();

    private void ConfirmBtn_Click(object sender, RoutedEventArgs e)
    {
        if (!ConfirmBtn.IsEnabled) return;
        ReplaceMode = _replace;
        DialogResult = true;
    }

    private void UpdateVisuals()
    {
        var confirmOk = !_replace || ReplaceConfirmInput.Text == ReplaceConfirmText.Resolve();

        // 选中态：常规 = PrimaryContainer 卡 + Primary 单选；「清空后导入」= 次强调容器卡
        // （与次操作共用同一套呈现 —— 破坏性动作不设专门警示色，不可逆性由确认文案承担）
        // ⚠️ 画刷一律走**资源引用**：一次性取画刷赋值会把当前主题固化成本地值（换主题后停在旧主题，
        // 与表头底色同一根因）；取不到键也不再静默兜一个透明画刷（那会伪装成"正常但不着色"）。
        AppendCard.SetResourceReference(Border.BackgroundProperty, _replace ? "SurfaceContainerHighest" : "PrimaryContainer");
        AppendRing.SetResourceReference(Border.BorderBrushProperty, _replace ? "OutlineVariant" : "Primary");
        AppendDot.Visibility = _replace ? Visibility.Collapsed : Visibility.Visible;

        ReplaceCard.SetResourceReference(Border.BackgroundProperty,
            _replace ? Theming.Tokens.AppTokens.SupportContainer : "SurfaceContainerHighest");
        ReplaceRing.SetResourceReference(Border.BorderBrushProperty, _replace ? "Primary" : "OutlineVariant");
        ReplaceDot.Visibility = _replace ? Visibility.Visible : Visibility.Collapsed;

        ReplaceConfirmBox.Visibility = _replace ? Visibility.Visible : Visibility.Collapsed;
        ConfirmBtn.Style = (Style)FindResource(_replace ? "TonalButton" : "PrimaryPillButton");
        ConfirmBtn.IsEnabled = confirmOk;
    }
}
