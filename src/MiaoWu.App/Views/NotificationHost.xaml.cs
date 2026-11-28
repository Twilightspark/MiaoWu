using System;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using MiaoWu.App.ViewModels;

namespace MiaoWu.App.Views
{
    /// <summary>
    /// 待处理弹窗栈（覆盖输入卡）：审批与提问都必须显式处理，不会自动消失。
    /// 新弹窗从下往上堆，底边贴输入卡底边；高度超过上限时整栈滚动，并把最新一条滚进视野。
    /// </summary>
    public partial class NotificationHost : UserControl
    {
        private INotifyCollectionChanged _source;
        private bool _scrollQueued;

        public NotificationHost()
        {
            InitializeComponent();
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
            DataContextChanged += OnDataContextChanged;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            HookSource();
            QueueScrollToEnd();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            HookSource(null);
        }

        private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            HookSource();
        }

        private void HookSource()
        {
            HookSource(DataContext as MainWindowViewModel);
        }

        private void HookSource(MainWindowViewModel viewModel)
        {
            if (_source != null)
            {
                _source.CollectionChanged -= OnNotificationsChanged;
                _source = null;
            }

            if (viewModel == null)
            {
                return;
            }

            _source = viewModel.Notifications;
            _source.CollectionChanged += OnNotificationsChanged;
        }

        private void OnNotificationsChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            QueueScrollToEnd();
        }

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
                    if (NoticeScroll != null)
                    {
                        NoticeScroll.ScrollToEnd();
                    }
                }),
                DispatcherPriority.Background);
        }
    }
}
