using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using MiaoWu.App.ViewModels;

namespace MiaoWu.App.Views
{
    /// <summary>
    /// 行式文档视图：文本、代码、diff、命令输出共用。
    /// 行列表虚拟化；滚动位置按标签保存；查找命中时滚到对应行。
    /// </summary>
    public partial class LineDocumentView : UserControl
    {
        private ScrollViewer _scroll;
        private bool _suppressScroll;
        private LineDocumentViewModel _viewModel;

        public LineDocumentView()
        {
            InitializeComponent();
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
            DataContextChanged += OnDataContextChanged;
        }

        /// <summary>离屏快照使用：同步应用滚动位置。</summary>
        public void ApplyScrollOffsets()
        {
            if (_viewModel == null)
            {
                return;
            }

            EnsureScrollViewer();

            if (_scroll != null)
            {
                _suppressScroll = true;
                try
                {
                    _scroll.ScrollToVerticalOffset(_viewModel.ScrollOffset);
                }
                finally
                {
                    _suppressScroll = false;
                }
            }

            if (_viewModel.CurrentFindLine != null)
            {
                LineList.ScrollIntoView(_viewModel.CurrentFindLine);
            }
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            Subscribe();
            ApplyScrollOffsets();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            Unsubscribe();

            if (_scroll != null)
            {
                _scroll.ScrollChanged -= OnScrollChanged;
                _scroll = null;
            }
        }

        private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            Subscribe();
            Dispatcher.BeginInvoke(
                new Action(ApplyScrollOffsets),
                DispatcherPriority.Loaded);
        }

        private void Subscribe()
        {
            if (_viewModel != null)
            {
                _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            }

            _viewModel = DataContext as LineDocumentViewModel;

            if (_viewModel != null)
            {
                _viewModel.PropertyChanged += OnViewModelPropertyChanged;
            }
        }

        private void Unsubscribe()
        {
            if (_viewModel != null)
            {
                _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
                _viewModel = null;
            }
        }

        private void OnViewModelPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == "CurrentFindLine" && _viewModel != null && _viewModel.CurrentFindLine != null)
            {
                DocumentLineViewModel line = _viewModel.CurrentFindLine;
                Dispatcher.BeginInvoke(
                    new Action(() => LineList.ScrollIntoView(line)),
                    DispatcherPriority.Loaded);
            }
        }

        private void EnsureScrollViewer()
        {
            if (_scroll != null)
            {
                return;
            }

            LineList.ApplyTemplate();
            LineList.UpdateLayout();
            _scroll = FindScrollViewer(LineList);

            if (_scroll != null)
            {
                _scroll.ScrollChanged += OnScrollChanged;
                return;
            }

            EventHandler retry = null;
            retry = (sender, args) =>
            {
                ScrollViewer viewer = FindScrollViewer(LineList);
                if (viewer == null)
                {
                    return;
                }

                _scroll = viewer;
                _scroll.ScrollChanged += OnScrollChanged;
                LineList.LayoutUpdated -= retry;
                ApplyScrollOffsets();
            };

            LineList.LayoutUpdated += retry;
        }

        private static ScrollViewer FindScrollViewer(DependencyObject root)
        {
            var viewer = root as ScrollViewer;
            if (viewer != null)
            {
                return viewer;
            }

            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int index = 0; index < count; index++)
            {
                ScrollViewer found = FindScrollViewer(VisualTreeHelper.GetChild(root, index));
                if (found != null)
                {
                    return found;
                }
            }

            return null;
        }

        private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (_suppressScroll || _viewModel == null)
            {
                return;
            }

            _viewModel.ScrollOffset = _scroll == null ? 0 : _scroll.VerticalOffset;
        }
    }
}
