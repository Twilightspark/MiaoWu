using System;
using MiaoWu.App.Mvvm;

namespace MiaoWu.App.ViewModels
{
    /// <summary>关闭确认浮层的三种出口。</summary>
    public enum ClosePromptChoice
    {
        Cancel,
        MinimizeToTray,
        Exit
    }

    public sealed class ClosePromptDecidedEventArgs : EventArgs
    {
        public ClosePromptDecidedEventArgs(ClosePromptChoice choice, bool remember)
        {
            Choice = choice;
            Remember = remember;
        }

        public ClosePromptChoice Choice { get; }

        /// <summary>用户勾选了“记住我的选择，下次不再询问”。</summary>
        public bool Remember { get; }
    }

    /// <summary>
    /// 主窗口内的关闭确认浮层。只负责“问”，不直接隐藏窗口或退出应用：
    /// 决定通过 <see cref="Decided"/> 交给 MainWindow，由它走隐藏到托盘或退出路径。
    /// </summary>
    public sealed class ClosePromptViewModel : ObservableObject
    {
        private bool _isOpen;
        private bool _rememberChoice;
        private bool _trayAvailable = true;

        public ClosePromptViewModel()
        {
            MinimizeToTrayCommand = new RelayCommand(
                () => Decide(ClosePromptChoice.MinimizeToTray));
            ExitCommand = new RelayCommand(() => Decide(ClosePromptChoice.Exit));
            CancelCommand = new RelayCommand(Cancel);
        }

        public event EventHandler<ClosePromptDecidedEventArgs> Decided;

        public RelayCommand MinimizeToTrayCommand { get; }

        public RelayCommand ExitCommand { get; }

        public RelayCommand CancelCommand { get; }

        public bool IsOpen
        {
            get { return _isOpen; }
            private set { SetProperty(ref _isOpen, value); }
        }

        public bool TrayAvailable
        {
            get { return _trayAvailable; }
        }

        public string MinimizeAutomationName
        {
            get
            {
                return _trayAvailable
                    ? "后台运行，最小化到系统托盘"
                    : "后台运行，最小化到任务栏";
            }
        }

        /// <summary>托盘不可用时只影响读屏名：按钮文案固定为“后台运行”，由 MainWindow 降级到任务栏。</summary>
        public void SetTrayAvailability(bool available)
        {
            if (_trayAvailable == available)
            {
                return;
            }

            _trayAvailable = available;
            OnPropertyChanged(nameof(TrayAvailable));
            OnPropertyChanged(nameof(MinimizeAutomationName));
        }

        /// <summary>每次打开都恢复未勾选，避免上次的“记住”被无意带到下一次询问。</summary>
        public bool RememberChoice
        {
            get { return _rememberChoice; }
            set { SetProperty(ref _rememberChoice, value); }
        }

        public void Open()
        {
            RememberChoice = false;
            IsOpen = true;
        }

        public void Cancel()
        {
            IsOpen = false;
        }

        private void Decide(ClosePromptChoice choice)
        {
            if (!IsOpen)
            {
                return;
            }

            bool remember = RememberChoice;
            IsOpen = false;

            EventHandler<ClosePromptDecidedEventArgs> handler = Decided;
            if (handler != null)
            {
                handler(this, new ClosePromptDecidedEventArgs(choice, remember));
            }
        }
    }
}
