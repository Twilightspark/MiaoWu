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
        private const string TrayDescription =
            "关闭窗口后，智能体可以继续在系统托盘后台生成；选择退出应用会取消当前生成并保存已生成的内容。";

        private const string NoTrayDescription =
            "当前以受限权限运行（例如从 VS Code 或沙箱终端启动），Windows 不会显示系统托盘图标。"
            + "关闭窗口将最小化到任务栏；从资源管理器直接运行 MiaoWu.exe 可使用托盘。";

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

        public string Description
        {
            get { return _trayAvailable ? TrayDescription : NoTrayDescription; }
        }

        public string MinimizeLabel
        {
            get { return _trayAvailable ? "最小化到托盘" : "最小化到任务栏"; }
        }

        public string MinimizeAutomationName
        {
            get
            {
                return _trayAvailable
                    ? "最小化到系统托盘"
                    : "最小化到任务栏，保留任务栏按钮";
            }
        }

        /// <summary>托盘不可用时把按钮与说明改成“最小化到任务栏”，由 MainWindow 执行降级。</summary>
        public void SetTrayAvailability(bool available)
        {
            if (_trayAvailable == available)
            {
                return;
            }

            _trayAvailable = available;
            OnPropertyChanged(nameof(TrayAvailable));
            OnPropertyChanged(nameof(Description));
            OnPropertyChanged(nameof(MinimizeLabel));
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
