using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using MiaoWu.App.Services;
using MiaoWu.Core;
using MiaoWu.Core.Abstractions;
using MiaoWu.Infrastructure;

namespace MiaoWu.App
{
    public partial class App : Application
    {
        private AppHost _host;
        private SingleInstanceGuard _instanceGuard;
        private readonly Stopwatch _startupWatch = Stopwatch.StartNew();
        private bool _isExiting;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            DispatcherUnhandledException += OnDispatcherUnhandledException;

            _instanceGuard = new SingleInstanceGuard();
            if (!_instanceGuard.IsFirstInstance)
            {
                SingleInstanceGuard.TryNotifyExisting();
                Shutdown();
                return;
            }

            _instanceGuard.StartServer(() => Dispatcher.BeginInvoke(new Action(ShowMainWindow)));

            IAppPaths appPaths;
            try
            {
                appPaths = AppPaths.CreateDefault();
                appPaths.EnsureCreated();
            }
            catch (Exception ex)
            {
                ShowDataDirectoryFailure(ex);
                Shutdown(1);
                return;
            }

            try
            {
                _host = new AppHost(appPaths);
                _host.ExitRequested += OnHostExitRequested;
                _host.Start();
                _host.Services.Logger.Info(
                    "app.startup",
                    "启动完成，用时 " + _startupWatch.ElapsedMilliseconds + " ms。");
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    "应用启动失败。\n\n" +
                    "错误：" + ex.Message + "\n\n" +
                    "请查看日志目录，或重新启动后重试。",
                    AppInfo.Name + " - 启动失败",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);

                Shutdown(1);
            }
        }

        protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
        {
            base.OnSessionEnding(e);
            _isExiting = true;
            if (_host != null)
            {
                _host.ShutdownSync();
            }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            DispatcherUnhandledException -= OnDispatcherUnhandledException;

            if (_host != null)
            {
                _host.Dispose();
                _host = null;
            }

            if (_instanceGuard != null)
            {
                _instanceGuard.Dispose();
                _instanceGuard = null;
            }

            base.OnExit(e);
        }

        private void ShowMainWindow()
        {
            if (_host != null)
            {
                _host.ShowMainWindow();
            }
        }

        private async void OnHostExitRequested(object sender, EventArgs e)
        {
            if (_isExiting)
            {
                return;
            }

            _isExiting = true;
            try
            {
                if (_host != null)
                {
                    await _host.ShutdownAsync();
                    _host = null;
                }
            }
            catch (Exception)
            {
            }
            finally
            {
                Shutdown();
            }
        }

        private static void ShowDataDirectoryFailure(Exception ex)
        {
            MessageBox.Show(
                "初始化本地数据目录失败，应用无法启动。\n\n" +
                "数据目录：用户目录\\.miaowu（或 MIAOWU_DATA_DIR 指定目录）\n" +
                "错误：" + ex.Message + "\n\n" +
                "请确认当前账户对该目录有写入权限。\n" +
                "如果运行在受限沙箱中，请将环境变量 MIAOWU_DATA_DIR " +
                "指向工作区内的可写目录后重试。",
                AppInfo.Name + " - 启动失败",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }

        private void OnDispatcherUnhandledException(
            object sender,
            DispatcherUnhandledExceptionEventArgs e)
        {
            if (_host != null)
            {
                _host.Services.Logger.Error("app.unhandled", "未处理异常。", e.Exception);
            }

            MessageBox.Show(
                "抱歉，MiaoWu 遇到未处理的错误，应用即将关闭。\n\n" + e.Exception.Message,
                AppInfo.Name + " - 错误",
                MessageBoxButton.OK,
                MessageBoxImage.Error);

            e.Handled = true;
            Shutdown(1);
        }
    }
}
