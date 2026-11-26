using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using MiaoWu.App.Mvvm;
using MiaoWu.Core.Updates;

namespace MiaoWu.App.Services
{
    /// <summary>
    /// 更新流程的唯一协调者：手动检查（设置 → 关于）与自动更新共用同一把互斥锁，
    /// 避免两个入口同时请求 / 下载。自动更新开启时每 4 小时检查一次；发现新版本后
    /// 静默下载，完成后 <see cref="IsUpdateReady"/> 置位，左栏设置按钮右侧出现更新入口。
    /// </summary>
    public sealed class UpdateCoordinator : ObservableObject, IDisposable
    {
        /// <summary>启动后先等一会儿再检查，避免和首屏渲染、模型目录刷新抢网络。</summary>
        public static readonly TimeSpan FirstCheckDelay = TimeSpan.FromMinutes(2);

        /// <summary>程序持续运行时的自动检查间隔。</summary>
        public static readonly TimeSpan CheckInterval = TimeSpan.FromHours(4);

        private readonly AppServices _app;
        private readonly Dispatcher _dispatcher;
        private readonly DispatcherTimer _timer;
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
        private readonly CancellationTokenSource _lifetime = new CancellationTokenSource();

        private bool _isChecking;
        private bool _isDownloading;
        private bool _isUpdateReady;
        private string _readyVersion;
        private string _readyFilePath;
        private bool _disposed;

        public UpdateCoordinator(AppServices app, Dispatcher dispatcher)
        {
            _app = app ?? throw new ArgumentNullException(nameof(app));
            _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
            _timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
            {
                Interval = CheckInterval
            };
            _timer.Tick += OnTimerTick;
        }

        /// <summary>已有安装包等待安装：左栏显示更新按钮。</summary>
        public bool IsUpdateReady
        {
            get { return _isUpdateReady; }
        }

        /// <summary>已下载安装包对应的版本号；没有时为空。</summary>
        public string ReadyVersion
        {
            get { return _readyVersion; }
        }

        /// <summary>检查或下载正在进行；设置页按钮据此置灰并显示旋转图标。</summary>
        public bool IsBusy
        {
            get { return _isChecking || _isDownloading; }
        }

        public bool IsChecking
        {
            get { return _isChecking; }
        }

        public bool IsDownloading
        {
            get { return _isDownloading; }
        }

        /// <summary>设置里的「自动更新」当前是否为开。</summary>
        public bool AutoUpdateEnabled
        {
            get { return _app.Settings.Agent.AutoUpdate; }
        }

        /// <summary>启动后调用：自动更新开启时安排第一次后台检查。</summary>
        public void Start()
        {
            if (_disposed || !AutoUpdateEnabled)
            {
                return;
            }

            if (!_timer.IsEnabled)
            {
                Schedule(FirstCheckDelay);
            }
        }

        /// <summary>关闭窗口 / 退出时停止计时器并取消进行中的检查与下载。</summary>
        public void Stop()
        {
            _timer.Stop();
            try
            {
                _lifetime.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        /// <summary>设置页切换「自动更新」后调用：开则安排检查，关则停止计时器。</summary>
        public void OnAutoUpdateSettingChanged()
        {
            if (_disposed)
            {
                return;
            }

            if (AutoUpdateEnabled)
            {
                if (!_timer.IsEnabled)
                {
                    Schedule(FirstCheckDelay);
                }
            }
            else
            {
                _timer.Stop();
            }
        }

        /// <summary>
        /// 手动检查。已有检查 / 下载在跑时返回 null；网络失败通过结果对象的
        /// <see cref="UpdateCheckResult.Message"/> 返回，不抛异常。
        /// </summary>
        public async Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken)
        {
            if (_disposed || !_gate.Wait(0))
            {
                return null;
            }

            IsCheckingState(true);
            try
            {
                UpdateCheckResult result = await _app.Updates.CheckAsync(cancellationToken);
                if (result != null && result.Succeeded && result.HasUpdate)
                {
                    _app.Logger.Info(
                        "update.check",
                        "发现新版本 " + result.LatestVersion + "（当前 " + result.CurrentVersion + "）。");
                }

                return result;
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            catch (Exception exception)
            {
                _app.Logger.Warn("update.check", "检查更新失败：" + exception.Message);
                return new UpdateCheckResult
                {
                    Succeeded = false,
                    Message = "检查更新失败：" + exception.Message
                };
            }
            finally
            {
                IsCheckingState(false);
                _gate.Release();
            }
        }

        /// <summary>
        /// 下载安装包。成功后自动把 <see cref="IsUpdateReady"/> 置位；同一时刻只允许一个下载。
        /// 另一个更新任务在跑时返回失败结果而不是等待。
        /// </summary>
        public async Task<UpdateDownloadResult> DownloadAsync(
            UpdateCheckResult update,
            IProgress<double> progress,
            CancellationToken cancellationToken)
        {
            if (update == null || !update.HasUpdate)
            {
                return DownloadFailure("没有可下载的更新。");
            }

            if (_disposed || !_gate.Wait(0))
            {
                return DownloadFailure("另一个更新任务正在进行，请稍后重试。");
            }

            IsDownloadingState(true);
            try
            {
                UpdateDownloadResult result = await _app.UpdateDownloader.DownloadAsync(
                    update,
                    progress,
                    cancellationToken);

                if (result != null && result.Succeeded)
                {
                    SetReady(update.LatestVersion, result.FilePath);
                    _app.Logger.Info(
                        "update.download",
                        "安装包已下载：" + result.FilePath + "（" + result.Bytes + " 字节）。");
                }
                else
                {
                    _app.Logger.Warn(
                        "update.download",
                        "下载安装包失败：" + (result == null ? "没有返回结果" : result.Message));
                }

                return result ?? DownloadFailure("下载安装包失败：没有返回结果。");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                _app.Logger.Warn("update.download", "下载安装包失败：" + exception.Message);
                return DownloadFailure("下载安装包失败：" + exception.Message);
            }
            finally
            {
                IsDownloadingState(false);
                _gate.Release();
            }
        }

        /// <summary>启动已下载的安装包。返回 false 时 <paramref name="error"/> 是给用户看的原因。</summary>
        public bool TryInstall(out string error)
        {
            error = null;
            if (!_isUpdateReady || string.IsNullOrWhiteSpace(_readyFilePath))
            {
                error = "当前没有已下载的更新。";
                return false;
            }

            if (!File.Exists(_readyFilePath))
            {
                ClearReady();
                error = "安装包已被删除，请重新检查更新。";
                return false;
            }

            if (!_app.UpdateInstaller.Install(_readyFilePath, out error))
            {
                _app.Logger.Warn("update.install", "无法启动更新安装程序：" + error);
                return false;
            }

            _app.Logger.Info("update.install", "已启动更新安装程序：" + _readyFilePath);
            return true;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _timer.Stop();
            _timer.Tick -= OnTimerTick;

            try
            {
                _lifetime.Cancel();
                _lifetime.Dispose();
            }
            catch (ObjectDisposedException)
            {
            }

            // 不 Dispose _gate：退出时可能还有一次检查 / 下载在 finally 里 Release，
            // 释放信号量会让那次 await 以 ObjectDisposedException 结束。
        }

        private void Schedule(TimeSpan delay)
        {
            _timer.Stop();
            _timer.Interval = delay <= TimeSpan.Zero ? CheckInterval : delay;
            _timer.Start();
        }

        private async void OnTimerTick(object sender, EventArgs e)
        {
            _timer.Stop();
            try
            {
                if (AutoUpdateEnabled && !IsUpdateReady)
                {
                    await RunAutoUpdateAsync(_lifetime.Token);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception exception)
            {
                _app.Logger.Warn("update.auto", "自动更新失败：" + exception.Message);
            }
            finally
            {
                if (!_disposed && AutoUpdateEnabled)
                {
                    Schedule(CheckInterval);
                }
            }
        }

        /// <summary>自动流程：检查 → 有更新就静默下载；没有则什么都不做。</summary>
        private async Task RunAutoUpdateAsync(CancellationToken cancellationToken)
        {
            UpdateCheckResult check = await CheckAsync(cancellationToken);
            if (check == null)
            {
                return;
            }

            if (!check.Succeeded)
            {
                _app.Logger.Warn("update.auto", "自动检查更新失败：" + check.Message);
                return;
            }

            if (!check.HasUpdate)
            {
                return;
            }

            UpdateDownloadResult download = await DownloadAsync(check, null, cancellationToken);
            if (download == null || !download.Succeeded)
            {
                _app.Logger.Warn(
                    "update.auto",
                    "自动下载更新失败：" + (download == null ? "没有返回结果" : download.Message));
            }
        }

        private void ClearReady()
        {
            _readyVersion = null;
            _readyFilePath = null;
            if (_isUpdateReady)
            {
                _isUpdateReady = false;
                RaiseOnUi(nameof(IsUpdateReady));
            }

            RaiseOnUi(nameof(ReadyVersion));
        }

        private void SetReady(string version, string filePath)
        {
            _readyVersion = version;
            _readyFilePath = filePath;
            if (!_isUpdateReady)
            {
                _isUpdateReady = true;
                RaiseOnUi(nameof(IsUpdateReady));
            }

            RaiseOnUi(nameof(ReadyVersion));
        }

        private void IsCheckingState(bool value)
        {
            if (_isChecking == value)
            {
                return;
            }

            _isChecking = value;
            RaiseOnUi(nameof(IsChecking));
            RaiseOnUi(nameof(IsBusy));
        }

        private void IsDownloadingState(bool value)
        {
            if (_isDownloading == value)
            {
                return;
            }

            _isDownloading = value;
            RaiseOnUi(nameof(IsDownloading));
            RaiseOnUi(nameof(IsBusy));
        }

        /// <summary>
        /// 下载 / 检查的 await 可能在后台线程收尾（服务内部用了 ConfigureAwait(false)，
        /// 或调用方没有 SynchronizationContext）；属性通知必须切回 UI 线程，
        /// 否则左栏更新入口的 Visibility 绑定会停在旧值。
        /// </summary>
        private void RaiseOnUi(string propertyName)
        {
            if (_dispatcher.CheckAccess())
            {
                OnPropertyChanged(propertyName);
                return;
            }

            try
            {
                _dispatcher.BeginInvoke(new Action(() => OnPropertyChanged(propertyName)));
            }
            catch (InvalidOperationException)
            {
                // Dispatcher 已关闭（退出中），不再通知。
            }
        }

        private static UpdateDownloadResult DownloadFailure(string message)
        {
            return new UpdateDownloadResult
            {
                Succeeded = false,
                Message = message
            };
        }
    }
}
