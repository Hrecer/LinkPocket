using System;
using System.Windows;
using LinkPocket.I18n;
using LinkPocket.ViewModels;

namespace LinkPocket.Views;

/// <summary>
/// 拖拽共享口径（**唯一实现**，浏览页 / 回收站两页共用）：
/// 起手阈值、OLE 效果映射、提示条文案——这三样最容易在两页各写一份后悄悄漂移（文案/阈值/光标不一致）。
/// </summary>
public static class DragSupport
{
    /// <summary>移动是否超过系统拖拽阈值（行左键 / 行右键 / 树节点共用同一口径）。</summary>
    public static bool BeyondThreshold(Point pos, Point start)
        => Math.Abs(pos.X - start.X) >= SystemParameters.MinimumHorizontalDragDistance ||
           Math.Abs(pos.Y - start.Y) >= SystemParameters.MinimumVerticalDragDistance;

    /// <summary>模式 → OLE 效果（**唯一映射**：Copy 时 Windows 会画带加号的光标）。</summary>
    public static DragDropEffects EffectFor(TransferMode mode)
        => mode == TransferMode.Copy ? DragDropEffects.Copy : DragDropEffects.Move;

    /// <summary>落点提示条文案（`移动到「X」` / `复制到「X」`）：动作词由模式决定，单一来源。</summary>
    public static string HintText(string? targetName, TransferMode mode)
        => string.IsNullOrEmpty(targetName)
            ? string.Empty
            : Loc.T(mode == TransferMode.Copy ? "browser.menu.copyTo" : "browser.menu.moveTo", targetName);
}
