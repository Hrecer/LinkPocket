using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Windows.Media;
using LinkPocket.Contracts;
using LinkPocket.Theming;
using LinkPocket.Theming.Color;
using LinkPocket.Theming.Fonts;
using LinkPocket.Theming.Preferences;
using LinkPocket.Theming.Themes;
using LinkPocket.Theming.Tokens;
using Material3.Core;
using LinkPocket.I18n;

namespace LinkPocket.ViewModels;

/// <summary>一个色槽（4/5 色自选配色）。**可以为空**（还没选颜色）。</summary>
/// <remarks>
/// 空槽不是"黑色"也不是"透明"这类会骗人的值：<see cref="Color"/> 为 <c>null</c>、
/// <see cref="Hex"/> 为空串，界面据此画虚线空心环 + 「+」。
/// </remarks>
public sealed class ColorSlotViewModel
{
    public ColorSlotViewModel(int index, Color? color)
    {
        Index = index;
        Color = color;
    }

    public int Index { get; }

    /// <summary>槽里的颜色；<c>null</c> = **空槽**（还没选）。</summary>
    public Color? Color { get; }

    /// <summary>是否为空槽（界面画虚线空心环 + 「+」）。</summary>
    public bool IsEmpty => Color is null;

    /// <summary>是否有颜色（应用门槛按它数"还差几个"）。</summary>
    public bool HasColor => Color is not null;

    /// <summary>槽位序号文案（1 起）。</summary>
    public LocValue Label => Loc.K("appearance.slot.n", Index + 1);

    /// <summary>HEX 文案（空槽 = 空串：**绝不**拿 <c>#000000</c> 这类假值冒充"已选"）。</summary>
    public string Hex => Color is { } c ? $"#{c.R:X2}{c.G:X2}{c.B:X2}" : string.Empty;

    /// <summary>槽位数值文案（空槽 = 「未选」）。</summary>
    public LocValue ValueText => IsEmpty ? Loc.K("common.noneSelected") : Loc.K("appearance.slot.hex", Hex);
}
