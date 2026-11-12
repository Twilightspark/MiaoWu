using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MiaoWu.Core.Agents.Tools;

namespace MiaoWu.Infrastructure.Tools
{
    /// <summary>
    /// run_command：在工作区内执行 PowerShell / cmd 命令。命令始终需要审批，
    /// 默认 60 秒超时（上限 10 分钟），取消/超时会杀掉整个进程树。
    /// </summary>
    public sealed class RunCommandTool : IAgentTool, IToolRiskEvaluator, IToolAuditRedactor
    {
        private const int MaxCommandLength = 4000;
        private const int MaxPathLength = 400;
        private const int DefaultTimeoutSeconds = 60;
        private const int MaxTimeoutSeconds = 600;
        private const int MaxOutputBytes = 1024 * 1024;

        public string Name
        {
            get { return ToolNames.RunCommand; }
        }

        public string Description
        {
            get
            {
                return "在工作区内执行命令。参数：command（必填）、shell（powershell 或 cmd，默认 powershell）、"
                    + "cwd（相对工作区根的目录，默认根目录）、timeoutSeconds（默认 60，最大 600）。"
                    + "输出上限 1 MB；命令始终需要审批，取消或超时会终止整个进程树。";
            }
        }

        public ToolRisk Risk
        {
            get { return ToolRisk.High; }
        }

        public bool RequiresApproval
        {
            get { return true; }
        }

        public ToolRiskAssessment AssessRisk(ToolCall call, ToolContext context)
        {
            string command;
            if (!TryReadCommand(call, out command))
            {
                return new ToolRiskAssessment(ToolRisk.High, "命令参数无法解析，按高风险处理。");
            }

            CommandRiskAssessment assessment = CommandRiskClassifier.Classify(command);
            return new ToolRiskAssessment(assessment.Risk, assessment.Reason);
        }

        public string GetAuditArguments(ToolCall call, ToolContext context)
        {
            try
            {
                ToolArguments arguments = ToolArguments.Parse(call.ArgumentsJson);
                string command = arguments.GetRequiredString("command", MaxCommandLength);
                string shell = arguments.GetOptionalString("shell", "powershell", 40);
                return "command=" + command + "; shell=" + shell;
            }
            catch (ToolArgumentException)
            {
                return "（参数无法解析）";
            }
        }

        public ToolApprovalPreview Describe(ToolCall call, ToolContext context)
        {
            CommandPlan plan = Prepare(call, context);

            return new ToolApprovalPreview
            {
                Title = "执行命令",
                Target = plan.Command,
                Summary = "Shell："
                    + ShellDisplayName(plan.Shell)
                    + " · 目录："
                    + plan.WorkingDirectoryDisplay
                    + " · 超时："
                    + plan.TimeoutSeconds.ToString(CultureInfo.InvariantCulture)
                    + " 秒 · 输出上限 1 MB · 命令不可撤销",
                CanApproveForSession = false,
                CanShowDiff = false,
                ScopeKey = null,
                StateToken = null
            };
        }

        public async Task<ToolResult> ExecuteAsync(
            ToolCall call,
            ToolContext context,
            CancellationToken cancellationToken)
        {
            CommandPlan plan;
            try
            {
                plan = Prepare(call, context);
            }
            catch (ToolArgumentException exception)
            {
                return ToolResultFactory.InvalidArguments(call, exception);
            }
            catch (ToolPreparationException exception)
            {
                return ToolResult.Failure(call, exception.Status, exception.Code, exception.Message);
            }

            cancellationToken.ThrowIfCancellationRequested();

            if (context.ApprovalPreview == null)
            {
                return ToolResult.Failure(
                    call,
                    ToolResultStatus.Failed,
                    "approval_state_missing",
                    "命令必须经过审批后才能执行。");
            }

            string scriptPath = null;
            Process process = null;
            try
            {
                scriptPath = CreateScriptFile(plan);
                process = StartProcess(plan, scriptPath);
            }
            catch (Exception exception) when (
                exception is IOException
                || exception is UnauthorizedAccessException
                || exception is InvalidOperationException
                || exception is System.ComponentModel.Win32Exception)
            {
                DeleteQuietly(scriptPath);
                return ToolResult.Failure(
                    call,
                    ToolResultStatus.Failed,
                    "launch_failed",
                    "无法启动命令进程：" + Shorten(exception.Message, 160));
            }

            var stopwatch = Stopwatch.StartNew();
            var budget = new OutputBudget(MaxOutputBytes);
            var stdout = new MemoryStream();
            var stderr = new MemoryStream();

            Task stdoutTask = CaptureAsync(process.StandardOutput.BaseStream, stdout, budget);
            Task stderrTask = CaptureAsync(process.StandardError.BaseStream, stderr, budget);

            ExitWaitResult wait = await WaitForExitAsync(
                process,
                plan.TimeoutSeconds,
                cancellationToken).ConfigureAwait(false);

            if (wait == ExitWaitResult.TimedOut || wait == ExitWaitResult.Cancelled)
            {
                ProcessTreeKiller.Kill(process);
            }

            // 进程退出/被杀后管道会关闭；最多再等 2 秒收尾，避免卡住会话循环。
            await WaitForReadersAsync(stdoutTask, stderrTask).ConfigureAwait(false);

            stopwatch.Stop();
            int exitCode = TryGetExitCode(process);
            DeleteQuietly(scriptPath);
            scriptPath = null;
            DisposeQuietly(process);
            process = null;

            string output = FormatOutput(
                plan,
                stdout.ToArray(),
                stderr.ToArray(),
                exitCode,
                stopwatch.ElapsedMilliseconds,
                wait,
                budget);

            bool truncated = budget.Truncated;

            switch (wait)
            {
                case ExitWaitResult.TimedOut:
                    return FailureWithOutput(
                        call,
                        ToolResultStatus.Failed,
                        "command_timeout",
                        "命令超过 " + plan.TimeoutSeconds.ToString(CultureInfo.InvariantCulture)
                            + " 秒，进程树已终止。",
                        output,
                        truncated);

                case ExitWaitResult.Cancelled:
                    return FailureWithOutput(
                        call,
                        ToolResultStatus.Cancelled,
                        "cancelled",
                        "命令已取消，进程树已终止。",
                        output,
                        truncated);

                case ExitWaitResult.StartFailed:
                    return FailureWithOutput(
                        call,
                        ToolResultStatus.Failed,
                        "launch_failed",
                        "命令进程没有正常启动。",
                        output,
                        truncated);
            }

            if (exitCode != 0)
            {
                ToolResult failed = FailureWithOutput(
                    call,
                    ToolResultStatus.Failed,
                    "nonzero_exit",
                    "命令退出码 " + exitCode.ToString(CultureInfo.InvariantCulture) + "。",
                    output,
                    truncated);
                failed.Summary = BuildSummary(plan, exitCode);
                return failed;
            }

            var result = ToolResult.Success(
                call,
                output,
                BuildSummary(plan, exitCode),
                true,
                truncated);
            return result;
        }

        private static ToolResult FailureWithOutput(
            ToolCall call,
            ToolResultStatus status,
            string code,
            string message,
            string output,
            bool truncated)
        {
            return new ToolResult
            {
                CallId = call == null ? null : call.Id,
                ToolName = call == null ? null : call.Name,
                Status = status,
                Content = output,
                ErrorCode = code,
                ErrorMessage = message,
                IsUntrusted = true,
                Truncated = truncated
            };
        }

        private static CommandPlan Prepare(ToolCall call, ToolContext context)
        {
            ToolArguments arguments;
            string command;
            string shell;
            string cwd;
            int timeoutSeconds;
            try
            {
                arguments = ToolArguments.Parse(call.ArgumentsJson);
                command = arguments.GetRequiredString("command", MaxCommandLength);
                shell = arguments.GetOptionalString("shell", "powershell", 40);
                cwd = arguments.GetOptionalString("cwd", ".", MaxPathLength);
                timeoutSeconds = arguments.GetOptionalInt(
                    "timeoutSeconds",
                    DefaultTimeoutSeconds,
                    1,
                    MaxTimeoutSeconds);
            }
            catch (ToolArgumentException exception)
            {
                throw new ToolPreparationException(
                    exception.Code,
                    exception.Message,
                    ToolResultStatus.InvalidArguments);
            }

            string normalizedShell = (shell ?? string.Empty).Trim().ToLowerInvariant();
            if (normalizedShell == "pwsh")
            {
                normalizedShell = "powershell";
            }

            if (normalizedShell != "powershell" && normalizedShell != "cmd")
            {
                throw new ToolPreparationException(
                    "invalid_shell",
                    "shell 只支持 powershell 或 cmd。",
                    ToolResultStatus.InvalidArguments);
            }

            PathSandboxResult resolved = context.Sandbox.Resolve(
                cwd,
                PathAccessKind.Directory);
            if (!resolved.IsAllowed)
            {
                throw new ToolPreparationException(
                    ToolResultFactory.ToErrorCode(resolved.Status),
                    resolved.Detail);
            }

            string relative = context.Sandbox.ToRelativePath(resolved.NormalizedPath);
            return new CommandPlan
            {
                Command = command,
                Shell = normalizedShell,
                WorkingDirectory = resolved.NormalizedPath,
                WorkingDirectoryDisplay = ToolText.ToDisplayPath(relative),
                TimeoutSeconds = timeoutSeconds
            };
        }

        private static string CreateScriptFile(CommandPlan plan)
        {
            string extension = plan.Shell == "powershell" ? ".ps1" : ".cmd";
            string path = Path.Combine(
                Path.GetTempPath(),
                "miaowu-command-" + Guid.NewGuid().ToString("N") + extension);

            if (plan.Shell == "powershell")
            {
                // Windows PowerShell 5.1 只有看到 UTF-8 BOM 才按 UTF-8 解析脚本。
                File.WriteAllText(path, plan.Command, new UTF8Encoding(true));
            }
            else
            {
                Encoding encoding = GetOemEncoding();
                File.WriteAllBytes(path, encoding.GetBytes(plan.Command + "\r\n"));
            }

            return path;
        }

        private static Process StartProcess(CommandPlan plan, string scriptPath)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = plan.Shell == "powershell" ? "powershell.exe" : "cmd.exe",
                Arguments = plan.Shell == "powershell"
                    ? "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"" + scriptPath + "\""
                    : "/d /s /c \"\"" + scriptPath + "\"\"",
                WorkingDirectory = plan.WorkingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true
            };

            var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            if (!process.Start())
            {
                throw new InvalidOperationException("命令进程没有启动。");
            }

            try
            {
                // 脚本文件不需要输入；关掉 stdin，避免命令等待输入导致挂起。
                process.StandardInput.Close();
            }
            catch (Exception exception) when (
                exception is IOException
                || exception is InvalidOperationException
                || exception is ObjectDisposedException)
            {
            }

            return process;
        }

        private static async Task<ExitWaitResult> WaitForExitAsync(
            Process process,
            int timeoutSeconds,
            CancellationToken cancellationToken)
        {
            var exitSource = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            try
            {
                process.Exited += (sender, args) => exitSource.TrySetResult(true);
                if (process.HasExited)
                {
                    exitSource.TrySetResult(true);
                }

                Task timeoutTask = Task.Delay(TimeSpan.FromSeconds(timeoutSeconds));
                Task cancelTask = cancellationToken.CanBeCanceled
                    ? Task.Delay(Timeout.Infinite, cancellationToken)
                    : new TaskCompletionSource<bool>().Task;

                Task completed = await Task.WhenAny(
                    exitSource.Task,
                    timeoutTask,
                    cancelTask).ConfigureAwait(false);

                if (completed == exitSource.Task)
                {
                    return ExitWaitResult.Exited;
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    return ExitWaitResult.Cancelled;
                }

                return ExitWaitResult.TimedOut;
            }
            catch (InvalidOperationException)
            {
                return ExitWaitResult.StartFailed;
            }
        }

        private static async Task CaptureAsync(Stream stream, MemoryStream sink, OutputBudget budget)
        {
            var buffer = new byte[8192];
            try
            {
                while (true)
                {
                    int read = await stream.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
                    if (read <= 0)
                    {
                        break;
                    }

                    int take = budget.Reserve(read);
                    if (take > 0)
                    {
                        sink.Write(buffer, 0, take);
                    }
                }
            }
            catch (Exception exception) when (
                exception is IOException
                || exception is ObjectDisposedException
                || exception is InvalidOperationException)
            {
                // 进程被杀后管道关闭是正常路径。
            }
        }

        private static async Task WaitForReadersAsync(Task stdoutTask, Task stderrTask)
        {
            Task all = Task.WhenAll(stdoutTask, stderrTask);
            await Task.WhenAny(all, Task.Delay(2000)).ConfigureAwait(false);
        }

        private static int TryGetExitCode(Process process)
        {
            try
            {
                if (process.HasExited)
                {
                    return process.ExitCode;
                }
            }
            catch (InvalidOperationException)
            {
            }

            return -1;
        }

        private static string FormatOutput(
            CommandPlan plan,
            byte[] stdoutBytes,
            byte[] stderrBytes,
            int exitCode,
            long elapsedMilliseconds,
            ExitWaitResult wait,
            OutputBudget budget)
        {
            int oemCodePage = GetOemCodePage();
            string stdoutText = ConsoleOutputDecoder.Decode(stdoutBytes, oemCodePage);
            string stderrText = ConsoleOutputDecoder.Decode(stderrBytes, oemCodePage);

            var builder = new StringBuilder();
            builder.Append("命令：").Append(plan.Command).Append('\n');
            builder.Append("Shell：").Append(ShellDisplayName(plan.Shell))
                .Append(" · 目录：").Append(plan.WorkingDirectoryDisplay)
                .Append(" · 耗时：")
                .Append((elapsedMilliseconds / 1000.0).ToString("F1", CultureInfo.InvariantCulture))
                .Append("s · 退出码：");

            if (wait == ExitWaitResult.TimedOut)
            {
                builder.Append("超时");
            }
            else if (wait == ExitWaitResult.Cancelled)
            {
                builder.Append("已取消");
            }
            else
            {
                builder.Append(exitCode.ToString(CultureInfo.InvariantCulture));
            }

            builder.Append('\n');

            if (stdoutText.Length > 0)
            {
                builder.Append("[stdout]\n").Append(stdoutText);
                if (!stdoutText.EndsWith("\n", StringComparison.Ordinal))
                {
                    builder.Append('\n');
                }
            }

            if (stderrText.Length > 0)
            {
                builder.Append("[stderr]\n").Append(stderrText);
                if (!stderrText.EndsWith("\n", StringComparison.Ordinal))
                {
                    builder.Append('\n');
                }
            }

            if (stdoutText.Length == 0 && stderrText.Length == 0)
            {
                builder.Append("（没有输出）\n");
            }

            if (budget.Truncated)
            {
                builder.Append("（输出超过 1 MB，已截断）\n");
            }

            return builder.ToString();
        }

        private static string BuildSummary(CommandPlan plan, int exitCode)
        {
            return ShellDisplayName(plan.Shell)
                + " · " + plan.WorkingDirectoryDisplay
                + " · 退出码 " + exitCode.ToString(CultureInfo.InvariantCulture);
        }

        private static string ShellDisplayName(string shell)
        {
            return shell == "cmd" ? "cmd" : "PowerShell";
        }

        private static int GetOemCodePage()
        {
            try
            {
                return CultureInfo.CurrentCulture.TextInfo.OEMCodePage;
            }
            catch (Exception)
            {
                return 936;
            }
        }

        private static Encoding GetOemEncoding()
        {
            try
            {
                return Encoding.GetEncoding(GetOemCodePage());
            }
            catch (ArgumentException)
            {
                return Encoding.Default;
            }
        }

        private static bool TryReadCommand(ToolCall call, out string command)
        {
            command = null;
            if (call == null || string.IsNullOrWhiteSpace(call.ArgumentsJson))
            {
                return false;
            }

            try
            {
                ToolArguments arguments = ToolArguments.Parse(call.ArgumentsJson);
                command = arguments.GetRequiredString("command", MaxCommandLength);
                return true;
            }
            catch (ToolArgumentException)
            {
                return false;
            }
        }

        private static void DeleteQuietly(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        private static void DisposeQuietly(Process process)
        {
            if (process == null)
            {
                return;
            }

            try
            {
                process.Dispose();
            }
            catch (Exception)
            {
            }
        }

        private static string Shorten(string text, int maxLength)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            return text.Length <= maxLength ? text : text.Substring(0, maxLength) + "…";
        }

        private enum ExitWaitResult
        {
            Exited,
            TimedOut,
            Cancelled,
            StartFailed
        }

        private sealed class CommandPlan
        {
            public string Command;
            public string Shell;
            public string WorkingDirectory;
            public string WorkingDirectoryDisplay;
            public int TimeoutSeconds;
        }

        /// <summary>stdout + stderr 共享一个字节预算；超出后继续读但丢弃，避免管道阻塞。</summary>
        private sealed class OutputBudget
        {
            private readonly object _gate = new object();
            private int _remaining;

            public OutputBudget(int totalBytes)
            {
                _remaining = totalBytes;
            }

            public bool Truncated { get; private set; }

            public int Reserve(int requested)
            {
                lock (_gate)
                {
                    if (_remaining <= 0)
                    {
                        Truncated = true;
                        return 0;
                    }

                    if (requested <= _remaining)
                    {
                        _remaining -= requested;
                        return requested;
                    }

                    int take = _remaining;
                    _remaining = 0;
                    Truncated = true;
                    return take;
                }
            }
        }
    }

    /// <summary>把控制台原始字节解码为文本：优先 BOM，否则按 OEM 代码页。</summary>
    public static class ConsoleOutputDecoder
    {
        public static string Decode(byte[] bytes, int oemCodePage)
        {
            if (bytes == null || bytes.Length == 0)
            {
                return string.Empty;
            }

            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            {
                return new UTF8Encoding(false).GetString(bytes, 3, bytes.Length - 3);
            }

            if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            {
                return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
            }

            if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            {
                return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
            }

            Encoding encoding;
            try
            {
                encoding = Encoding.GetEncoding(oemCodePage);
            }
            catch (ArgumentException)
            {
                encoding = Encoding.Default;
            }

            return encoding.GetString(bytes);
        }
    }

    /// <summary>杀掉整个进程树：优先 taskkill /T /F，失败再退到 Process.Kill。</summary>
    internal static class ProcessTreeKiller
    {
        public static void Kill(Process process)
        {
            if (process == null)
            {
                return;
            }

            try
            {
                if (process.HasExited)
                {
                    return;
                }
            }
            catch (InvalidOperationException)
            {
                return;
            }

            int pid = 0;
            try
            {
                pid = process.Id;
            }
            catch (InvalidOperationException)
            {
            }

            if (pid > 0)
            {
                try
                {
                    var startInfo = new ProcessStartInfo
                    {
                        FileName = "taskkill.exe",
                        Arguments = "/PID " + pid.ToString(CultureInfo.InvariantCulture) + " /T /F",
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };

                    using (Process killer = Process.Start(startInfo))
                    {
                        if (killer != null)
                        {
                            killer.WaitForExit(3000);
                        }
                    }
                }
                catch (Exception exception) when (
                    exception is IOException
                    || exception is System.ComponentModel.Win32Exception
                    || exception is InvalidOperationException)
                {
                    // 继续走 Process.Kill 兜底。
                }
            }

            try
            {
                if (!process.HasExited)
                {
                    process.Kill();
                }
            }
            catch (InvalidOperationException)
            {
            }
            catch (System.ComponentModel.Win32Exception)
            {
            }

            try
            {
                process.WaitForExit(3000);
            }
            catch (InvalidOperationException)
            {
            }
        }
    }
}
