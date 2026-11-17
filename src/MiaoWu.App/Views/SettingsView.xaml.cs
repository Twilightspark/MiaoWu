using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MiaoWu.App.ViewModels;

namespace MiaoWu.App.Views
{
    /// <summary>
    /// 设置浮层：遮罩点击 / Esc 关闭，Tab 焦点在浮层内循环，打开时不新开 Window。
    /// 模型表单展开时把表单滚进可视区。
    /// </summary>
    public partial class SettingsView : UserControl
    {
        private SettingsViewModel _viewModel;
        private bool _syncingPasswords;

        public SettingsView()
        {
            InitializeComponent();
            IsVisibleChanged += OnIsVisibleChanged;
            PreviewKeyDown += OnPreviewKeyDown;
            DataContextChanged += OnDataContextChanged;
        }

        private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (_viewModel != null)
            {
                _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            }

            _viewModel = e.NewValue as SettingsViewModel;
            if (_viewModel != null)
            {
                _viewModel.PropertyChanged += OnViewModelPropertyChanged;
            }

            SyncPasswordBoxes();
        }

        private void OnViewModelPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            SyncPasswordBoxes();

            if (e.PropertyName != "HasProviderForm" || _viewModel == null || !_viewModel.HasProviderForm)
            {
                return;
            }

            Dispatcher.BeginInvoke(new Action(() =>
            {
                ProviderFormPanel.UpdateLayout();
                ProviderFormPanel.BringIntoView();
            }));
        }

        /// <summary>
        /// API Key 是 PasswordBox，不能直接绑定；这里把 VM 的当前值同步到框里。
        /// 密钥永远不回显已保存值，所以同步只用于清空（切换预置、关闭表单）。
        /// </summary>
        private void SyncPasswordBoxes()
        {
            if (_viewModel == null || ThirdPartyKeyBox == null || CustomKeyBox == null)
            {
                return;
            }

            _syncingPasswords = true;
            try
            {
                string thirdParty = _viewModel.ThirdPartyApiKey ?? string.Empty;
                if (!string.Equals(ThirdPartyKeyBox.Password, thirdParty, StringComparison.Ordinal))
                {
                    ThirdPartyKeyBox.Password = thirdParty;
                }

                ProviderEditorViewModel draft = _viewModel.DraftProvider;
                string custom = draft == null ? string.Empty : draft.ApiKey ?? string.Empty;
                if (!string.Equals(CustomKeyBox.Password, custom, StringComparison.Ordinal))
                {
                    CustomKeyBox.Password = custom;
                }
            }
            finally
            {
                _syncingPasswords = false;
            }
        }

        private void OnThirdPartyKeyChanged(object sender, RoutedEventArgs e)
        {
            if (_syncingPasswords || _viewModel == null)
            {
                return;
            }

            _viewModel.ThirdPartyApiKey = ThirdPartyKeyBox.Password;
        }

        private void OnCustomKeyChanged(object sender, RoutedEventArgs e)
        {
            if (_syncingPasswords || _viewModel == null || _viewModel.DraftProvider == null)
            {
                return;
            }

            _viewModel.DraftProvider.ApiKey = CustomKeyBox.Password;
        }

        private void OnScrimClick(object sender, MouseButtonEventArgs e)
        {
            SettingsViewModel viewModel = DataContext as SettingsViewModel;
            if (viewModel != null)
            {
                viewModel.CloseCommand.Execute(null);
            }
        }

        private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (e.NewValue is bool && (bool)e.NewValue)
            {
                Dispatcher.BeginInvoke(new Action(FocusFirst));
            }
        }

        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            SettingsViewModel viewModel = DataContext as SettingsViewModel;
            if (viewModel == null)
            {
                return;
            }

            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                viewModel.CloseCommand.Execute(null);
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

        private void FocusFirst()
        {
            List<UIElement> focusable = CollectFocusable(this);
            if (focusable.Count > 0)
            {
                focusable[0].Focus();
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
