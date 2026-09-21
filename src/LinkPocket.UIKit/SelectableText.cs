using System.Windows.Controls;

namespace LinkPocket.Views;

/// <summary>
/// 可鼠标拖选的展示型文本（全站唯一实现）：只读 <see cref="TextBox"/> 的薄子类 ——
/// 视觉默认值全部放在 UIKit.xaml 的**样式**里（透明底 / 无边框 / 无焦点视觉 / 隐藏插入符 / 可换行），
/// 因此局部属性与 BasedOn 派生样式仍能覆盖（等宽 ID 行就是这样定制成 NoWrap 的）。
/// 构造函数只钉住三项**正确性相关**属性（本地值，样式无法误改）：
/// <list type="bullet">
/// <item>只读 —— 这是展示文本，不是输入域；</item>
/// <item>不进 Tab 序列 —— 不污染键盘导航；</item>
/// <item>不记撤销。</item>
/// </list>
/// ⚠️ 主栏 / 结果表格的**行**不用它：行要保持"整行选中 / 拖拽 / 双击打开"的手势语义，
/// 文本拖选会与行手势冲突（列表行保持整行选中语义）。
/// </summary>
public class SelectableText : TextBox
{
    public SelectableText()
    {
        IsReadOnly = true;
        IsTabStop = false;
        IsUndoEnabled = false;
    }
}
