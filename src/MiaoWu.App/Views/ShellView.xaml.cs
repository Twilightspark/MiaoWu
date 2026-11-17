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
            RightPane.SizeChanged += OnRightPaneSizeChanged;
            RightPane.LayoutUpdated += OnRightPaneLayoutUpdated;
        }

        private void OnRightPaneSizeChanged(object sender, SizeChangedEventArgs e)
        {
            MainWindowViewModel viewModel = ViewModel;
            if (viewModel != null)
            {
                viewModel.SetRightPaneVisualWidth(e.NewSize.Width);
            }
        }

        private void OnRightPaneLayoutUpdated(object sender, EventArgs e)
        {
            MainWindowViewModel viewModel = ViewModel;
            if (viewModel != null)
            {
                viewModel.SetRightPaneVisualWidth(RightPane.ActualWidth);
            }
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

        /// <summary>标题栏工作区左键打开菜单；键盘 Enter 同样走 Click。</summary>
        private void OnWorkspaceChipClick(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            if (button == null || !BuildWorkspaceMenu(button))
            {
                return;
            }

            button.ContextMenu.PlacementTarget = button;
            button.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            button.ContextMenu.IsOpen = true;
        }

        /// <summary>右键 / Shift+F10 走系统菜单时也要有内容，不能弹空卡片。</summary>
        private void OnWorkspaceMenuOpening(object sender, ContextMenuEventArgs e)
        {
            var button = sender as Button;
            if (button == null || !BuildWorkspaceMenu(button))
            {
                e.Handled = true;
            }
        }

        private bool BuildWorkspaceMenu(Button button)
        {
            MainWindowViewModel viewModel = ViewModel;
            if (button == null || button.ContextMenu == null || viewModel == null)
            {
                return false;
            }

            ContextMenu menu = button.ContextMenu;
            menu.Items.Clear();
            foreach (WorkspaceOption option in viewModel.WorkspaceOptions)
            {
                var item = new MenuItem
                {
                    Header = option.Name,
                    IsChecked = option.IsCurrent,
                    Command = viewModel.SwitchWorkspaceCommand,
                    CommandParameter = option,
                    ToolTip = option.IsCurrent ? "当前工作区" : null
                };

                if (!option.IsCurrent)
                {
                    item.Icon = new System.Windows.Shapes.Path
                    {
                        Style = (Style)FindResource("Icon.Menu"),
                        Data = (System.Windows.Media.Geometry)FindResource("Ico.Folder")
                    };
                }

                menu.Items.Add(item);
            }

            return menu.Items.Count > 0;
        }
    }
}
