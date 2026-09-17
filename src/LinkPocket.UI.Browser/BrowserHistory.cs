using System;
using LinkPocket.Api;
using System.Collections.Generic;

namespace LinkPocket.ViewModels;

/// <summary>
/// 资源管理器式浏览的导航状态（P4；阶段 10 自 Managers/NavigationController 归位为 BrowserHistory）：
/// 维护 currentFolderId 与后退 / 前进历史栈（只存 folderId，null = 根目录「全部书签」）。
/// 不持有任何 UI 引用；目录内容加载由 BrowserViewModel 完成。
/// </summary>
public class BrowserHistory
{
    private readonly Stack<string?> _back = new();
    private readonly Stack<string?> _forward = new();

    /// <summary>当前目录 ID；<c>null</c> 表示根目录（全部书签）。根目录不是文件夹，没有 ID。</summary>
    public string? CurrentFolderId { get; private set; }

    public bool CanGoBack => _back.Count > 0;
    public bool CanGoForward => _forward.Count > 0;

    /// <summary>导航到指定目录；重复导航到当前目录时忽略。返回是否发生了变化。</summary>
    public bool NavigateTo(string? folderId)
    {
        var normalized = Normalize(folderId);
        if (normalized == CurrentFolderId) return false;

        _back.Push(CurrentFolderId);
        _forward.Clear();
        CurrentFolderId = normalized;
        return true;
    }

    /// <summary>后退，返回目标目录 ID（无历史时返回当前目录）。</summary>
    public string? GoBack()
    {
        if (_back.Count == 0) return CurrentFolderId;
        _forward.Push(CurrentFolderId);
        CurrentFolderId = _back.Pop();
        return CurrentFolderId;
    }

    /// <summary>前进，返回目标目录 ID（无历史时返回当前目录）。</summary>
    public string? GoForward()
    {
        if (_forward.Count == 0) return CurrentFolderId;
        _back.Push(CurrentFolderId);
        CurrentFolderId = _forward.Pop();
        return CurrentFolderId;
    }

    private static string? Normalize(string? folderId) => FolderIds.Normalize(folderId);
}
