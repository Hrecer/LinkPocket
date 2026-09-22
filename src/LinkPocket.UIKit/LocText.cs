using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using LinkPocket.I18n;
using LinkPocket.Views;

namespace LinkPocket.UIKit;

/// <summary>
/// 代码里给控件写"会驻留在界面上的文案"的唯一出口：交出去的是 <see cref="LocValue"/>（键 + 参数），
/// 控件显示的是它在当前语言下的取词结果——语言一变，控件上的文字跟着换。
/// </summary>
/// <remarks>
/// <para>
/// 为什么不写 <c>tb.Text = Loc.T(key)</c>：那等于把成品句子钉进程里，换语言后停在旧语言，
/// 还得靠宿主"记得重投影"（不变式禁的就是这个形状）。这里改用与 <c>{loc:Value}</c> 同一条
/// 绑定通道（版本失效 + <see cref="LocValueResolver"/>），自己不留第二套状态。
/// </para>
/// <para>
/// 适用范围有限：**只有确实没有绑定位置**的元素才走这里（覆盖层进度条、取色盘的即时校验提示）。
/// 有对应 VM/模型的文本一律在 XAML 里写 <c>{loc:Value}</c>。
/// </para>
/// </remarks>
public static class LocText
{
    private static readonly ConditionalWeakTable<DependencyObject, Holder> Holders = new();

    /// <summary>把 <paramref name="text"/> 的显示文字绑到给定文案值上。</summary>
    public static void SetText(this TextBlock text, LocValue value)
    {
        var holder = Get(text);
        holder.Set(value);
        BindingOperations.SetBinding(text, TextBlock.TextProperty, Build(holder));
    }

    /// <summary>把 <paramref name="control"/> 的内容绑到给定文案值上。</summary>
    public static void SetContent(this ContentControl control, LocValue value)
    {
        var holder = Get(control);
        holder.Set(value);
        BindingOperations.SetBinding(control, ContentControl.ContentProperty, Build(holder));
    }

    /// <summary>把提示绑到给定文案值上。</summary>
    public static void SetTip(this FrameworkElement element, LocValue value)
    {
        var holder = Get(element);
        holder.Set(value);
        element.SetBinding(FrameworkElement.ToolTipProperty, Build(holder));
    }

    /// <summary>
    /// 代码侧接**自适应通道**（模板侧那句 <c>{loc:Fit key}</c> 的 C# 版）：文字与字号由
    /// <see cref="LocFit"/> 按实测可用宽决定（短式优先 → 缩字号无下限），**宽度由调用方冻结**。
    /// </summary>
    /// <remarks>
    /// ⚠️ 只给"代码建的控件"用（能写 XAML 的地方一律写 <c>{loc:Fit key}</c>）。
    /// 两条通道必须成对：<see cref="LocFit.TextProperty"/> 是**唯一事实来源**、
    /// <c>TextBlock.Text</c> 只画 <c>LocFit.Chosen</c>——在这里再绑一次 Text 会让同一个属性有两个写者。
    /// </remarks>
    public static void SetFitText(this TextBlock text, string key, LocFitMode mode = LocFitMode.ShrinkThenEllipsis)
    {
        LocFit.SetMode(text, mode);
        BindingOperations.SetBinding(text, LocFit.TextProperty, I18n.LocFitBinding.For(key));
        text.SetBinding(TextBlock.TextProperty, LocFitResolver.BuildChosenBinding());
    }

    private static Holder Get(DependencyObject key) => Holders.GetValue(key, _ => new Holder());

    private static MultiBinding Build(Holder holder)
    {
        var mb = new MultiBinding { Converter = LocValueResolver.Instance, Mode = BindingMode.OneWay };
        mb.Bindings.Add(new Binding(nameof(LocTable.Version)) { Source = LocTable.Instance, Mode = BindingMode.OneWay });
        mb.Bindings.Add(new Binding(nameof(Holder.Value)) { Source = holder, Mode = BindingMode.OneWay });
        return mb;
    }

    /// <summary>每条绑定自己的值持有者：值一换就通知，绑定回来重新取词。</summary>
    private sealed class Holder : INotifyPropertyChanged
    {
        public LocValue Value { get; private set; }

        public event PropertyChangedEventHandler? PropertyChanged;

        public void Set(LocValue value)
        {
            if (Value.Equals(value)) return;
            Value = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Value)));
        }
    }
}
