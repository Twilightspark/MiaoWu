using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using MiaoWu.App.Services;
using MiaoWu.App.ViewModels;
using MiaoWu.Core.Layout;

namespace MiaoWu.App.Views
{
    /// <summary>
    /// 主窗口只负责窗口级行为：窗口壳、启动位置、尺寸/状态持久化。
    /// 三栏结构在 <see cref="ShellView"/> 中，便于离屏渲染与后续阶段复用。
    /// </summary>
    public partial class MainWindow : Window
    {
        private readonly MainWindowViewModel _viewModel;
        private readonly DispatcherTimer _saveTimer;
        private WindowChromeController _chromeController;

        public MainWindow(MainWindowViewModel viewModel)
        {
            _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));

            InitializeComponent();
            DataContext = _viewModel;

            ApplyStartupPlacement();

            // 首次引导不再强制最大化：窗口按主屏幕工作区居中，尺寸沿用上次退出时的值。

            // 必须在 Show 之前设置：Win11 用 DWM 画窗口圆角与系统投影。
            WindowChromeController.ConfigureSystemFrame(this);

            _saveTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(600)
            };
            _saveTimer.Tick += OnSaveTimerTick;

            _viewModel.StateChanged += OnViewModelStateChanged;
            StateChanged += OnWindowStateChanged;
            SizeChanged += OnWindowSizeChanged;
            SourceInitialized += OnSourceInitialized;
            Closing += OnClosing;
            IsVisibleChanged += OnIsVisibleChanged;
            Closed += OnClosed;
        }

        private void ApplyStartupPlacement()
        {
            Rect workArea = SystemParameters.WorkArea;
            var metrics = new WindowPlacementMetrics
            {
                DefaultWidth = _viewModel.PreferredWindowWidth,
                DefaultHeight = _viewModel.PreferredWindowHeight,
                MinimumWidth = _viewModel.MinimumWindowWidth,
                MinimumHeight = _viewModel.MinimumWindowHeight,
                RestoreSavedSize = _viewModel.HasSavedWindowPlacement
            };

            WindowPlacement placement = WindowPlacementPolicy.Compute(
                workArea.Left,
                workArea.Top,
                workArea.Width,
                workArea.Height,
                metrics);

            Width = placement.Width;
            Height = placement.Height;
            Left = placement.Left;
            Top = placement.Top;
            MinWidth = placement.MinimumWidth;
            MinHeight = placement.MinimumHeight;
        }

        private void OnSourceInitialized(object sender, EventArgs e)
        {
            _chromeController = new WindowChromeController(this);
            _chromeController.Attach();
        }

        private void OnWindowStateChanged(object sender, EventArgs e)
        {
            if (WindowState == WindowState.Minimized)
            {
                ScheduleSave();
                return;
            }

            if (WindowState == WindowState.Maximized)
            {
                _viewModel.SetWindowMaximized(true);
            }
            else
            {
                // 尺寸只由正常状态下的 SizeChanged 记录；这里只同步最大化标记，
                // 避免 WindowChrome/系统还原边界把窗口尺寸改写。
                _viewModel.SetWindowMaximized(false);
            }

            ScheduleSave();
        }

        private void OnWindowSizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (WindowState == WindowState.Minimized)
            {
                return;
            }

            if (WindowState == WindowState.Maximized)
            {
                _viewModel.SetWindowMaximized(true);
                ScheduleSave();
                return;
            }

            _viewModel.UpdateWindowBounds(e.NewSize.Width, e.NewSize.Height, false);
            ScheduleSave();
        }

        private void OnViewModelStateChanged(object sender, EventArgs e)
        {
            ScheduleSave();
        }

        private void ScheduleSave()
        {
            if (_saveTimer == null)
            {
                return;
            }

            _saveTimer.Stop();
            _saveTimer.Start();
        }

        private void OnSaveTimerTick(object sender, EventArgs e)
        {
            if (_saveTimer != null)
            {
                _saveTimer.Stop();
            }

            _viewModel.TrySaveState();
        }

        private void OnClosed(object sender, EventArgs e)
        {
            if (_saveTimer != null)
            {
                _saveTimer.Stop();
                _saveTimer.Tick -= OnSaveTimerTick;
            }

            if (_viewModel != null)
            {
                _viewModel.StateChanged -= OnViewModelStateChanged;

                // 尺寸已由正常状态下的 SizeChanged 记录；关闭时只保存，不再回写窗口边界。
                _viewModel.TrySaveState();
            }

            if (_chromeController != null)
            {
                _chromeController.Detach();
                _chromeController = null;
            }
        }

        private void OnClosing(object sender, CancelEventArgs e)
        {
            if (_viewModel == null || _viewModel.IsExiting)
            {
                return;
            }

            // 首次引导阶段关闭窗口等于退出应用，不隐藏到托盘。
            if (_viewModel.IsOnboarding)
            {
                e.Cancel = true;
                _viewModel.RequestExit();
                return;
            }

            if (_viewModel.KeepRunningInTray)
            {
                e.Cancel = true;
                _viewModel.TrySaveState();
                _viewModel.SuspendConversation();
                Hide();
                _viewModel.NotifyHiddenToTray();
                return;
            }

            e.Cancel = true;
            _viewModel.RequestExit();
        }

        private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (e.NewValue is bool && (bool)e.NewValue && _viewModel != null)
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (_viewModel.IsOnboarding)
                    {
                        return;
                    }

                    _viewModel.ApplyLayout(Shell != null && Shell.ActualWidth > 0
                        ? Shell.ActualWidth
                        : ActualWidth);
                    _viewModel.ResumeConversation();
                }));
            }
        }
    }
}
