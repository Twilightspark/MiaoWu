using System;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace MiaoWu.App.Controls
{
    /// <summary>
    /// 三栏之间的分隔条。本体 7px 命中区，实际列宽 4px；
    /// 拖动时实时更新 Value，由 ViewModel/布局策略做夹紧。
    /// </summary>
    public sealed class PaneSplitter : Thumb
    {
        public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
            nameof(Value),
            typeof(double),
            typeof(PaneSplitter),
            new FrameworkPropertyMetadata(
                0d,
                FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

        public static readonly DependencyProperty MinimumProperty = DependencyProperty.Register(
            nameof(Minimum),
            typeof(double),
            typeof(PaneSplitter),
            new PropertyMetadata(0d));

        public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
            nameof(Maximum),
            typeof(double),
            typeof(PaneSplitter),
            new PropertyMetadata(double.MaxValue));

        public static readonly DependencyProperty IsRightProperty = DependencyProperty.Register(
            nameof(IsRight),
            typeof(bool),
            typeof(PaneSplitter),
            new PropertyMetadata(false));

        public static readonly DependencyProperty IdleLineVisibleProperty = DependencyProperty.Register(
            nameof(IdleLineVisible),
            typeof(bool),
            typeof(PaneSplitter),
            new PropertyMetadata(false));

        public static readonly DependencyProperty KeyboardIncrementProperty = DependencyProperty.Register(
            nameof(KeyboardIncrement),
            typeof(double),
            typeof(PaneSplitter),
            new PropertyMetadata(8d));

        private double _dragStartValue;

        public PaneSplitter()
        {
            Focusable = true;
            IsTabStop = false;
            Cursor = Cursors.SizeWE;
            Background = System.Windows.Media.Brushes.Transparent;
            DragStarted += OnDragStarted;
            DragDelta += OnDragDelta;
        }

        public double Value
        {
            get { return (double)GetValue(ValueProperty); }
            set { SetValue(ValueProperty, value); }
        }

        public double Minimum
        {
            get { return (double)GetValue(MinimumProperty); }
            set { SetValue(MinimumProperty, value); }
        }

        public double Maximum
        {
            get { return (double)GetValue(MaximumProperty); }
            set { SetValue(MaximumProperty, value); }
        }

        /// <summary>true 表示调整右栏：向右拖动应缩小右栏。</summary>
        public bool IsRight
        {
            get { return (bool)GetValue(IsRightProperty); }
            set { SetValue(IsRightProperty, value); }
        }

        /// <summary>中/右栏常驻分割线；true 时即使未悬停也显示 1px 线。</summary>
        public bool IdleLineVisible
        {
            get { return (bool)GetValue(IdleLineVisibleProperty); }
            set { SetValue(IdleLineVisibleProperty, value); }
        }

        public double KeyboardIncrement
        {
            get { return (double)GetValue(KeyboardIncrementProperty); }
            set { SetValue(KeyboardIncrementProperty, value); }
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            double delta = 0;
            if (e.Key == Key.Left)
            {
                delta = IsRight ? KeyboardIncrement : -KeyboardIncrement;
            }
            else if (e.Key == Key.Right)
            {
                delta = IsRight ? -KeyboardIncrement : KeyboardIncrement;
            }

            if (delta == 0)
            {
                base.OnKeyDown(e);
                return;
            }

            Value = Clamp(Value + delta, Minimum, Maximum);
            e.Handled = true;
        }

        private void OnDragStarted(object sender, DragStartedEventArgs e)
        {
            _dragStartValue = Value;
        }

        private void OnDragDelta(object sender, DragDeltaEventArgs e)
        {
            double delta = IsRight ? -e.HorizontalChange : e.HorizontalChange;
            Value = Clamp(_dragStartValue + delta, Minimum, Maximum);
        }

        private static double Clamp(double value, double minimum, double maximum)
        {
            if (maximum < minimum)
            {
                maximum = minimum;
            }

            if (value < minimum)
            {
                return minimum;
            }

            if (value > maximum)
            {
                return maximum;
            }

            return value;
        }
    }
}
