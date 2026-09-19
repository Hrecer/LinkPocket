namespace LinkPocket.ViewModels;

/// <summary>
/// 药丸按钮色调（动作面能力位的配色维度）：各页只声明"这颗药丸是什么语义"，
/// 由 <see cref="PillToneToStyleConverter"/> 映射到 UIKit 的三套既有药丸样式
/// （深紫 AccentBtn / 浅紫 Tonal / 奶油黄 WarnBg）——界面不按页复制按钮外观。
/// </summary>
public enum PillTone
{
    /// <summary>主操作：AccentBtn 深紫底白字（PrimaryPillButton）。</summary>
    Primary,

    /// <summary>次要操作：SecondaryContainer 浅紫底（TonalButton）。</summary>
    Tonal,

    /// <summary>破坏性动作：WarnBg 奶油黄底（WarnPillButton；严禁红色）。</summary>
    Warn,
}
