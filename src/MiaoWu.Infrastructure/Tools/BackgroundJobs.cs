using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MiaoWu.Infrastructure.Tools
{
    /// <summary>后台任务的状态快照。</summary>
    public sealed class BackgroundJobInfo
    {
        public string Id { get; set; }

        public string SessionId { get; set; }

        public string Command { get; set; }

        public string Description { get; set; }

        public DateTimeOffset StartedAt { get; set; }

        public DateTimeOffset? CompletedAt { get; set; }

        public bool Running { get; set; }

        public int? ExitCode { get; set; }

        public bool Killed { get; set; }
    }

    /// <summary>
    /// 与任务种类无关的后台任务注册表（对齐 DSH 的 ctx.jobs 最小版）：
    /// 只管理进程与输出环，不做唤醒、不做会话投递。
    /// </summary>
    public sealed class BackgroundJobRegistry : IDisposable
    {
        private const int MaxOutputChars = 256 * 1024;

        private readonly object _gate = new object();
        private readonly Dictionary<string, BackgroundJob> _jobs =
            new Dictionary<string, BackgroundJob>(StringComparer.OrdinalIgnoreCase);

        private bool _disposed;

        public BackgroundJob Start(string sessionId, string command, string workingDirectory, string description)
        {
            if (string.IsNullOrWhiteSpace(command))
            {
                throw new ArgumentException("后台命令不能为空。", nameof(command));
            }

            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(BackgroundJobRegistry));
            }

            string id = "job_" + Guid.NewGuid().ToString("N").Substring(0, 12);
            var job = new BackgroundJob(id, sessionId, command, description ?? string.Empty);

            var start = new ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                WorkingDirectory = string.IsNullOrWhiteSpace(workingDirectory)
                    ? Environment.CurrentDirectory
                    : workingDirectory
            };

            // -EncodedCommand 用 Base64(UTF-16LE) 传命令，彻底避开引号转义。
            // 先关掉进度流：stderr 被重定向时 PowerShell 5.1 会把进度记录写成 CLIXML
            // （首行 `#< CLIXML`），后台任务里既没用又会顶掉真正的输出。
            const string prelude = "$ProgressPreference = 'SilentlyContinue'; ";
            start.Arguments = "-NoLogo -NoProfile -NonInteractive -EncodedCommand "
                + Convert.ToBase64String(Encoding.Unicode.GetBytes(prelude + command));

            var process = new Process { StartInfo = start, EnableRaisingEvents = true };
            process.OutputDataReceived += (sender, args) =>
            {
                if (!string.IsNullOrEmpty(args.Data))
                {
                    job.Append(args.Data + "\n");
                }
            };
            process.ErrorDataReceived += (sender, args) =>
            {
                if (!string.IsNullOrEmpty(args.Data))
                {
                    job.Append(args.Data + "\n");
                }
            };
            process.Exited += (sender, args) =>
            {
                // Exited 可能先于最后一批 OutputDataReceived 到达；WaitForExit() 会等异步读取
                // 把管道读完，否则“任务已结束”后 job_output 还可能少最后几行。
                try
                {
                    ((Process)sender).WaitForExit();
                }
                catch (Exception)
                {
                }

                job.MarkExited();
            };

            lock (_gate)
            {
                _jobs[id] = job;
            }

            try
            {
                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                job.Attach(process);
            }
            catch (Exception exception)
            {
                job.MarkFailed("后台任务启动失败：" + exception.Message);
                throw;
            }

            return job;
        }

        public BackgroundJob Find(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                return null;
            }

            lock (_gate)
            {
                BackgroundJob job;
                return _jobs.TryGetValue(id, out job) ? job : null;
            }
        }

        public IReadOnlyList<BackgroundJobInfo> List(string sessionId)
        {
            var list = new List<BackgroundJobInfo>();
            lock (_gate)
            {
                foreach (BackgroundJob job in _jobs.Values)
                {
                    if (!string.IsNullOrWhiteSpace(sessionId)
                        && !string.Equals(job.SessionId, sessionId, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    list.Add(job.Snapshot());
                }
            }

            list.Sort((a, b) => b.StartedAt.CompareTo(a.StartedAt));
            return list;
        }

        /// <summary>
        /// 移除本会话已结束的任务记录（运行中的不动）。注册表本身不会自动丢弃历史，
        /// 标题栏菜单用这个动作把「已结束」组清干净。
        /// </summary>
        public int RemoveFinished(string sessionId)
        {
            lock (_gate)
            {
                var remove = new List<string>();
                foreach (BackgroundJob job in _jobs.Values)
                {
                    if (job.IsRunning)
                    {
                        continue;
                    }

                    if (!string.IsNullOrWhiteSpace(sessionId)
                        && !string.Equals(job.SessionId, sessionId, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    remove.Add(job.Id);
                }

                foreach (string id in remove)
                {
                    _jobs.Remove(id);
                }

                return remove.Count;
            }
        }

        public void Dispose()
        {
            List<BackgroundJob> jobs;
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                jobs = new List<BackgroundJob>(_jobs.Values);
            }

            foreach (BackgroundJob job in jobs)
            {
                try
                {
                    job.Kill();
                }
                catch (Exception)
                {
                }
            }
        }

        /// <summary>一个后台进程：输出环 + 生命周期状态。所有公开成员线程安全。</summary>
        public sealed class BackgroundJob
        {
            private readonly object _jobGate = new object();
            private readonly StringBuilder _output = new StringBuilder();
            private readonly DateTimeOffset _startedAt;
            private Process _process;
            private TaskCompletionSource<object> _outputSignal;
            private int _nextReadOffset;
            private int _droppedChars;
            private bool _running = true;
            private bool _killed;
            private int? _exitCode;
            private string _failure;
            private DateTimeOffset? _completedAt;

            internal BackgroundJob(string id, string sessionId, string command, string description)
            {
                Id = id;
                SessionId = sessionId;
                Command = command;
                Description = description;
                _startedAt = DateTimeOffset.UtcNow;
            }

            public string Id { get; }

            public string SessionId { get; }

            public string Command { get; }

            public string Description { get; }

            internal void Attach(Process process)
            {
                lock (_jobGate)
                {
                    _process = process;
                }
            }

            internal void Append(string text)
            {
                if (string.IsNullOrEmpty(text))
                {
                    return;
                }

                TaskCompletionSource<object> signal;
                lock (_jobGate)
                {
                    _output.Append(text);
                    while (_output.Length > MaxOutputChars)
                    {
                        int cut = _output.Length - MaxOutputChars;
                        _output.Remove(0, cut);
                        _droppedChars += cut;
                    }

                    signal = _outputSignal;
                }

                if (signal != null)
                {
                    signal.TrySetResult(null);
                }
            }

            /// <summary>等下一块输出；被取消时抛 <see cref="OperationCanceledException"/>。</summary>
            public async Task WaitForOutputAsync(CancellationToken cancellationToken)
            {
                TaskCompletionSource<object> signal;
                lock (_jobGate)
                {
                    if (_outputSignal == null)
                    {
                        _outputSignal = new TaskCompletionSource<object>(
                            TaskCreationOptions.RunContinuationsAsynchronously);
                    }

                    signal = _outputSignal;
                }

                Task cancel = Task.Delay(Timeout.Infinite, cancellationToken);
                await Task.WhenAny(signal.Task, cancel).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
            }

            internal void MarkExited()
            {
                TaskCompletionSource<object> signal;
                lock (_jobGate)
                {
                    _running = false;
                    _completedAt = DateTimeOffset.UtcNow;
                    try
                    {
                        if (_process != null)
                        {
                            _exitCode = _process.ExitCode;
                        }
                    }
                    catch (Exception)
                    {
                    }

                    signal = _outputSignal;
                }

                // 结束也要叫醒等待者：waitMs 内部等的是“新输出或进程结束”，
                // 只在 Append 时发信号会让无输出的任务一直等到超时。
                if (signal != null)
                {
                    signal.TrySetResult(null);
                }
            }

            internal void MarkFailed(string message)
            {
                TaskCompletionSource<object> signal;
                lock (_jobGate)
                {
                    _running = false;
                    _failure = message;
                    _completedAt = DateTimeOffset.UtcNow;
                    signal = _outputSignal;
                }

                if (signal != null)
                {
                    signal.TrySetResult(null);
                }
            }

            /// <summary>读取上次读取之后的新输出；没有新输出时返回说明。</summary>
            public string Read()
            {
                string text;
                return TryReadNew(out text)
                    ? text
                    : (_running ? "(还没有新输出)" : "(没有更多输出)");
            }

            /// <summary>有未读输出时取出并返回 true；没有则返回 false（不改变读取位置）。</summary>
            public bool TryReadNew(out string text)
            {
                lock (_jobGate)
                {
                    int start = Math.Max(0, _nextReadOffset - _droppedChars);
                    if (start >= _output.Length)
                    {
                        text = string.Empty;
                        return false;
                    }

                    text = _output.ToString(start, _output.Length - start);
                    _nextReadOffset = _droppedChars + _output.Length;

                    if (_droppedChars > 0 && start == 0 && text.Length == _output.Length)
                    {
                        text = "[更早的输出已丢弃，只保留最后 " + MaxOutputChars + " 字符]\n" + text;
                    }

                    return true;
                }
            }

            /// <summary>
            /// 只读地取输出末尾 <paramref name="maxChars"/> 个字符，不改变 <see cref="Read"/> 的读取位置。
            /// 标题栏的任务菜单用它做实时输出；读走一段输出仍应由 <see cref="Read"/> 负责。
            /// </summary>
            public string PeekTail(int maxChars)
            {
                if (maxChars <= 0)
                {
                    return string.Empty;
                }

                lock (_jobGate)
                {
                    if (_output.Length == 0)
                    {
                        return string.Empty;
                    }

                    int start = Math.Max(0, _output.Length - maxChars);
                    string text = _output.ToString(start, _output.Length - start);
                    if (_droppedChars > 0 && start == 0)
                    {
                        return "[更早的输出已丢弃，只保留最后 " + MaxOutputChars + " 字符]\n" + text;
                    }

                    return text;
                }
            }

            /// <summary>
            /// 等新输出：有未读就立刻返回；没有就等输出信号或进程结束，最多等 <paramref name="waitMs"/>。
            /// 超时或进程结束且没有新输出时，按 <see cref="Read"/> 的规则返回说明文字。
            /// </summary>
            public async Task<string> WaitForReadAsync(int waitMs, CancellationToken cancellationToken)
            {
                string text;
                if (TryReadNew(out text))
                {
                    return text;
                }

                if (waitMs <= 0 || !IsRunning)
                {
                    return Read();
                }

                DateTime deadline = DateTime.UtcNow.AddMilliseconds(waitMs);
                while (true)
                {
                    TaskCompletionSource<object> signal;
                    lock (_jobGate)
                    {
                        if (_outputSignal == null || _outputSignal.Task.IsCompleted)
                        {
                            _outputSignal = new TaskCompletionSource<object>(
                                TaskCreationOptions.RunContinuationsAsynchronously);
                        }

                        signal = _outputSignal;
                    }

                    int remaining = (int)(deadline - DateTime.UtcNow).TotalMilliseconds;
                    if (remaining <= 0)
                    {
                        return Read();
                    }

                    var delay = new CancellationTokenSource();
                    try
                    {
                        delay.CancelAfter(remaining);
                        Task cancelled = Task.Delay(Timeout.Infinite, delay.Token);
                        await Task.WhenAny(signal.Task, cancelled).ConfigureAwait(false);
                    }
                    finally
                    {
                        delay.Dispose();
                    }

                    cancellationToken.ThrowIfCancellationRequested();

                    if (TryReadNew(out text))
                    {
                        return text;
                    }

                    if (!IsRunning)
                    {
                        return Read();
                    }
                }
            }

            /// <summary>是否仍在运行。</summary>
            public bool IsRunning
            {
                get
                {
                    lock (_jobGate)
                    {
                        return _running;
                    }
                }
            }

            public void Kill()
            {
                Process process;
                lock (_jobGate)
                {
                    if (!_running)
                    {
                        return;
                    }

                    _killed = true;
                    process = _process;
                }

                if (process == null)
                {
                    return;
                }

                try
                {
                    Process.Start(new ProcessStartInfo("taskkill.exe")
                    {
                        Arguments = "/PID " + process.Id + " /T /F",
                        UseShellExecute = false,
                        CreateNoWindow = true
                    });
                }
                catch (Exception)
                {
                }

                try
                {
                    process.Kill();
                }
                catch (Exception)
                {
                }
            }

            public BackgroundJobInfo Snapshot()
            {
                lock (_jobGate)
                {
                    return new BackgroundJobInfo
                    {
                        Id = Id,
                        SessionId = SessionId,
                        Command = Command,
                        Description = Description,
                        StartedAt = _startedAt,
                        CompletedAt = _completedAt,
                        Running = _running,
                        ExitCode = _exitCode,
                        Killed = _killed
                    };
                }
            }

            public string Describe()
            {
                lock (_jobGate)
                {
                    string state = _running
                        ? "运行中"
                        : _killed
                            ? "已终止"
                            : _failure != null
                                ? "启动失败"
                                : "已结束（退出码 " + (_exitCode.HasValue ? _exitCode.Value.ToString() : "?") + "）";
                    return Id + " · " + state;
                }
            }
        }
    }
}
