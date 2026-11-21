using System;
using System.Collections.Generic;
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
    /// 通用二次确认浮层：遮罩点击 / Esc / 右上角图标按钮取消，Tab 在浮层内循环，
    /// 打开时焦点落在安全出口“取消”上。关闭后把焦点还给打开前的控件；不新开 Window。
    /// </summary>
    public partial class ConfirmPromptView : UserControl
    {
        private ConfirmPromptViewModel _viewModel;
        private IInputElement _previousFocus;
        private Window _hostWindow;

        public ConfirmPromptView()
        {
            InitializeComponent();
            DataContextChanged += OnDataContextChanged;
            PreviewKeyDown += OnPreviewKeyDown;
            Unloaded += (sender, args) => DetachHostEscape();
        }

        private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (_viewModel != null)
            {
                _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            }

            _viewModel = DataContext as ConfirmPromptViewModel;

            if (_viewModel != null)
            {
                _viewModel.PropertyChanged += OnViewModelPropertyChanged;
            }
        }

        private void OnViewModelPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != "IsOpen" || _viewModel == null)
            {
                return;
            }

            if (_viewModel.IsOpen)
            {
                AttachHostEscape();
                _previousFocus = Keyboard.FocusedElement;
                Dispatcher.BeginInvoke(
                    new Action(() => CancelButton.Focus()),
                    DispatcherPriority.Input);
            }
            else
            {
                DetachHostEscape();
                if (_previousFocus != null)
                {
                    IInputElement previous = _previousFocus;
                    _previousFocus = null;
                    Dispatcher.BeginInvoke(
                        new Action(() => Keyboard.Focus(previous)),
                        DispatcherPriority.Input);
                }
            }
        }

        /// <summary>
        /// 从 ContextMenu / 行内菜单打开时，菜单关闭后的焦点恢复可能把焦点抢走；
        /// 在宿主窗口上再兜一层 Esc，保证浮层始终能用键盘取消（与 MessageBox 一致）。
        /// </summary>
        private void AttachHostEscape()
        {
            if (_hostWindow != null)
            {
                return;
            }

            _hostWindow = Window.GetWindow(this);
            if (_hostWindow != null)
            {
                _hostWindow.PreviewKeyDown += OnHostPreviewKeyDown;
            }
        }

        private void DetachHostEscape()
        {
            if (_hostWindow == null)
            {
                return;
            }

            _hostWindow.PreviewKeyDown -= OnHostPreviewKeyDown;
            _hostWindow = null;
        }

        private void OnHostPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (_viewModel == null || !_viewModel.IsOpen || e.Key != Key.Escape)
            {
                return;
            }

            e.Handled = true;
            Cancel();
        }

        private void OnScrimClick(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            Cancel();
        }

        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (_viewModel == null || !_viewModel.IsOpen)
            {
                return;
            }

            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                Cancel();
                return;
            }

            if (e.Key != Key.Tab)
            {
                return;
            }

            List<UIElement> focusable = CollectFocusable(this);
            if (focusable.Count == 0)
            {
                return;
            }

            UIElement focused = Keyboard.FocusedElement as UIElement;
            int index = focused == null ? -1 : focusable.IndexOf(focused);
            bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;

            if (index < 0)
            {
                e.Handled = true;
                focusable[shift ? focusable.Count - 1 : 0].Focus();
                return;
            }

            if (!shift && index == focusable.Count - 1)
            {
                e.Handled = true;
                focusable[0].Focus();
            }
            else if (shift && index == 0)
            {
                e.Handled = true;
                focusable[focusable.Count - 1].Focus();
            }
        }

        private void Cancel()
        {
            if (_viewModel != null)
            {
                _viewModel.CancelCommand.Execute(null);
            }
        }

        private static List<UIElement> CollectFocusable(DependencyObject root)
        {
            var result = new List<UIElement>();
            Collect(root, result);
            return result;
        }

        private static void Collect(DependencyObject parent, List<UIElement> result)
        {
            int count = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(parent, i);
                var element = child as UIElement;
                if (element != null)
                {
                    var control = element as Control;
                    bool focusable = element.Focusable
                        && element.IsVisible
                        && element.IsEnabled
                        && (control == null || control.IsTabStop);
                    if (focusable)
                    {
                        result.Add(element);
                    }
                }

                Collect(child, result);
            }
        }
    }
}
