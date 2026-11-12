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
    /// 左栏视图：工作区手风琴、会话列表（虚拟化 + 吸顶分组）、文件树（懒加载、横向滚动）、
    /// 底部固定入口。滚动位置按工作区保存与恢复。
    /// </summary>
    public partial class LeftPaneView : UserControl
    {
        private ScrollViewer _sessionsScroll;
        private ScrollViewer _treeScroll;
        private bool _suppressScroll;
        private LeftPaneViewModel _viewModel;
        private WorkspaceItemViewModel _watchedWorkspace;

        public LeftPaneView()
        {
            InitializeComponent();
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
            DataContextChanged += OnDataContextChanged;
        }

        /// <summary>离屏快照与测试可直接调用，避免依赖 Dispatcher 队列。</summary>
        public void ApplyScrollOffsets()
        {
            WorkspaceItemViewModel workspace = _viewModel == null ? null : _viewModel.ActiveWorkspace;
            if (workspace == null)
            {
                return;
            }

            EnsureScrollViewers();

            _suppressScroll = true;
            try
            {
                if (_sessionsScroll != null && SessionsList.Visibility == Visibility.Visible)
                {
                    _sessionsScroll.ScrollToVerticalOffset(workspace.ScrollOffset);
                }

                if (_treeScroll != null && TreeList.Visibility == Visibility.Visible)
                {
                    _treeScroll.ScrollToVerticalOffset(workspace.ScrollOffset);
                }
            }
            finally
            {
                _suppressScroll = false;
            }

            UpdateStickyHeader();
        }

        /// <summary>快照工具用：滚动到指定偏移并同步工作区状态。</summary>
        public void ScrollSessionsTo(double offset)
        {
            EnsureScrollViewers();

            if (_sessionsScroll == null)
            {
                return;
            }

            _suppressScroll = true;
            try
            {
                _sessionsScroll.ScrollToVerticalOffset(offset);
            }
            finally
            {
                _suppressScroll = false;
            }

            WorkspaceItemViewModel workspace = _viewModel == null ? null : _viewModel.ActiveWorkspace;
            if (workspace != null)
            {
                workspace.ScrollOffset = offset;
            }

            UpdateStickyHeader();
        }

        public void ScrollTreeTo(double offset)
        {
            EnsureScrollViewers();

            if (_treeScroll == null)
            {
                return;
            }

            _suppressScroll = true;
            try
            {
                _treeScroll.ScrollToVerticalOffset(offset);
            }
            finally
            {
                _suppressScroll = false;
            }

            WorkspaceItemViewModel workspace = _viewModel == null ? null : _viewModel.ActiveWorkspace;
            if (workspace != null)
            {
                workspace.ScrollOffset = offset;
            }
        }

        public void FocusInitial()
        {
            if (SessionsList.Visibility == Visibility.Visible)
            {
                Keyboard.Focus(SessionsList);
                return;
            }

            if (TreeList.Visibility == Visibility.Visible)
            {
                Keyboard.Focus(TreeList);
                return;
            }

            Keyboard.Focus(AddWorkspaceButton);
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            EnsureScrollViewers();
            Subscribe();
            QueueApplyScroll();
        }

        private void EnsureScrollViewers()
        {
            if (_sessionsScroll == null)
            {
                AttachScrollViewer(SessionsList, OnSessionsScroll, viewer => _sessionsScroll = viewer);
            }

            if (_treeScroll == null)
            {
                AttachScrollViewer(TreeList, OnTreeScroll, viewer => _treeScroll = viewer);
            }
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            if (_sessionsScroll != null)
            {
                _sessionsScroll.ScrollChanged -= OnSessionsScroll;
                _sessionsScroll = null;
            }

            if (_treeScroll != null)
            {
                _treeScroll.ScrollChanged -= OnTreeScroll;
                _treeScroll = null;
            }

            Unsubscribe();
        }

        private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            Unsubscribe();
            Subscribe();
            QueueApplyScroll();
        }

        private void Subscribe()
        {
            if (_viewModel != null)
            {
                _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            }

            _viewModel = DataContext as LeftPaneViewModel;
            if (_viewModel != null)
            {
                _viewModel.PropertyChanged += OnViewModelPropertyChanged;
            }

            WatchWorkspace(_viewModel == null ? null : _viewModel.ActiveWorkspace);
        }

        private void Unsubscribe()
        {
            if (_viewModel != null)
            {
                _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
                _viewModel = null;
            }

            WatchWorkspace(null);
        }

        private void OnViewModelPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == "ActiveWorkspace")
            {
                WatchWorkspace(_viewModel == null ? null : _viewModel.ActiveWorkspace);
                QueueApplyScroll();
                UpdateStickyHeader();
            }
        }

        private void WatchWorkspace(WorkspaceItemViewModel workspace)
        {
            if (ReferenceEquals(_watchedWorkspace, workspace))
            {
                return;
            }

            if (_watchedWorkspace != null)
            {
                _watchedWorkspace.PropertyChanged -= OnWorkspacePropertyChanged;
            }

            _watchedWorkspace = workspace;

            if (_watchedWorkspace != null)
            {
                _watchedWorkspace.PropertyChanged += OnWorkspacePropertyChanged;
            }
        }

        private void OnWorkspacePropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == "ShowSessionList"
                || e.PropertyName == "ShowFileTree"
                || e.PropertyName == "IsExpanded")
            {
                QueueApplyScroll();
                UpdateStickyHeader();
            }
        }

        private void QueueApplyScroll()
        {
            if (!IsLoaded)
            {
                return;
            }

            Dispatcher.BeginInvoke(
                new Action(ApplyScrollOffsets),
                DispatcherPriority.Loaded);
        }

        private void AttachScrollViewer(
            ListBox listBox,
            ScrollChangedEventHandler handler,
            Action<ScrollViewer> assign)
        {
            listBox.ApplyTemplate();
            listBox.UpdateLayout();

            ScrollViewer viewer = FindScrollViewer(listBox);
            if (viewer != null)
            {
                assign(viewer);
                viewer.ScrollChanged += handler;
                return;
            }

            // 模板可能还没生成；LayoutUpdated 时再试一次。
            EventHandler retry = null;
            retry = (sender, args) =>
            {
                ScrollViewer retried = FindScrollViewer(listBox);
                if (retried == null)
                {
                    return;
                }

                assign(retried);
                retried.ScrollChanged += handler;
                listBox.LayoutUpdated -= retry;
                ApplyScrollOffsets();
            };

            listBox.LayoutUpdated += retry;
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

        private void OnSessionsScroll(object sender, ScrollChangedEventArgs e)
        {
            if (_suppressScroll)
            {
                return;
            }

            WorkspaceItemViewModel workspace = _viewModel == null ? null : _viewModel.ActiveWorkspace;
            if (workspace == null || SessionsList.Visibility != Visibility.Visible)
            {
                return;
            }

            workspace.ScrollOffset = _sessionsScroll == null ? 0 : _sessionsScroll.VerticalOffset;
            UpdateStickyHeader();
        }

        private void OnTreeScroll(object sender, ScrollChangedEventArgs e)
        {
            if (_suppressScroll)
            {
                return;
            }

            WorkspaceItemViewModel workspace = _viewModel == null ? null : _viewModel.ActiveWorkspace;
            if (workspace == null || TreeList.Visibility != Visibility.Visible)
            {
                return;
            }

            workspace.ScrollOffset = _treeScroll == null ? 0 : _treeScroll.VerticalOffset;
        }

        private void UpdateStickyHeader()
        {
            WorkspaceItemViewModel workspace = _viewModel == null ? null : _viewModel.ActiveWorkspace;
            if (workspace == null || SessionsList.Visibility != Visibility.Visible)
            {
                if (workspace != null)
                {
                    workspace.IsStickyHeaderVisible = false;
                }

                return;
            }

            double offset = _sessionsScroll == null ? 0 : _sessionsScroll.VerticalOffset;
            if (offset < 1)
            {
                workspace.IsStickyHeaderVisible = false;
                return;
            }

            for (int index = 0; index < SessionsList.Items.Count; index++)
            {
                var container = SessionsList.ItemContainerGenerator.ContainerFromIndex(index) as ListBoxItem;
                if (container == null)
                {
                    continue;
                }

                Point origin = container.TransformToAncestor(SessionsList).Transform(new Point(0, 0));
                if (origin.Y + container.ActualHeight < 26)
                {
                    continue;
                }

                var row = container.DataContext as SessionRowViewModel;
                if (row == null)
                {
                    continue;
                }

                workspace.StickyHeaderText = LeftPaneViewModel.GetGroupHeaderText(row.Group);
                workspace.IsStickyHeaderVisible = true;
                return;
            }

            workspace.IsStickyHeaderVisible = false;
        }

        private void OnTreePreviewKeyDown(object sender, KeyEventArgs e)
        {
            WorkspaceItemViewModel workspace = _viewModel == null ? null : _viewModel.ActiveWorkspace;
            FileTreeRowViewModel row = workspace == null ? null : workspace.SelectedFile;
            if (row == null)
            {
                return;
            }

            if (e.Key == Key.Right && row.IsDirectory && !row.IsExpanded)
            {
                workspace.ToggleNode(row);
                e.Handled = true;
            }
            else if (e.Key == Key.Left && row.IsDirectory && row.IsExpanded)
            {
                workspace.CollapseNode(row);
                e.Handled = true;
            }
            else if (e.Key == Key.Enter)
            {
                workspace.OpenNode(row);
                e.Handled = true;
            }
        }

        private void OnTreeMouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (FindAncestor<Button>((DependencyObject)e.OriginalSource) != null)
            {
                return;
            }

            var container = ItemsControl.ContainerFromElement(
                TreeList,
                (DependencyObject)e.OriginalSource) as ListBoxItem;

            var row = container == null ? null : container.DataContext as FileTreeRowViewModel;
            WorkspaceItemViewModel workspace = _viewModel == null ? null : _viewModel.ActiveWorkspace;
            if (row == null || workspace == null)
            {
                return;
            }

            if (row.IsDirectory)
            {
                workspace.ToggleNode(row);
            }
            else
            {
                workspace.OpenNode(row);
            }

            e.Handled = true;
        }

        private static T FindAncestor<T>(DependencyObject start) where T : DependencyObject
        {
            DependencyObject current = start;
            while (current != null)
            {
                var match = current as T;
                if (match != null)
                {
                    return match;
                }

                current = VisualTreeHelper.GetParent(current);
            }

            return null;
        }
    }
}
