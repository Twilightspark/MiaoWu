using System;
using System.Threading.Tasks;
using System.Windows.Input;

namespace MiaoWu.App.Mvvm
{
    /// <summary>
    /// 异步命令：执行期间禁用自身，失败时通过 <see cref="ExecutionFailed"/> 交给上层提示，
    /// 不让异常逃逸到 Dispatcher。UI 线程只做绑定更新。
    /// </summary>
    public sealed class AsyncRelayCommand : ICommand
    {
        private readonly Func<object, Task> _execute;
        private readonly Predicate<object> _canExecute;
        private bool _isRunning;

        public AsyncRelayCommand(Func<Task> execute, Func<bool> canExecute = null)
            : this(
                _ => execute(),
                canExecute == null ? (Predicate<object>)null : _ => canExecute())
        {
        }

        public AsyncRelayCommand(Func<object, Task> execute, Predicate<object> canExecute = null)
        {
            _execute = execute ?? throw new ArgumentNullException(nameof(execute));
            _canExecute = canExecute;
        }

        public event EventHandler<Exception> ExecutionFailed;

        public bool IsRunning
        {
            get { return _isRunning; }
        }

        public event EventHandler CanExecuteChanged
        {
            add { CommandManager.RequerySuggested += value; }
            remove { CommandManager.RequerySuggested -= value; }
        }

        public bool CanExecute(object parameter)
        {
            return !_isRunning && (_canExecute == null || _canExecute(parameter));
        }

        public async void Execute(object parameter)
        {
            if (!CanExecute(parameter))
            {
                return;
            }

            _isRunning = true;
            CommandManager.InvalidateRequerySuggested();

            try
            {
                await _execute(parameter);
            }
            catch (Exception ex)
            {
                ExecutionFailed?.Invoke(this, ex);
            }
            finally
            {
                _isRunning = false;
                CommandManager.InvalidateRequerySuggested();
            }
        }

        public void RaiseCanExecuteChanged()
        {
            CommandManager.InvalidateRequerySuggested();
        }
    }
}
