using System;
using System.Collections.Specialized;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using MiaoWu.App.ViewModels;
using MiaoWu.Core.Input;

namespace MiaoWu.App.Views
{
    /// <summary>
    /// 右栏对话视图的纯视图逻辑：Enter 发送、Shift+Enter 换行、
    /// 消息流只在用户停在底部时跟随新内容；`/` 与 `@` 菜单的键盘路由也在这里，
    /// 焦点始终留在编辑器里（菜单 Popup 不抢焦点）。
    /// </summary>
    public partial class ConversationPaneView : UserControl
    {
        /// <summary>距底多少像素内仍算“贴底”，避免 1px 抖动导致跟随中断。</summary>
        private const double BottomTolerance = 6;

        private const int ComposerSettleMilliseconds = 180;

        private ConversationViewModel _viewModel;
        private ScrollViewer _scroller;
        private bool _stickToBottom = true;
        private bool _scrollQueued;
        private bool _welcomeLayout;
        private bool _composerSettleQueued;

        /// <summary>读数明细的锚点：最近一次点击的 pill，面板左缘与它对齐。</summary>
        private Button _dockAnchorPill;

        public ConversationPaneView()
        {
            InitializeComponent();
            ModelPopup.CustomPopupPlacementCallback = PlaceModelPopup;
            TriggerPopup.CustomPopupPlacementCallback = PlaceTriggerPopup;
            DockPopup.CustomPopupPlacementCallback = PlaceDockPopup;
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

        /// <summary>供离屏快照探针检查 `/` 与 `@` 补全菜单的位置与开关状态。</summary>
        public Popup ComposerMenuPopup
        {
            get { return TriggerPopup; }
        }

        /// <summary>供离屏快照探针检查输入框下方读数明细面板。</summary>
        public Popup ComposerDockPopup
        {
            get { return DockPopup; }
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
            // 欢迎态（0 条消息）→ 正常态：底部星号行会从 S 收成 0，输入卡正好下移 S。
            // 先记住这个像素高度，布局切换后再用等量 Y 偏移把整组送回旧位置，然后滑回 0。
            bool wasWelcome = _welcomeLayout;
            _welcomeLayout = _viewModel == null || _viewModel.IsEmpty;
            if (wasWelcome && !_welcomeLayout && e.Action != NotifyCollectionChangedAction.Reset)
            {
                double spacerHeight = WelcomeSpacerRow == null ? 0 : WelcomeSpacerRow.ActualHeight;
                if (spacerHeight > 1)
                {
                    QueueComposerSettle(-spacerHeight);
                }
            }

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

        /// <summary>
        /// 等布局落定后，把整组从旧位置（负偏移）平移回 0，形成“发送后从中间落回底部”的过渡。
        /// 只动画一个 Y 偏移，不改宽度或行高，避免文字在动画中反复重排。
        /// </summary>
        private void QueueComposerSettle(double offset)
        {
            if (_composerSettleQueued || Math.Abs(offset) < 1)
            {
                return;
            }

            _composerSettleQueued = true;
            Dispatcher.BeginInvoke(
                new Action(() =>
                {
                    _composerSettleQueued = false;
                    if (!SystemParameters.ClientAreaAnimation || ComposerGroup == null)
                    {
                        return;
                    }

                    UpdateLayout();

                    var transform = ComposerGroup.RenderTransform as TranslateTransform;
                    if (transform == null)
                    {
                        transform = new TranslateTransform();
                        ComposerGroup.RenderTransform = transform;
                    }

                    transform.BeginAnimation(TranslateTransform.YProperty, null);
                    transform.Y = offset;

                    var animation = new DoubleAnimation
                    {
                        From = offset,
                        To = 0,
                        Duration = TimeSpan.FromMilliseconds(ComposerSettleMilliseconds),
                        EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
                        FillBehavior = FillBehavior.Stop
                    };
                    animation.Completed += (sender, args) =>
                    {
                        transform.BeginAnimation(TranslateTransform.YProperty, null);
                        transform.Y = 0;
                    };
                    transform.BeginAnimation(TranslateTransform.YProperty, animation);
                }),
                DispatcherPriority.Render);
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
                _viewModel.MentionInsertRequested -= OnMentionInsertRequested;
            }

            if (_viewModel != null)
            {
                ((INotifyCollectionChanged)_viewModel.Items).CollectionChanged -= OnItemsChanged;
            }

            _viewModel = viewModel;
            _welcomeLayout = _viewModel == null || _viewModel.IsEmpty;

            if (_viewModel != null)
            {
                _viewModel.ComposerFocusRequested += OnComposerFocusRequested;
                _viewModel.MentionInsertRequested += OnMentionInsertRequested;
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
            // 输入法组词期间 WPF 报 ImeProcessed；不要把候选回车当成发送或菜单选中。
            if (e.Key == Key.ImeProcessed || e.Key == Key.System)
            {
                return;
            }

            ConversationViewModel viewModel = DataContext as ConversationViewModel;
            ComposerTriggerViewModel menu = viewModel == null ? null : viewModel.ComposerTriggers;
            bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;

            if (menu != null && menu.IsOpen && !shift)
            {
                switch (e.Key)
                {
                    case Key.Down:
                        menu.Move(1);
                        e.Handled = true;
                        EnsureHighlightVisible();
                        return;

                    case Key.Up:
                        menu.Move(-1);
                        e.Handled = true;
                        EnsureHighlightVisible();
                        return;

                    case Key.Escape:
                        menu.Dismiss();
                        e.Handled = true;
                        return;

                    case Key.Tab:
                        if (ApplyComposerPick(menu.PickHighlighted(true)))
                        {
                            e.Handled = true;
                        }

                        return;

                    case Key.Enter:
                        if (ApplyComposerPick(menu.PickHighlighted(false)))
                        {
                            e.Handled = true;
                            return;
                        }

                        // 候选还没回来 / 没有高亮：吞掉回车，既不选中旧行也不发送。
                        e.Handled = true;
                        return;
                }
            }

            if (e.Key == Key.Escape)
            {
                if (menu != null)
                {
                    menu.Close(false);
                }

                return;
            }

            if (e.Key != Key.Enter)
            {
                return;
            }

            if (shift)
            {
                // 让 RichTextBox 插入新段落。
                return;
            }

            e.Handled = true;
            if (viewModel == null)
            {
                return;
            }

            // 裸命令（`/compact`、`/plan`、`/work`）在回车时消费 token 并执行，不进会话日志。
            ComposerCommand command;
            if (ComposerCommandCatalog.TryParseBareCommand(ComposerBox.PlainText, out command))
            {
                ComposerBox.ClearAll();
                viewModel.Draft = string.Empty;
                viewModel.ExecuteComposerCommand(command);
                return;
            }

            if (!string.IsNullOrWhiteSpace(viewModel.Draft) || viewModel.ComposerImages.Count > 0)
            {
                viewModel.SendCommand.Execute(null);
            }

            _stickToBottom = true;
            if (_scroller != null)
            {
                _scroller.ScrollToEnd();
            }
        }

        /// <summary>草稿变化：同步 Draft / 引用集合，并重新检测触发 token。</summary>
        private void OnComposerDraftChanged(object sender, EventArgs e)
        {
            ConversationViewModel viewModel = DataContext as ConversationViewModel;
            if (viewModel == null || ComposerBox == null)
            {
                return;
            }

            viewModel.SyncComposerMentions(ComposerBox.Mentions);
            viewModel.TrackComposer(ComposerBox.PlainText, ComposerBox.CaretOffset);
        }

        /// <summary>光标移动：同一 token 上移回来时按 dismissed 记忆决定是否重开菜单。</summary>
        private void OnComposerCaretMoved(object sender, EventArgs e)
        {
            ConversationViewModel viewModel = DataContext as ConversationViewModel;
            if (viewModel == null || ComposerBox == null)
            {
                return;
            }

            viewModel.TrackComposer(ComposerBox.PlainText, ComposerBox.CaretOffset);
        }

        /// <summary>拖进输入框的文件：图片读成附件，其余插入引用 chip。</summary>
        private void OnComposerFilesDropped(object sender, MiaoWu.App.Controls.ComposerPathsEventArgs e)
        {
            ConversationViewModel viewModel = DataContext as ConversationViewModel;
            if (viewModel == null || e == null)
            {
                return;
            }

            foreach (string path in e.Paths)
            {
                ComposerMention mention;
                string error;
                if (!viewModel.TryCreateComposerMention(path, out mention, out error))
                {
                    continue;
                }

                ComposerBox.InsertMentionAtCaret(mention);
            }

            viewModel.SyncComposerMentions(ComposerBox.Mentions);
            ComposerBox.Focus();
        }

        /// <summary>`+` 选完文件后，把引用逐个插到光标处。</summary>
        private void OnMentionInsertRequested(object sender, ConversationViewModel.ComposerMentionsEventArgs e)
        {
            if (e == null || ComposerBox == null)
            {
                return;
            }

            foreach (ComposerMention mention in e.Mentions)
            {
                ComposerBox.InsertMentionAtCaret(mention);
            }

            ComposerBox.Focus();
        }

        private void OnTriggerRowMouseMove(object sender, MouseEventArgs e)
        {
            var button = sender as Button;
            var row = button == null ? null : button.Tag as ComposerMenuRowViewModel;
            ConversationViewModel viewModel = DataContext as ConversationViewModel;
            if (row == null || viewModel == null)
            {
                return;
            }

            viewModel.ComposerTriggers.Hover(row);
        }

        private void OnTriggerRowClick(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            var row = button == null ? null : button.Tag as ComposerMenuRowViewModel;
            ConversationViewModel viewModel = DataContext as ConversationViewModel;
            if (row == null || viewModel == null)
            {
                return;
            }

            ApplyComposerPick(viewModel.ComposerTriggers.PickRow(row));
            ComposerBox.Focus();
        }

        /// <summary>外部点击关闭菜单时，触发控制器也要一起收起来（并记住拒绝）。</summary>
        private void OnTriggerPopupClosed(object sender, EventArgs e)
        {
            ConversationViewModel viewModel = DataContext as ConversationViewModel;
            if (viewModel != null && viewModel.ComposerTriggers.IsOpen)
            {
                viewModel.ComposerTriggers.Dismiss();
            }
        }

        /// <summary>窄宽度时收起速度 / 缓存命中，只留轮步与总 token。</summary>
        private void OnComposerDockSizeChanged(object sender, SizeChangedEventArgs e)
        {
            ConversationViewModel viewModel = DataContext as ConversationViewModel;
            if (viewModel != null)
            {
                viewModel.ComposerDock.SetCompact(e.NewSize.Width < 396);
            }
        }

        /// <summary>读数 pill：点开对应明细；再点同一个收起，同一时刻只开一个面板。</summary>
        private void OnDockPillClick(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            ConversationViewModel viewModel = DataContext as ConversationViewModel;
            if (button == null || viewModel == null)
            {
                return;
            }

            ComposerDockPanel panel;
            string tag = button.Tag as string;
            if (string.Equals(tag, "Usage", StringComparison.Ordinal))
            {
                panel = ComposerDockPanel.Usage;
            }
            else if (string.Equals(tag, "Context", StringComparison.Ordinal))
            {
                panel = ComposerDockPanel.Context;
            }
            else
            {
                panel = ComposerDockPanel.Stats;
            }

            // 明细跟着被点的 pill 走：换 panel 时重新落位，而不是停在第一颗 pill 上方。
            _dockAnchorPill = button;
            viewModel.ComposerDock.Toggle(panel);
            RepositionDockPopup();
        }

        /// <summary>
        /// 自定义落位只在目标重新布局时才重算；换 pill 时读数行尺寸没变，
        /// 用一次 0.5 → 0 的偏移抖动把同一帧里的落位再算一遍（不产生可见位移）。
        /// </summary>
        private void RepositionDockPopup()
        {
            if (!DockPopup.IsOpen)
            {
                return;
            }

            DockPopup.HorizontalOffset = 0.5;
            DockPopup.HorizontalOffset = 0;
        }

        /// <summary>点外部或再次点击 pill 关闭面板时，视图模型也要归位。</summary>
        private void OnDockPopupClosed(object sender, EventArgs e)
        {
            ConversationViewModel viewModel = DataContext as ConversationViewModel;
            if (viewModel != null)
            {
                viewModel.ComposerDock.Close();
            }
        }

        /// <summary>Esc 关闭读数面板并把焦点还给输入框。</summary>
        private void OnDockKeyDown(object sender, KeyEventArgs e)
        {
            ConversationViewModel viewModel = DataContext as ConversationViewModel;
            if (viewModel == null || !viewModel.ComposerDock.IsOpen)
            {
                return;
            }

            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                viewModel.ComposerDock.Close();
                if (ComposerBox != null)
                {
                    ComposerBox.Focus();
                }
            }
        }

        /// <summary>
        /// 焦点离开编辑器（点到消息流、模型按钮等）时收起菜单；点输入卡自身不触发，
        /// 所以 StaysOpen=True，不靠 Popup 自己判断“外面”。
        /// </summary>
        private void OnComposerLostFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            ConversationViewModel viewModel = DataContext as ConversationViewModel;
            if (viewModel == null || !viewModel.ComposerTriggers.IsOpen)
            {
                return;
            }

            var target = e.NewFocus as DependencyObject;
            if (target != null && (ComposerBox.IsAncestorOf(target) || target == ComposerBox))
            {
                return;
            }

            viewModel.ComposerTriggers.Dismiss();
        }

        /// <summary>把一个菜单动作落到编辑器 / 会话上；返回是否真的做了事。</summary>
        private bool ApplyComposerPick(ComposerPickResult pick)
        {
            ConversationViewModel viewModel = DataContext as ConversationViewModel;
            if (pick == null || pick.Kind == ComposerPickKind.None || viewModel == null)
            {
                return false;
            }

            switch (pick.Kind)
            {
                case ComposerPickKind.Command:
                    ComposerBox.ReplaceHit(pick.Hit, string.Empty);
                    viewModel.Draft = ComposerBox.PlainText;
                    viewModel.ExecuteComposerCommand(pick.Command);
                    return true;

                case ComposerPickKind.Text:
                    ComposerBox.ReplaceHit(pick.Hit, pick.Text);
                    viewModel.Draft = ComposerBox.PlainText;
                    if (pick.ContinueMenu)
                    {
                        // 目录下钻：菜单不关，下一次击键继续命中同一个引号 token。
                        viewModel.TrackComposer(ComposerBox.PlainText, ComposerBox.CaretOffset);
                    }

                    return true;

                case ComposerPickKind.Mention:
                    ComposerBox.InsertMention(pick.Hit, pick.Mention);
                    viewModel.SyncComposerMentions(ComposerBox.Mentions);
                    if (pick.Mention != null && pick.Mention.Kind == ComposerMentionKind.Image)
                    {
                        viewModel.AttachImageForMention(pick.Mention);
                    }

                    return true;
            }

            return false;
        }

        /// <summary>↑ / ↓ 之后把高亮行滚进菜单视口（菜单本身不抢焦点）。</summary>
        private void EnsureHighlightVisible()
        {
            ConversationViewModel viewModel = DataContext as ConversationViewModel;
            if (viewModel == null)
            {
                return;
            }

            Dispatcher.BeginInvoke(
                new Action(() =>
                {
                    foreach (ComposerMenuRowViewModel row in viewModel.ComposerTriggers.Rows)
                    {
                        if (row.IsHeader || !row.IsHighlighted)
                        {
                            continue;
                        }

                        var container = TriggerRows.ItemContainerGenerator.ContainerFromItem(row) as FrameworkElement;
                        if (container != null)
                        {
                            container.BringIntoView();
                        }

                        break;
                    }
                }),
                DispatcherPriority.Background);
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

        /// <summary>
        /// 读数明细像模型菜单一样向上弹出：底边在读数行上方 6px、水平居中对着被点的 pill，
        /// 越界时贴住读数行两侧；上方放不下时回退到读数行下方。
        /// </summary>
        private CustomPopupPlacement[] PlaceDockPopup(
            Size popupSize,
            Size targetSize,
            Point offset)
        {
            const double gap = 6;

            // 自定义落位的回调坐标是 Popup 的屏幕坐标系，高 DPI 下是设备像素；
            // 元素 ActualWidth/Height 却是 DIP。用读数行自身的尺寸反推缩放，避免混用。
            double scale = ComposerDockRowHost != null && ComposerDockRowHost.ActualHeight > 0
                ? targetSize.Height / ComposerDockRowHost.ActualHeight
                : 1;
            if (scale <= 0)
            {
                scale = 1;
            }

            double x = DockAnchorLeft(popupSize.Width / scale) * scale;
            double gapPixels = gap * scale;
            return new[]
            {
                new CustomPopupPlacement(
                    new Point(x, -popupSize.Height - gapPixels),
                    PopupPrimaryAxis.Horizontal),
                new CustomPopupPlacement(
                    new Point(x, targetSize.Height + gapPixels),
                    PopupPrimaryAxis.Horizontal)
            };
        }

        /// <summary>
        /// 面板左缘在读数行里的位置：右缘对齐被点 pill 的右缘（与模型菜单同一套对齐），
        /// 再夹在读数行内；面板比读数行还宽时贴左。
        /// </summary>
        private double DockAnchorLeft(double popupWidth)
        {
            if (_dockAnchorPill == null || ComposerDockRowHost == null)
            {
                return 0;
            }

            Point pill = _dockAnchorPill.TranslatePoint(new Point(0, 0), ComposerDockRowHost);
            double right = pill.X + _dockAnchorPill.ActualWidth;
            double hostWidth = ComposerDockRowHost.ActualWidth;
            double maxLeft = Math.Max(0, hostWidth - popupWidth);
            return Math.Min(Math.Max(0, right - popupWidth), maxLeft);
        }

        /// <summary>触发菜单向上展开、左缘与输入卡对齐；上方放不下时回退到输入卡下方。</summary>
        private static CustomPopupPlacement[] PlaceTriggerPopup(
            Size popupSize,
            Size targetSize,
            Point offset)
        {
            const double gap = 4;
            return new[]
            {
                new CustomPopupPlacement(
                    new Point(0, -popupSize.Height - gap),
                    PopupPrimaryAxis.Vertical),
                new CustomPopupPlacement(
                    new Point(0, targetSize.Height + gap),
                    PopupPrimaryAxis.Vertical)
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
