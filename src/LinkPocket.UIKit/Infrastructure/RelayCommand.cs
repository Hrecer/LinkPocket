using System;
using System.Windows.Input;

namespace LinkPocket.ViewModels
{
    /// <summary>
    /// 无参命令：<c>CanExecuteChanged</c> 走**显式 + 隐式两条路**——
    /// 显式 = <see cref="RaiseCanExecuteChanged"/>（经 <see cref="CommandRefresh"/> 同步触发，立即生效）；
    /// 隐式 = WPF 的 <see cref="CommandManager.RequerySuggested"/>（焦点/输入变化时的兜底重查）。
    /// </summary>
    /// <remarks>
    /// ⚠️ 只靠隐式那一条会有可感知延迟：WPF 的 `InvalidateRequerySuggested()` 把重查排在
    /// <c>DispatcherPriority.Background</c>，界面忙时被饿住（取消选中后按钮要过一会才变灰）。
    /// 因此凡"改变 CanExecute 依据"的状态写入点都要调 <see cref="CommandRefresh.Request"/>。
    /// </remarks>
    public class RelayCommand : ICommand, ICommandRefreshable
    {
        private readonly Action _execute;
        private readonly Func<bool>? _canExecute;

        /// <summary>显式订阅者（与 CommandManager 那条隐式路并行存在，互不干扰）。</summary>
        private event EventHandler? ExplicitCanExecuteChanged;

        public event EventHandler? CanExecuteChanged
        {
            add
            {
                ExplicitCanExecuteChanged += value;
                CommandManager.RequerySuggested += value;
            }
            remove
            {
                ExplicitCanExecuteChanged -= value;
                CommandManager.RequerySuggested -= value;
            }
        }

        public RelayCommand(Action execute, Func<bool>? canExecute = null)
        {
            _execute = execute ?? throw new ArgumentNullException(nameof(execute));
            _canExecute = canExecute;
            CommandRefresh.Register(this);
        }

        public bool CanExecute(object? parameter) => _canExecute?.Invoke() ?? true;

        /// <summary>立即重估（同步；不再只依赖 Background 级的隐式重查）。</summary>
        public void RaiseCanExecuteChanged() => ExplicitCanExecuteChanged?.Invoke(this, EventArgs.Empty);

        public void Execute(object? parameter) => _execute();
    }

    /// <summary>带参命令（语义与 <see cref="RelayCommand"/> 完全一致）。</summary>
    public class RelayCommand<T> : ICommand, ICommandRefreshable
    {
        private readonly Action<T?> _execute;
        private readonly Func<T?, bool>? _canExecute;
        private event EventHandler? ExplicitCanExecuteChanged;

        public event EventHandler? CanExecuteChanged
        {
            add
            {
                ExplicitCanExecuteChanged += value;
                CommandManager.RequerySuggested += value;
            }
            remove
            {
                ExplicitCanExecuteChanged -= value;
                CommandManager.RequerySuggested -= value;
            }
        }

        public RelayCommand(Action<T?> execute, Func<T?, bool>? canExecute = null)
        {
            _execute = execute ?? throw new ArgumentNullException(nameof(execute));
            _canExecute = canExecute;
            CommandRefresh.Register(this);
        }

        public bool CanExecute(object? parameter)
        {
            if (_canExecute == null) return true;
            return _canExecute((T?)parameter);
        }

        public void Execute(object? parameter) => _execute((T?)parameter);

        /// <summary>与无泛型版对齐：立即重估命令可用性。</summary>
        public void RaiseCanExecuteChanged() => ExplicitCanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}
