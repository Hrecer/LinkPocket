using System;
using System.Collections.Generic;
using System.Linq;

namespace LinkPocket.ViewModels;

/// <summary>
/// 浏览页**面包屑内联路径编辑（控制器）**：从 BrowserViewModel 抽出的状态机——
/// 编辑态 / 文本 / 校验态 / 候选列表与高亮索引的唯一事实来源。
/// 解析与候选算法在 UIKit <see cref="Views.PathResolver"/>（浏览页与回收站共用同一实现），
/// 本控制器只持状态并把"导航 / 提示"回调给宿主（VM）。
/// </summary>
public sealed class BrowserPathEditController
{
    private readonly Views.PathResolver _resolver;
    private readonly Action<string?> _navigate;
    private readonly Action<string> _report;

    private bool _isEditing;
    private string _text = string.Empty;
    private bool _isInvalid;
    private List<string> _candidates = new();
    private int _selectedCandidateIndex = -1;

    /// <param name="resolver">路径解析器（唯一实现，UIKit）。</param>
    /// <param name="navigate">Enter 解析成功 → 导航到目标目录（宿主执行 LoadAsync）。</param>
    /// <param name="report">解析失败 → 状态栏提示（宿主写入 StatusText）。</param>
    public BrowserPathEditController(Views.PathResolver resolver, Action<string?> navigate, Action<string> report)
    {
        _resolver = resolver;
        _navigate = navigate;
        _report = report;
    }

    /// <summary>状态变化（编辑态 / 文本 / 校验 / 候选 / 高亮）→ 宿主转发 PropertyChanged + 命令可用性。</summary>
    public event Action? Changed;

    public bool IsEditing => _isEditing;

    /// <summary>编辑文本（编辑框 TwoWay 绑定；输入即清校验态并刷新候选）。</summary>
    public string Text
    {
        get => _text;
        set
        {
            if (_text == value) return;
            _text = value ?? string.Empty;
            _isInvalid = false;
            UpdateCandidates();
            Changed?.Invoke();
        }
    }

    public bool IsInvalid => _isInvalid;

    /// <summary>候选清单（只读投影给视图 Popup）。</summary>
    public IReadOnlyList<string> Candidates => _candidates;

    public bool HasCandidates => _isEditing && _candidates.Count > 0;

    /// <summary>候选高亮索引（视图键盘 ↑/↓ 与补全共用）。</summary>
    public int SelectedCandidateIndex
    {
        get => _selectedCandidateIndex;
        set
        {
            if (_selectedCandidateIndex == value) return;
            _selectedCandidateIndex = value;
            Changed?.Invoke();
        }
    }

    /// <summary>进入编辑态（文本由宿主按当前面包屑构建：名字里的 / 转义为 \/，编辑往返不丢）。</summary>
    public void Enter(string initialText)
    {
        _text = initialText ?? string.Empty;
        _isInvalid = false;
        _isEditing = true;
        Changed?.Invoke();
    }

    /// <summary>退出编辑态（取消 / 提交成功后统一收口；候选清空）。</summary>
    public void Cancel()
    {
        _isEditing = false;
        _candidates = new List<string>();
        _selectedCandidateIndex = -1;
        Changed?.Invoke();
    }

    /// <summary>Enter：逐级按名解析路径（同级重名取排序第一；不区分大小写）。失败 → 标红并提示。</summary>
    public void Confirm()
    {
        if (_resolver.TryResolve(_text, out var folderId, out var invalidSegment))
        {
            Cancel();
            _navigate(folderId);
        }
        else
        {
            _isInvalid = true;
            _report($"路径不存在：{invalidSegment}");
            Changed?.Invoke();
        }
    }

    /// <summary>Tab：用当前候选补全最后一级。</summary>
    public void Complete()
    {
        if (_selectedCandidateIndex >= 0 && _selectedCandidateIndex < _candidates.Count)
            Choose(_candidates[_selectedCandidateIndex]);
    }

    /// <summary>选择候选（点击或 Tab）：改写文本后保留编辑态，继续输入下一级。</summary>
    public void Choose(string name)
    {
        Text = Views.PathResolver.ApplyCandidate(_text, name);   // 候选名含 / 时同样转义写入
        SelectedCandidateIndex = 0;
    }

    /// <summary>↑/↓ 移动候选高亮（由视图键盘事件调用）。</summary>
    public void MoveCandidate(int delta)
    {
        if (_candidates.Count == 0) return;
        SelectedCandidateIndex = Math.Clamp(_selectedCandidateIndex + delta, 0, _candidates.Count - 1);
    }

    private void UpdateCandidates()
    {
        _candidates = _resolver.Candidates(_text).ToList();
        _selectedCandidateIndex = _candidates.Count > 0 ? 0 : -1;
    }
}
