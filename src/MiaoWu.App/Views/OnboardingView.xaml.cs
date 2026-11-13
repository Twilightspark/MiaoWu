using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using MiaoWu.App.ViewModels;

namespace MiaoWu.App.Views
{
    /// <summary>
    /// 首次启动引导视图。窗口按钮是窗口壳行为，PasswordBox 不能直接绑定，
    /// 因此这两类交互留在 code-behind；业务保存全部在 <see cref="OnboardingViewModel"/>。
    /// </summary>
    public partial class OnboardingView : UserControl
    {
        private bool _syncingPassword;

        public OnboardingView()
        {
            InitializeComponent();
            DataContextChanged += OnDataContextChanged;
            Loaded += OnLoaded;
        }

        private OnboardingViewModel ViewModel
        {
            get { return DataContext as OnboardingViewModel; }
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            SyncPassword();
            UpdatePasswordHint();

            Dispatcher.BeginInvoke(new Action(FocusInitial), DispatcherPriority.Input);
        }

        private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            SyncPassword();
            UpdatePasswordHint();
        }

        private void FocusInitial()
        {
            OnboardingViewModel viewModel = ViewModel;
            if (viewModel != null && viewModel.HasWorkspace)
            {
                ProviderCombo.Focus();
                return;
            }

            if (ChooseWorkspaceButton != null && ChooseWorkspaceButton.IsVisible)
            {
                ChooseWorkspaceButton.Focus();
                return;
            }

            ProviderCombo.Focus();
        }

        private void SyncPassword()
        {
            if (ApiKeyBox == null)
            {
                return;
            }

            OnboardingViewModel viewModel = ViewModel;
            _syncingPassword = true;
            ApiKeyBox.Password = viewModel == null ? string.Empty : viewModel.ApiKey ?? string.Empty;
            _syncingPassword = false;
        }

        private void OnApiKeyChanged(object sender, RoutedEventArgs e)
        {
            if (!_syncingPassword)
            {
                OnboardingViewModel viewModel = ViewModel;
                if (viewModel != null)
                {
                    viewModel.ApiKey = ApiKeyBox.Password;
                }
            }

            UpdatePasswordHint();
        }

        private void OnApiKeyFocusChanged(object sender, KeyboardFocusChangedEventArgs e)
        {
            UpdatePasswordHint();
        }

        private void UpdatePasswordHint()
        {
            if (ApiKeyHint == null || ApiKeyBox == null)
            {
                return;
            }

            bool empty = ApiKeyBox.Password.Length == 0;
            bool focused = ApiKeyBox.IsKeyboardFocusWithin;
            ApiKeyHint.Visibility = empty && !focused ? Visibility.Visible : Visibility.Collapsed;
        }

        private void OnMinimizeClick(object sender, RoutedEventArgs e)
        {
            Window window = Window.GetWindow(this);
            if (window != null)
            {
                window.WindowState = WindowState.Minimized;
            }
        }

        private void OnMaximizeRestoreClick(object sender, RoutedEventArgs e)
        {
            Window window = Window.GetWindow(this);
            if (window == null)
            {
                return;
            }

            window.WindowState = window.WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;
        }

        private void OnCloseClick(object sender, RoutedEventArgs e)
        {
            OnboardingViewModel viewModel = ViewModel;
            if (viewModel != null)
            {
                viewModel.CloseCommand.Execute(null);
            }
        }
    }
}
