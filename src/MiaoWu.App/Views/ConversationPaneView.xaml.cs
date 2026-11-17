using System;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MiaoWu.App.ViewModels;

namespace MiaoWu.App.Views
{
    /// <summary>
    /// 右栏对话视图的纯视图逻辑：Enter 发送、Shift+Enter 换行、
    /// 消息流只在用户停在底部时跟随新内容；用户上滚后不再被拉回底部。
    /// </summary>
    public partial class ConversationPaneView : UserControl
    {
        /// <summary>距底多少像素内仍算“贴底”，避免 1px 抖动导致跟随中断。</summary>
        private const double BottomTolerance = 6;

        private ConversationViewModel _viewModel;
        private ScrollViewer _scroller;
        private bool _stickToBottom = true;
        private bool _scrollQueued;

        public ConversationPaneView()
        {
            InitializeComponent();
            ModelPopup.CustomPopupPlacementCallback = PlaceModelPopup;
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
            DataContextChanged += OnDataContextChanged;
        }

        /// <summary>供离屏快照探针读取输入框实际尺寸。</summary>
        public FrameworkElement InputShell
        {
            get { return RightInputShell; }
        }

        /// <summary>供离屏快照探针统计消息容器数量。</summary>
        public ListBox MessagesList
        {
            get { return MessageList; }
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            MessageList.Loaded -= OnMessageListLoaded;
            MessageList.Loaded += OnMessageListLoaded;

            HookScroller();
            Dispatcher.BeginInvoke(
                new Action(() =>
                {
                    HookScroller();
                    if (_stickToBottom)
                    {
                        ScrollToEnd();
                    }
                }),
                DispatcherPriority.Loaded);

            HookViewModel(DataContext as ConversationViewModel);
        }

        private void OnMessageListLoaded(object sender, RoutedEventArgs e)
        {
            HookScroller();
            if (_stickToBottom)
            {
                ScrollToEnd();
            }
        }

        private void HookScroller()
        {
            if (_scroller != null)
            {
                return;
            }

            _scroller = FindChild<ScrollViewer>(MessageList);
            if (_scroller != null)
            {
                _scroller.ScrollChanged += OnScrollChanged;
            }
        }

        private void OnItemsChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.Action == NotifyCollectionChangedAction.Reset)
            {
                // 会话切换 / 重放会把列表清空重建，新会话从底部开始看。
                _stickToBottom = true;
                QueueScrollToEnd();
                return;
            }

            // 自己刚发出的提问一定可见；其余新增内容只在原本贴底时跟随。
            if (ContainsUserMessage(e.NewItems))
            {
                _stickToBottom = true;
            }

            if (_stickToBottom)
            {
                QueueScrollToEnd();
            }
        }

        private static bool ContainsUserMessage(System.Collections.IList items)
        {
            if (items == null)
            {
                return false;
            }

            foreach (object item in items)
            {
                if (item is UserChatItemViewModel)
                {
                    return true;
                }
            }

            return false;
        }

        private void ScrollToEnd()
        {
            HookScroller();
            if (_scroller != null)
            {
                _scroller.ScrollToEnd();
            }
        }

        /// <summary>合并同一轮布局里的多次跟随请求，避免流式输出时反复排队。</summary>
        private void QueueScrollToEnd()
        {
            if (_scrollQueued)
            {
                return;
            }

            _scrollQueued = true;
            Dispatcher.BeginInvoke(
                new Action(() =>
                {
                    _scrollQueued = false;
                    ScrollToEnd();
                }),
                DispatcherPriority.Background);
        }

        private bool IsAtBottom()
        {
            if (_scroller == null)
            {
                return true;
            }

            double scrollable = _scroller.ScrollableHeight;
            return scrollable <= 0 || _scroller.VerticalOffset >= scrollable - BottomTolerance;
        }

        /// <summary>滚轮上滚时立即脱离贴底，后续流式内容不再把视口拽回底部。</summary>
        private void OnMessageListPreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (e.Delta > 0)
            {
                _stickToBottom = false;
            }
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            if (_scroller != null)
            {
                _scroller.ScrollChanged -= OnScrollChanged;
                _scroller = null;
            }

            _scrollQueued = false;
            HookViewModel(null);
        }

        private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            HookViewModel(e.NewValue as ConversationViewModel);
            _stickToBottom = true;
            Dispatcher.BeginInvoke(
                new Action(() =>
                {
                    HookScroller();
                    QueueScrollToEnd();
                }),
                DispatcherPriority.Loaded);
        }

        private void HookViewModel(ConversationViewModel viewModel)
        {
            if (_viewModel != null)
            {
                _viewModel.ComposerFocusRequested -= OnComposerFocusRequested;
            }

            if (_viewModel != null)
            {
                ((INotifyCollectionChanged)_viewModel.Items).CollectionChanged -= OnItemsChanged;
            }

            _viewModel = viewModel;

            if (_viewModel != null)
            {
                _viewModel.ComposerFocusRequested += OnComposerFocusRequested;
                ((INotifyCollectionChanged)_viewModel.Items).CollectionChanged += OnItemsChanged;
            }
        }

        private void OnComposerFocusRequested(object sender, EventArgs e)
        {
            if (ComposerBox != null)
            {
                ComposerBox.Focus();
                Keyboard.Focus(ComposerBox);
            }
        }

        private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (_scroller == null)
            {
                return;
            }

            bool atBottom = IsAtBottom();

            if (e.VerticalChange < 0)
            {
                // 用户往上滚：立即脱离跟随，直到自己滚回底部。
                _stickToBottom = false;
            }
            else if (e.VerticalChange > 0)
            {
                _stickToBottom = atBottom;
            }
            else if (e.ExtentHeightChange == 0 && e.ViewportHeightChange == 0)
            {
                _stickToBottom = atBottom;
            }

            // 内容长高（流式输出、工具输出展开）时，贴底就继续跟随；
            // 不贴底则保持用户当前的位置，不抢滚动条。
            if (_stickToBottom && !atBottom)
            {
                QueueScrollToEnd();
            }
        }

        private void OnComposerKeyDown(object sender, KeyEventArgs e)
        {
            // 输入法组词期间 WPF 报 ImeProcessed；不要把候选回车当成发送。
            if (e.Key == Key.ImeProcessed || e.Key == Key.System)
            {
                return;
            }

            if (e.Key != Key.Enter)
            {
                return;
            }

            bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
            if (shift)
            {
                // 让 TextBox 插入换行。
                return;
            }

            e.Handled = true;
            ConversationViewModel viewModel = DataContext as ConversationViewModel;
            if (viewModel == null)
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(viewModel.Draft))
            {
                viewModel.SendCommand.Execute(null);
            }

            _stickToBottom = true;
            if (_scroller != null)
            {
                _scroller.ScrollToEnd();
            }
        }

        /// <summary>会话头「更多」左键打开与左栏会话行共用的菜单。</summary>
        private void OnMoreClick(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            if (button == null || button.ContextMenu == null)
            {
                return;
            }

            button.ContextMenu.PlacementTarget = button;
            button.ContextMenu.Placement = PlacementMode.Bottom;
            button.ContextMenu.IsOpen = true;
        }

        private void OnModelPickerClick(object sender, RoutedEventArgs e)
        {
            if (!(DataContext is ConversationViewModel))
            {
                return;
            }

            if (ModelPopup.IsOpen)
            {
                ModelPopup.IsOpen = false;
                return;
            }

            // 点击本身会完成一次鼠标捕获；立即打开会被 StaysOpen=False 当成外部点击关掉。
            Dispatcher.BeginInvoke(
                new Action(() => ModelPopup.IsOpen = true),
                System.Windows.Threading.DispatcherPriority.Input);
        }

        private void OnModelRowClick(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            var option = button == null ? null : button.Tag as ModelOption;
            var viewModel = DataContext as ConversationViewModel;
            if (option == null || viewModel == null)
            {
                return;
            }

            viewModel.SelectModel(option);
            ModelPopup.IsOpen = false;
        }

        private void OnOpenModelSettingsClick(object sender, RoutedEventArgs e)
        {
            var viewModel = DataContext as ConversationViewModel;
            ModelPopup.IsOpen = false;
            if (viewModel != null)
            {
                viewModel.OpenSettingsCommand.Execute(null);
            }
        }

        /// <summary>
        /// 模型菜单向上展开，右缘与模型按钮对齐，避免在右栏贴边处被裁掉；
        /// 上方空间不足时由 WPF 顺次回退到按钮下方，不会顶出屏幕。
        /// </summary>
        private static CustomPopupPlacement[] PlaceModelPopup(
            Size popupSize,
            Size targetSize,
            Point offset)
        {
            const double gap = 6;
            double x = targetSize.Width - popupSize.Width;
            return new[]
            {
                new CustomPopupPlacement(
                    new Point(x, -popupSize.Height - gap),
                    PopupPrimaryAxis.Horizontal),
                new CustomPopupPlacement(
                    new Point(x, targetSize.Height + gap),
                    PopupPrimaryAxis.Horizontal)
            };
        }

        private void OnRenameKeyDown(object sender, KeyEventArgs e)
        {
            ConversationViewModel viewModel = DataContext as ConversationViewModel;
            if (viewModel == null)
            {
                return;
            }

            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                viewModel.CommitRenameCommand.Execute(null);
            }
            else if (e.Key == Key.Escape)
            {
                e.Handled = true;
                viewModel.CancelRenameCommand.Execute(null);
            }
        }

        private static T FindChild<T>(DependencyObject parent) where T : DependencyObject
        {
            if (parent == null)
            {
                return null;
            }

            int count = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(parent, i);
                T match = child as T;
                if (match != null)
                {
                    return match;
                }

                T nested = FindChild<T>(child);
                if (nested != null)
                {
                    return nested;
                }
            }

            return null;
        }
    }
}
