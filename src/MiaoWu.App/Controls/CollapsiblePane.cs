using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;

namespace MiaoWu.App.Controls
{
    /// <summary>
    /// 右栏宿主：只对宽度做 180ms ease-out 动画，内容保持固定宽度并裁切，
    /// 避免折叠过程中文字重排。右对齐使面板左缘移动、右缘保持贴住窗口。
    /// </summary>
    public sealed class CollapsiblePane : ContentControl
    {
        public static readonly DependencyProperty IsCollapsedProperty = DependencyProperty.Register(
            nameof(IsCollapsed),
            typeof(bool),
            typeof(CollapsiblePane),
            new PropertyMetadata(false, OnIsCollapsedChanged));

        public static readonly DependencyProperty ExpandedWidthProperty = DependencyProperty.Register(
            nameof(ExpandedWidth),
            typeof(double),
            typeof(CollapsiblePane),
            new PropertyMetadata(580d, OnExpandedWidthChanged));

        public static readonly DependencyProperty AnimationsEnabledProperty = DependencyProperty.Register(
            nameof(AnimationsEnabled),
            typeof(bool),
            typeof(CollapsiblePane),
            new PropertyMetadata(true));

        public static readonly DependencyProperty DurationMillisecondsProperty = DependencyProperty.Register(
            nameof(DurationMilliseconds),
            typeof(double),
            typeof(CollapsiblePane),
            new PropertyMetadata(180d));

        private bool _initialized;

        public CollapsiblePane()
        {
            ClipToBounds = true;
            HorizontalAlignment = HorizontalAlignment.Right;
            Focusable = false;
            IsTabStop = false;
            Loaded += OnLoaded;
        }

        public bool IsCollapsed
        {
            get { return (bool)GetValue(IsCollapsedProperty); }
            set { SetValue(IsCollapsedProperty, value); }
        }

        public double ExpandedWidth
        {
            get { return (double)GetValue(ExpandedWidthProperty); }
            set { SetValue(ExpandedWidthProperty, value); }
        }

        public bool AnimationsEnabled
        {
            get { return (bool)GetValue(AnimationsEnabledProperty); }
            set { SetValue(AnimationsEnabledProperty, value); }
        }

        public double DurationMilliseconds
        {
            get { return (double)GetValue(DurationMillisecondsProperty); }
            set { SetValue(DurationMillisecondsProperty, value); }
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            _initialized = true;
            SnapToState();
        }

        private static void OnIsCollapsedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            ((CollapsiblePane)d).AnimateToState();
        }

        private static void OnExpandedWidthChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var pane = (CollapsiblePane)d;
            if (!pane.IsCollapsed)
            {
                pane.SnapToExpandedWidth();
            }
        }

        private void SnapToState()
        {
            if (IsCollapsed)
            {
                BeginAnimation(WidthProperty, null);
                Width = 0;
                Visibility = Visibility.Collapsed;
            }
            else
            {
                Visibility = Visibility.Visible;
                SnapToExpandedWidth();
            }
        }

        private void SnapToExpandedWidth()
        {
            BeginAnimation(WidthProperty, null);
            Width = ExpandedWidth;
        }

        private void AnimateToState()
        {
            bool animate = AnimationsEnabled && SystemParameters.ClientAreaAnimation;

            if (!animate)
            {
                SnapToState();
                return;
            }

            // 首次绑定发生在 Loaded 之前：此时没有可动画的 ActualWidth，
            // 直接把初始状态（尤其是“启动默认折叠”）落到属性上。
            if (!_initialized)
            {
                SnapToState();
                return;
            }

            double from = ActualWidth;
            if (double.IsNaN(from) || from <= 0)
            {
                from = IsCollapsed ? ExpandedWidth : 0;
            }

            double to = IsCollapsed ? 0 : ExpandedWidth;

            BeginAnimation(WidthProperty, null);

            if (IsCollapsed)
            {
                Visibility = Visibility.Visible;
            }
            else
            {
                Visibility = Visibility.Visible;
                Width = 0;
            }

            var animation = new DoubleAnimation
            {
                From = from,
                To = to,
                Duration = TimeSpan.FromMilliseconds(DurationMilliseconds),
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.HoldEnd
            };

            animation.Completed += (sender, args) =>
            {
                BeginAnimation(WidthProperty, null);

                if (IsCollapsed)
                {
                    Width = 0;
                    Visibility = Visibility.Collapsed;
                }
                else
                {
                    Width = ExpandedWidth;
                }
            };

            BeginAnimation(WidthProperty, animation, HandoffBehavior.SnapshotAndReplace);
        }
    }
}
