using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MiaoWu.App.ViewModels;

namespace MiaoWu.App.Views
{
    /// <summary>
    /// 设置浮层：遮罩点击 / Esc 关闭，Tab 焦点在浮层内循环，打开时不新开 Window。
    /// </summary>
    public partial class SettingsView : UserControl
    {
        public SettingsView()
        {
            InitializeComponent();
            IsVisibleChanged += OnIsVisibleChanged;
            PreviewKeyDown += OnPreviewKeyDown;
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
