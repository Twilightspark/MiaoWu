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
                DefaultHeight = _viewModel.PreferredWindowHeight
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

            if (_viewModel.PreferredWindowMaximized)
            {
                WindowState = WindowState.Maximized;
            }
        }

        private void OnSourceInitialized(object sender, EventArgs e)
        {
            _chromeController = new WindowChromeController(this);
            _chromeController.Attach();
        }

        private void OnWindowStateChanged(object sender, EventArgs e)
        {            bool maximized = WindowState == WindowState.Maximized;
            _viewModel.IsMaximized = maximized;
            _viewModel.UpdateWindowBounds(ActualWidth, ActualHeight, maximized);
            ScheduleSave();
        }

        private void OnWindowSizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (WindowState == WindowState.Minimized)
            {
                return;
            }

            _viewModel.UpdateWindowBounds(e.NewSize.Width, e.NewSize.Height, WindowState == WindowState.Maximized);
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
                _viewModel.UpdateWindowBounds(ActualWidth, ActualHeight, WindowState == WindowState.Maximized);
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
                    _viewModel.ApplyLayout(ActualWidth);
                    _viewModel.ResumeConversation();
                }));
            }
        }
    }
}
