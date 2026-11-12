using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using MiaoWu.App.ViewModels;

namespace MiaoWu.App.Views
{
    /// <summary>
    /// Markdown 预览：块级 ItemsControl（不用 FlowDocument），648 行宽、左对齐；
    /// 查找命中时把对应块滚入视野。
    /// </summary>
    public partial class MarkdownDocumentView : UserControl
    {
        private MarkdownDocumentViewModel _viewModel;
        private bool _suppressScroll;

        public MarkdownDocumentView()
        {
            InitializeComponent();
            Loaded += OnLoaded;
            DataContextChanged += OnDataContextChanged;
            Scroller.ScrollChanged += OnScrollChanged;
        }

        public void ApplyScrollOffsets()
        {
            if (_viewModel == null)
            {
                return;
            }

            _suppressScroll = true;
            try
            {
                Scroller.ScrollToVerticalOffset(_viewModel.ScrollOffset);
            }
            finally
            {
                _suppressScroll = false;
            }

            if (_viewModel.CurrentFindBlock != null)
            {
                BringBlockIntoView(_viewModel.CurrentFindBlock);
            }
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            Subscribe();
            ApplyScrollOffsets();
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

            _viewModel = DataContext as MarkdownDocumentViewModel;

            if (_viewModel != null)
            {
                _viewModel.PropertyChanged += OnViewModelPropertyChanged;
            }
        }

        private void OnViewModelPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != "CurrentFindBlock" || _viewModel == null)
            {
                return;
            }

            MarkdownBlockViewModel block = _viewModel.CurrentFindBlock;
            if (block == null)
            {
                return;
            }

            Dispatcher.BeginInvoke(
                new Action(() => BringBlockIntoView(block)),
                DispatcherPriority.Loaded);
        }

        private void BringBlockIntoView(MarkdownBlockViewModel block)
        {
            var container = BlocksHost.ItemContainerGenerator.ContainerFromItem(block) as FrameworkElement;
            if (container != null)
            {
                container.BringIntoView();
            }
        }

        private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (_suppressScroll || _viewModel == null)
            {
                return;
            }

            _viewModel.ScrollOffset = Scroller.VerticalOffset;
        }
    }
}
