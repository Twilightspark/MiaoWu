using System;
using System.Windows;
using System.Windows.Controls;
using MiaoWu.App.ViewModels;

namespace MiaoWu.App.Views
{
    /// <summary>向导视图：PasswordBox 不能直接绑定，这里把输入写回 ViewModel。</summary>
    public partial class WizardView : UserControl
    {
        private bool _syncing;

        public WizardView()
        {
            InitializeComponent();
            DataContextChanged += OnDataContextChanged;
            IsVisibleChanged += OnIsVisibleChanged;
        }

        private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            SyncPasswordBox();
        }

        private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (e.NewValue is bool && (bool)e.NewValue)
            {
                Dispatcher.BeginInvoke(new Action(SyncPasswordBox));
            }
        }

        private void SyncPasswordBox()
        {
            WizardViewModel viewModel = DataContext as WizardViewModel;
            if (viewModel == null || ApiKeyBox == null)
            {
                return;
            }

            _syncing = true;
            ApiKeyBox.Password = viewModel.ApiKey ?? string.Empty;
            _syncing = false;
        }

        private void OnApiKeyChanged(object sender, RoutedEventArgs e)
        {
            if (_syncing)
            {
                return;
            }

            WizardViewModel viewModel = DataContext as WizardViewModel;
            if (viewModel != null)
            {
                viewModel.ApiKey = ApiKeyBox.Password;
            }
        }
    }
}
