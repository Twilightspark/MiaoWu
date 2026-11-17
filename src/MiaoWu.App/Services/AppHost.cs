using System;
using System.Threading.Tasks;
using System.Windows;
using MiaoWu.App.ViewModels;
using MiaoWu.App.Views;
using MiaoWu.Core.Abstractions;
using MiaoWu.Infrastructure.Storage;

namespace MiaoWu.App.Services
{
    /// <summary>
    /// 应用组合根：装配服务、主窗口与托盘，并统一关停顺序。
    /// App.xaml.cs 只负责启动、单实例与全局异常。
    /// </summary>
    public sealed class AppHost : IDisposable
    {
        private const string TrayTooltipRunning = "MiaoWu · 本地智能体工作台";
        private const string TrayTooltipBackground = "MiaoWu · 后台运行中";

        private readonly AppServices _services;
        private readonly AccessibilityService _accessibility;
        private MainWindow _mainWindow;
        private TrayService _tray;
        private bool _hiddenNoticeShown;
        private bool _disposed;

        public AppHost(IAppPaths paths)
        {
            if (paths == null)
            {
                throw new ArgumentNullException(nameof(paths));
            }

            Paths = paths;
            _services = new AppServices(paths);
            _accessibility = new AccessibilityService();
            _accessibility.Apply();

            MainViewModel = new MainWindowViewModel(
                _services.SettingsStore,
                _services.WorkspaceStore,
                _services.SessionCatalog,
                _services.FileCatalog,
                _services.SessionCatalog,
                _services.FileCatalog,
                _services.PreviewService,
                new WindowsFolderPicker(),
                new WindowsFilePicker(),
                new MessageBoxDialogService(),
                new SystemClipboardService(),
                null,
                _services);

            MainViewModel.ExitRequested += OnMainViewModelExitRequested;
            MainViewModel.HiddenToTray += OnHiddenToTray;
        }

        public event EventHandler ExitRequested;

        public IAppPaths Paths { get; }

        public MainWindowViewModel MainViewModel { get; }

        public AppServices Services
        {
            get { return _services; }
        }

        public void Start()
        {
            _services.CleanupToolOutputCache();
            CreateTray();

            bool trayAvailable = _tray != null && _tray.IsAvailable;
            MainViewModel.SetTrayAvailability(trayAvailable);
            if (!trayAvailable)
            {
                _services.Logger.Warn(
                    "app.tray",
                    "当前进程完整性低于 Medium（常见于 VS Code/沙箱/受限终端），"
                    + "Windows 不注册托盘图标；关闭窗口将降级为最小化到任务栏。"
                    + "请从资源管理器直接运行 MiaoWu.exe 以获得托盘图标。");
            }
            else
            {
                _services.Logger.Info(
                    "app.tray",
                    "托盘图标已创建（进程完整性 Medium 及以上）。");
            }

            // 首次启动即使配置了“只显示托盘”也先显示主窗口的引导页。
            // 托盘不可用时也不能静默隐藏，否则窗口没有可见入口。
            if (!_services.Settings.FirstLaunch
                && _services.Settings.Agent.StartMinimized
                && trayAvailable)
            {
                UpdateTrayTooltip(true);
                _tray.ShowBalloon("MiaoWu 正在后台运行", "双击托盘图标打开主窗口。");
            }
            else
            {
                ShowMainWindow();
            }
        }

        public void ShowMainWindow()
        {
            UpdateTrayTooltip(false);

            if (_mainWindow != null && _mainWindow.IsVisible)
            {
                if (_mainWindow.WindowState == WindowState.Minimized)
                {
                    _mainWindow.WindowState = WindowState.Normal;
                }

                _mainWindow.Activate();
                return;
            }

            if (_mainWindow == null)
            {
                _mainWindow = new MainWindow(MainViewModel);
            }

            _mainWindow.Show();
            _mainWindow.Activate();
            MainViewModel.ResumeConversation();
        }

        public void HideMainWindow()
        {
            if (_mainWindow != null && _mainWindow.IsVisible)
            {
                _mainWindow.Hide();
            }
        }

        public async Task ShutdownAsync()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            MainViewModel.PrepareExit();

            try
            {
                if (_mainWindow != null)
                {
                    _mainWindow.Close();
                    _mainWindow = null;
                }
            }
            catch (Exception)
            {
            }

            DisposeTray();

            try
            {
                _accessibility.Dispose();
            }
            catch (Exception)
            {
            }

            try
            {
                await _services.Runtime.ShutdownAsync(TimeSpan.FromSeconds(3));
            }
            catch (Exception)
            {
            }

            _services.Dispose();
        }

        public void ShutdownSync()
        {
            try
            {
                ShutdownAsync().Wait(TimeSpan.FromSeconds(5));
            }
            catch (Exception)
            {
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            ShutdownAsync().GetAwaiter().GetResult();
        }

        private void OnMainViewModelExitRequested(object sender, EventArgs e)
        {
            EventHandler handler = ExitRequested;
            if (handler != null)
            {
                handler(this, EventArgs.Empty);
            }
        }

        private void OnHiddenToTray(object sender, EventArgs e)
        {
            UpdateTrayTooltip(true);

            if (_hiddenNoticeShown || _tray == null)
            {
                return;
            }

            _hiddenNoticeShown = true;
            _tray.ShowBalloon(
                "MiaoWu 仍在后台运行",
                "关闭窗口不会中断正在生成的回答；若任务栏未显示图标，请点击 ^ 并固定 MiaoWu。");
        }

        private void UpdateTrayTooltip(bool background)
        {
            if (_tray != null)
            {
                _tray.UpdateTooltip(background ? TrayTooltipBackground : TrayTooltipRunning);
            }
        }

        private void CreateTray()
        {
            if (_tray != null)
            {
                return;
            }

            _tray = new TrayService(TrayTooltipRunning);
            _tray.OpenRequested += (sender, args) => ShowMainWindow();
            _tray.NewSessionRequested += (sender, args) =>
            {
                ShowMainWindow();
                MainViewModel.RequestNewSession();
            };
            _tray.CheckUpdateRequested += (sender, args) =>
            {
                ShowMainWindow();
                MainViewModel.ShowSettings();
                if (MainViewModel.Settings != null)
                {
                    MainViewModel.Settings.SelectPage(SettingsPage.About);
                    MainViewModel.Settings.CheckUpdateCommand.Execute(null);
                }
            };
            _tray.ExitRequested += (sender, args) =>
            {
                MainViewModel.PrepareExit();
                ShutdownAsync().ContinueWith(_ =>
                {
                    Application.Current.Dispatcher.BeginInvoke(new Action(() => Application.Current.Shutdown()));
                });
            };
        }

        private void DisposeTray()
        {
            if (_tray != null)
            {
                _tray.Dispose();
                _tray = null;
            }
        }
    }
}
