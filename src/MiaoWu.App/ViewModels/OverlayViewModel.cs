using MiaoWu.App.Mvvm;

namespace MiaoWu.App.ViewModels
{
    /// <summary>
    /// 主窗口内的模态浮层（P2 用于设置 / 能力市场占位）。
    /// 不新开 Window；Esc 关闭，关闭后焦点由 ShellView 还回原控件。
    /// </summary>
    public sealed class OverlayViewModel : ObservableObject
    {
        private bool _isOpen;
        private string _title;
        private string _body;

        public OverlayViewModel()
        {
            CloseCommand = new RelayCommand(Close);
        }

        public bool IsOpen
        {
            get { return _isOpen; }
            private set
            {
                if (SetProperty(ref _isOpen, value))
                {
                    OnPropertyChanged(nameof(IsClosed));
                }
            }
        }

        public bool IsClosed
        {
            get { return !_isOpen; }
        }

        public string Title
        {
            get { return _title; }
            private set { SetProperty(ref _title, value); }
        }

        public string Body
        {
            get { return _body; }
            private set { SetProperty(ref _body, value); }
        }

        public RelayCommand CloseCommand { get; }

        public void Show(string title, string body)
        {
            Title = title;
            Body = body;
            IsOpen = true;
        }

        public void Close()
        {
            IsOpen = false;
        }
    }
}
