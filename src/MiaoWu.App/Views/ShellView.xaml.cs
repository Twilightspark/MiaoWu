using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MiaoWu.App.ViewModels;

namespace MiaoWu.App.Views
{
    public partial class ShellView : UserControl
    {
        public ShellView()
        {
            InitializeComponent();
            Loaded += OnLoaded;
        }

        private MainWindowViewModel ViewModel
        {
            get { return DataContext as MainWindowViewModel; }
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            MainWindowViewModel viewModel = ViewModel;
            if (viewModel == null)
            {
                return;
            }

            viewModel.ApplyLayout(ActualWidth);
            FocusInitial();
        }

        private void OnRootSizeChanged(object sender, SizeChangedEventArgs e)
        {
            MainWindowViewModel viewModel = ViewModel;
            if (viewModel != null)
            {
                viewModel.ApplyLayout(e.NewSize.Width);
            }
        }

        /// <summary>初始焦点放在左栏会话列表，避免焦点被顶栏吃掉。</summary>
        public void FocusInitial()
        {
            LeftPane.FocusInitial();
        }

        private void OnMinimizeClick(object sender, RoutedEventArgs e)
        {
            Window window = Window.GetWindow(this);
            if (window != null)
            {
                SystemCommands.MinimizeWindow(window);
            }
        }

        private void OnMaximizeRestoreClick(object sender, RoutedEventArgs e)
        {
            Window window = Window.GetWindow(this);
            if (window == null)
            {
                return;
            }

            if (window.WindowState == WindowState.Maximized)
            {
                SystemCommands.RestoreWindow(window);
            }
            else
            {
                SystemCommands.MaximizeWindow(window);
            }
        }

        private void OnCloseClick(object sender, RoutedEventArgs e)
        {
            Window window = Window.GetWindow(this);
            if (window != null)
            {
                window.Close();
            }
        }

        /// <summary>Esc 优先收定时任务页面；设置、浮层与确认框打开时仍交给它们处理。</summary>
        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Escape || e.Handled)
            {
                return;
            }

            MainWindowViewModel viewModel = ViewModel;
            if (viewModel == null || viewModel.Schedules == null || !viewModel.Schedules.IsOpen)
            {
                return;
            }

            if ((viewModel.Settings != null && viewModel.Settings.IsOpen)
                || viewModel.Overlay.IsOpen
                || viewModel.ConfirmPrompt.IsOpen
                || viewModel.ClosePrompt.IsOpen)
            {
                return;
            }

            viewModel.Schedules.Close();
            e.Handled = true;
        }
    }
}
