using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MiaoWu.App.ViewModels;

namespace MiaoWu.App.Views
{
    /// <summary>
    /// 中栏视图：标签行的横向滚动、关闭后的焦点落位、选中标签自动滚入可视区。
    /// </summary>
    public partial class CanvasPaneView : UserControl
    {
        private CanvasViewModel _viewModel;

        public CanvasPaneView()
        {
            InitializeComponent();
            Loaded += OnLoaded;
            DataContextChanged += OnDataContextChanged;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            Subscribe();
            EnsureSelectedTabVisible();
        }

        private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            Subscribe();
            EnsureSelectedTabVisible();
        }

        private void Subscribe()
        {
            if (_viewModel != null)
            {
                _viewModel.TabFocusRequested -= OnTabFocusRequested;
                _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            }

            _viewModel = DataContext as CanvasViewModel;

            if (_viewModel != null)
            {
                _viewModel.TabFocusRequested += OnTabFocusRequested;
                _viewModel.PropertyChanged += OnViewModelPropertyChanged;
            }
        }

        private void OnViewModelPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == "SelectedTab")
            {
                Dispatcher.BeginInvoke(
                    new Action(EnsureSelectedTabVisible),
                    DispatcherPriority.Loaded);
            }
        }

        private void OnTabFocusRequested(object sender, TabFocusRequestedEventArgs e)
        {
            Dispatcher.BeginInvoke(
                new Action(() => FocusTab(e.Tab)),
                DispatcherPriority.Input);
        }

        private void EnsureSelectedTabVisible()
        {
            if (_viewModel == null || _viewModel.SelectedTab == null)
            {
                return;
            }

            var container = TabItems.ItemContainerGenerator.ContainerFromItem(_viewModel.SelectedTab)
                as FrameworkElement;

            if (container != null)
            {
                container.BringIntoView();
            }
        }

        private void FocusTab(DocumentTabViewModel tab)
        {
            var container = TabItems.ItemContainerGenerator.ContainerFromItem(tab);
            if (container == null)
            {
                return;
            }

            FrameworkElement focusable = FindFocusable(container);
            if (focusable != null)
            {
                Keyboard.Focus(focusable);
            }
        }

        private static FrameworkElement FindFocusable(DependencyObject root)
        {
            var element = root as FrameworkElement;
            if (element != null && element.Focusable)
            {
                return element;
            }

            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int index = 0; index < count; index++)
            {
                FrameworkElement found = FindFocusable(VisualTreeHelper.GetChild(root, index));
                if (found != null)
                {
                    return found;
                }
            }

            return null;
        }

        private void OnTabScrollerMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (TabScroller.ScrollableWidth <= 0)
            {
                return;
            }

            TabScroller.ScrollToHorizontalOffset(TabScroller.HorizontalOffset - e.Delta);
            e.Handled = true;
        }
    }
}
