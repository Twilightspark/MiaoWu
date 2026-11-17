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
    /// 流式输出时若用户停在底部则自动跟随滚动。
    /// </summary>
    public partial class ConversationPaneView : UserControl
    {
        private ConversationViewModel _viewModel;
        private ScrollViewer _scroller;
        private bool _stickToBottom = true;

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
            if (!_stickToBottom)
            {
                return;
            }

            Dispatcher.BeginInvoke(new Action(ScrollToEnd), DispatcherPriority.Background);
        }

        private void ScrollToEnd()
        {
            HookScroller();
            if (_scroller != null)
            {
                _scroller.ScrollToEnd();
            }
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            if (_scroller != null)
            {
                _scroller.ScrollChanged -= OnScrollChanged;
                _scroller = null;
            }

            HookViewModel(null);
        }
        private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            HookViewModel(e.NewValue as ConversationViewModel);
            Dispatcher.BeginInvoke(new Action(HookScroller), DispatcherPriority.Loaded);
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
            if (e.ExtentHeightChange == 0)
            {
                _stickToBottom = e.VerticalOffset >= e.ExtentHeight - e.ViewportHeight - 24;
                return;
            }

            if (_stickToBottom && _scroller != null)
            {
                _scroller.ScrollToEnd();
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
