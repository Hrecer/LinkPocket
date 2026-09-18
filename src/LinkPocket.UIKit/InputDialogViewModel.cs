using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace LinkPocket.ViewModels;

/// <summary>
/// 标准文本输入对话框的视图模型（数据驱动，视图零代码后置逻辑）：
/// 标题 / 提示语 / 图标 / 按钮文案全部由属性驱动，校验经 IDataErrorInfo 暴露，
/// 确定按钮随输入有效性自动启用/禁用。新建文件夹、重命名等场景共用。
/// </summary>
public class InputDialogViewModel : INotifyPropertyChanged, IDataErrorInfo
{
    private string _value = "";
    private bool _touched;

    public string Title { get; init; } = "";
    public string Prompt { get; init; } = "";
    /// <summary>M3Icon 键名（LpIcons 注册表），头部装饰图标。</summary>
    public string IconKind { get; init; } = "pencil";
    public string ConfirmText { get; init; } = "确定";
    public string CancelText { get; init; } = "取消";

    /// <summary>输入值；决定确定按钮可用性与错误提示。</summary>
    public string Value
    {
        get => _value;
        set
        {
            if (_value == value) return;
            _value = value;
            MarkTouched();
            OnPropertyChanged();
            // 2.6：ConfirmCommand.CanExecute 依赖值是否为空 —— 立即重评估，不依赖下一次输入事件
            System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        }
    }

    /// <summary>错误提示可见性：编辑过且为空才显示（避免打开就飘红）。</summary>
    public bool HasError => _touched && string.IsNullOrWhiteSpace(_value);

    public ICommand ConfirmCommand { get; }
    public ICommand CancelCommand { get; }

    /// <summary>由视图订阅：参数 true=确认 / false=取消，触发关闭对话框。</summary>
    public event Action<bool>? RequestClose;

    public InputDialogViewModel()
    {
        ConfirmCommand = new RelayCommand(
            () => RequestClose?.Invoke(true),
            () => !string.IsNullOrWhiteSpace(_value));
        CancelCommand = new RelayCommand(() => RequestClose?.Invoke(false));
    }

    /// <summary>首次编辑后开启错误提示。</summary>
    private void MarkTouched()
    {
        if (_touched) return;
        _touched = true;
        OnPropertyChanged(nameof(HasError));
    }

    string IDataErrorInfo.Error => "";
    public string this[string columnName]
        => columnName == nameof(Value) && _touched && string.IsNullOrWhiteSpace(Value)
            ? "内容不能为空"
            : "";

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
