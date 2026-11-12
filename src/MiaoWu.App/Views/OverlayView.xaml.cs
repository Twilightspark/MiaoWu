using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using MiaoWu.App.ViewModels;

namespace MiaoWu.App.Views
{
    /// <summary>
    /// 主窗口内的模态浮层：打开时焦点落到关闭按钮，关闭后还给原控件。
    /// 不新开 Window；Esc 由 ShellView 的键绑定关闭。
    /// </summary>
    public partial class OverlayView : UserControl
    {
        private OverlayViewModel _viewModel;
        private IInputElement _previousFocus;

        public OverlayView()
        {
            InitializeComponent();
            DataContextChanged += OnDataContextChanged;
        }

        private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (_viewModel != null)
            {
                _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            }

            _viewModel = DataContext as OverlayViewModel;

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
                _previousFocus = Keyboard.FocusedElement;
                Dispatcher.BeginInvoke(
                    new Action(() => CloseButton.Focus()),
                    DispatcherPriority.Input);
            }
            else if (_previousFocus != null)
            {
                IInputElement previous = _previousFocus;
                _previousFocus = null;
                Dispatcher.BeginInvoke(
                    new Action(() => Keyboard.Focus(previous)),
                    DispatcherPriority.Input);
            }
        }
    }
}
