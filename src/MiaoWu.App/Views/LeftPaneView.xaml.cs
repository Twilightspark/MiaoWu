using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MiaoWu.App.ViewModels;

namespace MiaoWu.App.Views
{
    /// <summary>
    /// 左栏视图：顶部功能入口、工作区手风琴（单击展开/收起，会话/文件紧跟在工作区下方）、
    /// 底部固定入口。整栏只有最外层一个垂直滚动条；文件树在深度过大时保留自身的横向滚动。
    /// </summary>
    public partial class LeftPaneView : UserControl
    {
        /// <summary>滚轮一行的兜底像素高度；系统“每次滚动行数”默认 3 行 ≈ 48px。</summary>
        private const double WheelLineHeight = 16;

        /// <summary>同一行两次单击的合并窗口；覆盖默认 500ms 双击，避免双击导致开→关。</summary>
        private const double FolderToggleDebounceMs = 500;

        private LeftPaneViewModel _viewModel;

        private FileTreeRowViewModel _lastToggledRow;

        private DateTime _lastToggleAt = DateTime.MinValue;

        public LeftPaneView()
        {
            InitializeComponent();
            DataContextChanged += OnDataContextChanged;
        }

        /// <summary>当前展开工作区的会话列表（模板生成后才有值）。</summary>
        public ListBox ActiveSessionsList
        {
            get { return FindActiveList("SessionsList"); }
        }

        /// <summary>当前展开工作区的文件树（模板生成后才有值）。</summary>
        public ListBox ActiveTreeList
        {
            get { return FindActiveList("FileTree"); }
        }

        public void FocusInitial()
        {
            ListBox sessions = ActiveSessionsList;
            if (sessions != null && sessions.Visibility == Visibility.Visible)
            {
                Keyboard.Focus(sessions);
                return;
            }

            ListBox tree = ActiveTreeList;
            if (tree != null && tree.Visibility == Visibility.Visible)
            {
                Keyboard.Focus(tree);
                return;
            }

            Keyboard.Focus(AddWorkspaceButton);
        }

        private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            _viewModel = DataContext as LeftPaneViewModel;
        }

        private ListBox FindActiveList(string automationId)
        {
            if (_viewModel == null || WorkspaceList == null)
            {
                return null;
            }

            WorkspaceItemViewModel active = _viewModel.ActiveWorkspace;
            if (active == null)
            {
                return null;
            }

            return FindActiveListIn(WorkspaceList, automationId, active);
        }

        private static ListBox FindActiveListIn(
            DependencyObject root,
            string automationId,
            WorkspaceItemViewModel active)
        {
            var list = root as ListBox;
            if (list != null
                && ReferenceEquals(list.DataContext, active)
                && string.Equals(
                    AutomationProperties.GetAutomationId(list),
                    automationId,
                    StringComparison.Ordinal))
            {
                return list;
            }

            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int index = 0; index < count; index++)
            {
                ListBox found = FindActiveListIn(VisualTreeHelper.GetChild(root, index), automationId, active);
                if (found != null)
                {
                    return found;
                }
            }

            return null;
        }

        /// <summary>
        /// 会话/文件列表自己带 ScrollViewer，即使关掉滚动条也会消费滚轮；左栏只允许最外层滚动，
        /// 因此在这里统一把滚轮折算成外层偏移，避免“鼠标在会话/文件上滚不动”。
        /// </summary>
        private void OnWorkspacePreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (e.Handled || WorkspaceScroll == null || WorkspaceScroll.ScrollableHeight <= 0 || e.Delta == 0)
            {
                return;
            }

            int lines = SystemParameters.WheelScrollLines;
            double step = lines == -1
                ? WorkspaceScroll.ViewportHeight
                : Math.Max(1, lines) * WheelLineHeight;

            WorkspaceScroll.ScrollToVerticalOffset(WorkspaceScroll.VerticalOffset - Math.Sign(e.Delta) * step);
            e.Handled = true;
        }

        /// <summary>单击工作区行展开/收起；点按钮或行内输入框时不触发。</summary>
        private void OnWorkspaceRowClick(object sender, MouseButtonEventArgs e)
        {
            DependencyObject source = (DependencyObject)e.OriginalSource;
            if (FindAncestor<Button>(source) != null || FindAncestor<TextBox>(source) != null)
            {
                return;
            }

            var element = sender as FrameworkElement;
            WorkspaceItemViewModel workspace = element == null
                ? null
                : element.DataContext as WorkspaceItemViewModel;
            if (workspace == null)
            {
                return;
            }

            workspace.ToggleExpandedCommand.Execute(null);
            e.Handled = true;
        }

        /// <summary>左键点击行尾更多按钮时打开同一个悬浮菜单；右键仍由 WPF 默认处理。</summary>
        private void OnMoreButtonClick(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            if (button == null || button.ContextMenu == null)
            {
                return;
            }

            button.ContextMenu.PlacementTarget = button;
            button.ContextMenu.Placement = PlacementMode.Bottom;
            button.ContextMenu.IsOpen = true;
            e.Handled = true;
        }

        /// <summary>行内重命名输入框出现时自动聚焦并全选，减少一次点击。</summary>
        private void OnInlineRenameBoxVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            var box = sender as TextBox;
            if (box == null || !box.IsVisible)
            {
                return;
            }

            Dispatcher.BeginInvoke(
                new Action(() =>
                {
                    if (!box.IsVisible)
                    {
                        return;
                    }

                    box.Focus();
                    box.SelectAll();
                }),
                DispatcherPriority.Input);
        }

        private void OnInlineRenameKeyDown(object sender, KeyEventArgs e)
        {
            var box = sender as TextBox;
            var row = box == null ? null : box.DataContext as IInlineRenameRow;
            if (row == null)
            {
                return;
            }

            if (e.Key == Key.Enter)
            {
                row.CommitRename();
                MoveFocusBackToList(box);
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                row.CancelRename();
                MoveFocusBackToList(box);
                e.Handled = true;
            }
        }

        /// <summary>点击行内其它位置即提交；提交失败时由 ViewModel 写内联错误。</summary>
        private void OnInlineRenameLostFocus(object sender, RoutedEventArgs e)
        {
            var box = sender as TextBox;
            var row = box == null ? null : box.DataContext as IInlineRenameRow;
            if (row != null && row.IsRenaming)
            {
                row.CommitRename();
            }
        }

        private static void MoveFocusBackToList(DependencyObject source)
        {
            ListBox list = FindAncestor<ListBox>(source);
            if (list != null)
            {
                list.Focus();
            }
        }

        /// <summary>单击文件夹即展开/收起；文件仍然是双击打开。</summary>
        private void OnTreeRowClick(object sender, MouseButtonEventArgs e)
        {
            FileTreeRowViewModel row = FindRow(sender, e);
            if (row == null || !row.IsDirectory)
            {
                return;
            }

            var workspace = GetWorkspaceFromSender(sender);
            if (workspace == null)
            {
                return;
            }

            // 双击会被拆成两次单击：同一行在双击时间窗内只认一次，避免“开了又关”。
            DateTime now = DateTime.UtcNow;
            if (ReferenceEquals(row, _lastToggledRow)
                && (now - _lastToggleAt).TotalMilliseconds < FolderToggleDebounceMs)
            {
                return;
            }

            _lastToggledRow = row;
            _lastToggleAt = now;
            workspace.ToggleNode(row);
        }

        private void OnTreeRowDoubleClick(object sender, MouseButtonEventArgs e)
        {
            FileTreeRowViewModel row = FindRow(sender, e);
            if (row == null || row.IsDirectory)
            {
                return;
            }

            var workspace = GetWorkspaceFromSender(sender);
            if (workspace == null)
            {
                return;
            }

            workspace.OpenNode(row);
            e.Handled = true;
        }

        private static FileTreeRowViewModel FindRow(object sender, MouseButtonEventArgs e)
        {
            DependencyObject source = (DependencyObject)e.OriginalSource;
            if (FindAncestor<Button>(source) != null || FindAncestor<TextBox>(source) != null)
            {
                return null;
            }

            var listBox = sender as ListBox;
            if (listBox == null)
            {
                return null;
            }

            var container = ItemsControl.ContainerFromElement(
                listBox,
                (DependencyObject)e.OriginalSource) as ListBoxItem;

            return container == null ? null : container.DataContext as FileTreeRowViewModel;
        }

        private void OnTreePreviewKeyDown(object sender, KeyEventArgs e)
        {
            WorkspaceItemViewModel workspace = GetWorkspaceFromSender(sender);
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

        private static WorkspaceItemViewModel GetWorkspaceFromSender(object sender)
        {
            var element = sender as FrameworkElement;
            return element == null ? null : element.DataContext as WorkspaceItemViewModel;
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
