using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MiaoWu.App.Services
{
    /// <summary>
    /// 单实例：命名 Mutex 判定首个实例；命名管道把第二实例的“显示主窗口”
    /// 转给首个实例后退出。Win7 可用，不依赖 WPF 或第三方库。
    /// </summary>
    public sealed class SingleInstanceGuard : IDisposable
    {
        private const string MutexName = "Local\\MiaoWu.Instance.v1";
        private const string PipeName = "MiaoWu.Instance.Pipe.v1";

        private readonly Mutex _mutex;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private Task _serverTask;
        private bool _ownsMutex;

        public SingleInstanceGuard()
        {
            bool created;
            _mutex = new Mutex(true, MutexName, out created);
            IsFirstInstance = created;
            _ownsMutex = created;
        }

        public bool IsFirstInstance { get; private set; }

        /// <summary>首个实例监听管道；回调在后台线程触发，调用方自行切到 UI 线程。</summary>
        public void StartServer(Action onShowRequested)
        {
            if (!IsFirstInstance || _serverTask != null)
            {
                return;
            }

            _serverTask = Task.Run(() => ServerLoopAsync(onShowRequested));
        }

        /// <summary>第二个实例调用：通知首个实例显示窗口。失败返回 false。</summary>
        public static bool TryNotifyExisting()
        {
            try
            {
                using (var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out))
                {
                    client.Connect(800);
                    using (var writer = new StreamWriter(client, new UTF8Encoding(false)))
                    {
                        writer.WriteLine("show");
                        writer.Flush();
                    }
                }

                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public void Dispose()
        {
            try
            {
                _cts.Cancel();
            }
            catch (Exception)
            {
            }

            if (_ownsMutex)
            {
                try
                {
                    _mutex.ReleaseMutex();
                }
                catch (ApplicationException)
                {
                }

                _ownsMutex = false;
            }

            try
            {
                _mutex.Dispose();
            }
            catch (Exception)
            {
            }
        }

        private async Task ServerLoopAsync(Action onShowRequested)
        {
            while (!_cts.IsCancellationRequested)
            {
                NamedPipeServerStream server = null;
                try
                {
                    server = new NamedPipeServerStream(
                        PipeName,
                        PipeDirection.In,
                        1,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous);

                    await server.WaitForConnectionAsync(_cts.Token).ConfigureAwait(false);

                    using (var reader = new StreamReader(server, new UTF8Encoding(false)))
                    {
                        string command = await reader.ReadLineAsync().ConfigureAwait(false);
                        if (string.Equals(command, "show", StringComparison.OrdinalIgnoreCase)
                            && onShowRequested != null)
                        {
                            onShowRequested();
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception)
                {
                    await Task.Delay(200).ConfigureAwait(false);
                }
                finally
                {
                    if (server != null)
                    {
                        try
                        {
                            server.Dispose();
                        }
                        catch (Exception)
                        {
                        }
                    }
                }
            }
        }
    }
}
