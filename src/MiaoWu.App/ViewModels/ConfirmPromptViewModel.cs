using System;
using System.Windows;
using System.Windows.Threading;
using MiaoWu.App.Mvvm;

namespace MiaoWu.App.ViewModels
{
    public sealed class ConfirmPromptDecidedEventArgs : EventArgs
    {
        public ConfirmPromptDecidedEventArgs(bool accepted)
        {
            Accepted = accepted;
        }

        public bool Accepted { get; }
    }

    /// <summary>
    /// 主窗口内的通用二次确认浮层：删除会话 / 删除文件 / 清除会话 / 撤销写入等。
    /// 与关闭确认浮层共用同一套表面（遮罩 + 400 宽卡片 + 44 高标题行 + 右上角取消图标），
    /// 底部只留“确认（危险操作用 Danger 前景色）/ 取消（Primary）”两个 28 高按钮。
    /// </summary>
    /// <remarks>
    /// <see cref="Confirm"/> 用嵌套消息循环同步等待用户选择，调用方仍是同步方法，
    /// 行为与原来的系统 MessageBox 一致；浮层本身不新开 Window。
    /// </remarks>
    public sealed class ConfirmPromptViewModel : ObservableObject
    {
        private string _title = string.Empty;
        private string _message = string.Empty;
        private string _confirmText = string.Empty;
        private string _cancelText = string.Empty;
        private bool _isOpen;
        private bool _isDestructive = true;

        public ConfirmPromptViewModel()
        {
            // 不给 CanExecute 挂 IsOpen：命令的可用性靠 CommandManager 重查询，
            // 浮层刚打开时可能还没重查询，按钮会停在禁用态。两个方法内部已自带 IsOpen 守卫。
            ConfirmCommand = new RelayCommand(Accept);
            CancelCommand = new RelayCommand(Cancel);
        }

        /// <summary>用户做出选择后触发；<see cref="ConfirmPromptDecidedEventArgs.Accepted"/> 为是否确认。</summary>
        public event EventHandler<ConfirmPromptDecidedEventArgs> Decided;

        public RelayCommand ConfirmCommand { get; }

        public RelayCommand CancelCommand { get; }

        public bool IsOpen
        {
            get { return _isOpen; }
            private set { SetProperty(ref _isOpen, value); }
        }

        public string Title
        {
            get { return _title; }
            private set { SetProperty(ref _title, value); }
        }

        public string Message
        {
            get { return _message; }
            private set { SetProperty(ref _message, value); }
        }

        /// <summary>确认按钮文案，例如“删除”“撤销写入”。</summary>
        public string ConfirmText
        {
            get { return _confirmText; }
            private set { SetProperty(ref _confirmText, value); }
        }

        /// <summary>取消按钮文案，通常为“取消”。</summary>
        public string CancelText
        {
            get { return _cancelText; }
            private set { SetProperty(ref _cancelText, value); }
        }

        /// <summary>危险操作：确认按钮用 Danger 前景色；只从列表移除工作区这类可逆操作传 false。</summary>
        public bool IsDestructive
        {
            get { return _isDestructive; }
            private set { SetProperty(ref _isDestructive, value); }
        }

        /// <summary>
        /// 同步询问用户：打开浮层并阻塞到用户点确认 / 取消 / Esc / 点遮罩。
        /// 没有 UI 线程可用（例如纯单元测试环境）时按取消处理，绝不静默放行删除。
        /// </summary>
        public bool Confirm(
            string title,
            string message,
            string confirmText,
            string cancelText,
            bool destructive = true)
        {
            Dispatcher dispatcher = Application.Current == null ? null : Application.Current.Dispatcher;
            if (dispatcher == null || dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
            {
                return false;
            }

            if (!dispatcher.CheckAccess())
            {
                return (bool)dispatcher.Invoke(
                    new Func<bool>(() => ConfirmCore(title, message, confirmText, cancelText, destructive)));
            }

            return ConfirmCore(title, message, confirmText, cancelText, destructive);
        }

        /// <summary>只打开浮层、不阻塞；离屏快照与探针用。</summary>
        public void Open(
            string title,
            string message,
            string confirmText,
            string cancelText,
            bool destructive = true)
        {
            if (IsOpen)
            {
                return;
            }

            Apply(title, message, confirmText, cancelText, destructive);
            IsOpen = true;
        }

        public void Accept()
        {
            if (!IsOpen)
            {
                return;
            }

            IsOpen = false;
            RaiseDecided(true);
        }

        public void Cancel()
        {
            if (!IsOpen)
            {
                return;
            }

            IsOpen = false;
            RaiseDecided(false);
        }

        private bool ConfirmCore(
            string title,
            string message,
            string confirmText,
            string cancelText,
            bool destructive)
        {
            if (IsOpen)
            {
                return false;
            }

            Apply(title, message, confirmText, cancelText, destructive);

            bool accepted = false;
            var frame = new DispatcherFrame();
            EventHandler<ConfirmPromptDecidedEventArgs> handler = (sender, args) =>
            {
                accepted = args.Accepted;
                frame.Continue = false;
            };

            Decided += handler;
            try
            {
                IsOpen = true;

                // 与 MessageBox 一样跑一个嵌套消息循环：用户选择前调用方停在原地，
                // 选择后 Continue=false 让 PushFrame 返回。
                Dispatcher.PushFrame(frame);
            }
            finally
            {
                Decided -= handler;
                IsOpen = false;
            }

            return accepted;
        }

        private void Apply(
            string title,
            string message,
            string confirmText,
            string cancelText,
            bool destructive)
        {
            Title = title ?? string.Empty;
            Message = message ?? string.Empty;
            ConfirmText = confirmText ?? string.Empty;
            CancelText = cancelText ?? string.Empty;
            IsDestructive = destructive;
        }

        private void RaiseDecided(bool accepted)
        {
            EventHandler<ConfirmPromptDecidedEventArgs> handler = Decided;
            if (handler != null)
            {
                handler(this, new ConfirmPromptDecidedEventArgs(accepted));
            }
        }
    }
}
