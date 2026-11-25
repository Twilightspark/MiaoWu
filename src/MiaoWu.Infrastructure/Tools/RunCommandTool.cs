using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MiaoWu.Core.Agents.Tools;
using MiaoWu.Core.Text;

namespace MiaoWu.Infrastructure.Tools
{
    /// <summary>
    /// pwsh：在工作区内执行 Windows PowerShell 命令（不加载 profile、不提权）。
    /// 命令始终需要审批，默认 120 秒超时（上限 10 分钟），取消/超时会杀掉整个进程树。
    /// </summary>
    public sealed class PwshTool : IAgentTool, IToolRiskEvaluator, IToolAuditRedactor
    {
        private const int MaxCommandLength = 4000;
        private const int MaxDescriptionLength = 400;
        private const int MaxPathLength = 400;
        private const int DefaultTimeoutMs = 120000;
        private const int MaxTimeoutMs = 600000;
        private const int MaxOutputBytes = 1024 * 1024;

        // 运行期统一 UTF-8：把控制台输出代码页切到 65001，子进程与重定向字节都按 UTF-8 生成。
        // 与 DSH 的 ENCODING_PREAMBLE 等价；DSH 用 [System.Text.UTF8Encoding]::new($false)，
        // 这里用 New-Object 以保持 Windows 7 自带 PowerShell 2.0 也能执行。
        private const string Utf8Prelude =
            "$ProgressPreference='SilentlyContinue'; "
            + "[Console]::OutputEncoding=New-Object System.Text.UTF8Encoding -ArgumentList $false; "
            + "$OutputEncoding=New-Object System.Text.UTF8Encoding -ArgumentList $false; ";

        private readonly BackgroundJobRegistry _jobs;

        public PwshTool(BackgroundJobRegistry jobs = null)
        {
            _jobs = jobs;
        }

        public string Name
        {
            get { return ToolNames.Pwsh; }
        }

        public string Description
        {
            get
            {
                return "在工作区内执行 PowerShell 命令（Windows PowerShell，无 profile）。参数：command（必填）、"
                    + "description（可选，一句话说明这条命令做什么）、timeoutMs（默认 120000，最大 600000）、"
                    + "workdir（相对工作区根或会话工作目录）、run_in_background（后台执行，返回 job id）。"
                    + "输出上限 1 MB；命令始终需要审批，取消或超时会终止整个进程树；"
                    + "非零退出码会以 [exit code: N] 结尾。";
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
                string description = arguments.GetOptionalString("description", string.Empty, MaxDescriptionLength);
                string workdir = arguments.GetOptionalString("workdir", ".", MaxPathLength);
                return "command=" + command
                    + "; description=" + description
                    + "; workdir=" + workdir;
            }
            catch (ToolArgumentException)
            {
                return "（参数无法解析）";
            }
        }

        public ToolApprovalPreview Describe(ToolCall call, ToolContext context)
        {
            CommandPlan plan = Prepare(call, context);

            string summary = string.IsNullOrWhiteSpace(plan.DescriptionText)
                ? "PowerShell · 目录：" + plan.WorkingDirectoryDisplay
                : plan.DescriptionText + " · PowerShell · 目录：" + plan.WorkingDirectoryDisplay;

            return new ToolApprovalPreview
            {
                Title = "执行命令",
                Target = plan.Command,
                Summary = summary
                    + " · 超时：" + (plan.TimeoutMs / 1000).ToString(CultureInfo.InvariantCulture)
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

            if (plan.Background)
            {
                if (_jobs == null)
                {
                    return ToolResult.Failure(
                        call,
                        ToolResultStatus.Failed,
                        "background_jobs_unavailable",
                        "当前组合没有后台任务注册表，不能后台执行。");
                }

                try
                {
                    BackgroundJobRegistry.BackgroundJob job = _jobs.Start(
                        context.SessionId,
                        plan.Command,
                        plan.WorkingDirectory,
                        plan.DescriptionText);
                    return ToolResult.Success(
                        call,
                        "started background job " + job.Id,
                        "后台任务 " + job.Id,
                        true,
                        false);
                }
                catch (Exception exception)
                {
                    return ToolResult.Failure(
                        call,
                        ToolResultStatus.Failed,
                        "launch_failed",
                        "无法启动后台任务：" + Shorten(exception.Message, 160));
                }
            }

            Process process;
            try
            {
                process = StartProcess(plan);
            }
            catch (Exception exception) when (
                exception is IOException
                || exception is UnauthorizedAccessException
                || exception is InvalidOperationException
                || exception is System.ComponentModel.Win32Exception)
            {
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
                plan.TimeoutMs,
                cancellationToken).ConfigureAwait(false);

            if (wait == ExitWaitResult.TimedOut || wait == ExitWaitResult.Cancelled)
            {
                ProcessTreeKiller.Kill(process);
            }

            // 进程退出/被杀后管道会关闭；最多再等 2 秒收尾，避免卡住会话循环。
            await WaitForReadersAsync(stdoutTask, stderrTask).ConfigureAwait(false);

            stopwatch.Stop();
            int exitCode = TryGetExitCode(process);
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
                        "命令超过 " + plan.TimeoutMs.ToString(CultureInfo.InvariantCulture)
                            + " 毫秒，进程树已终止。",
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

            return ToolResult.Success(
                call,
                output,
                BuildSummary(plan, exitCode),
                true,
                truncated);
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

        /// <summary>没有显式 workdir 时用会话工作目录；没有会话上下文时退回工作区根（"."）。</summary>
        private static string DefaultWorkdir(ToolContext context)
        {
            string current = context == null || context.WorkingDirectory == null
                ? null
                : context.WorkingDirectory.Current;
            return string.IsNullOrWhiteSpace(current) ? "." : current;
        }

        private static CommandPlan Prepare(ToolCall call, ToolContext context)
        {
            ToolArguments arguments;
            string command;
            string description;
            string workdir;
            int timeoutMs;
            bool background;
            try
            {
                arguments = ToolArguments.Parse(call.ArgumentsJson);
                command = arguments.GetRequiredString("command", MaxCommandLength);
                description = arguments.GetOptionalString(
                    "description",
                    string.Empty,
                    MaxDescriptionLength);
                workdir = arguments.GetOptionalString("workdir", DefaultWorkdir(context), MaxPathLength);
                timeoutMs = arguments.GetOptionalInt(
                    "timeoutMs",
                    DefaultTimeoutMs,
                    1,
                    MaxTimeoutMs);
                background = arguments.GetOptionalBool("run_in_background", false);
            }
            catch (ToolArgumentException exception)
            {
                throw new ToolPreparationException(
                    exception.Code,
                    exception.Message,
                    ToolResultStatus.InvalidArguments);
            }

            PathSandboxResult resolved = context.Sandbox.Resolve(
                workdir,
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
                DescriptionText = description == null ? string.Empty : description.Trim(),
                WorkingDirectory = resolved.NormalizedPath,
                WorkingDirectoryDisplay = ToolText.ToDisplayPath(relative),
                TimeoutMs = timeoutMs,
                Background = background
            };
        }

        private static Process StartProcess(CommandPlan plan)
        {
            var startInfo = new ProcessStartInfo
            {
                // 固定 Windows PowerShell：工具名沿用 DSH 的 pwsh，但不做 shell 方言切换。
                FileName = "powershell.exe",
                Arguments = "-NoLogo -NoProfile -NonInteractive -Command "
                    + QuoteCommandLineArgument(Utf8Prelude + plan.Command),
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
                // 命令不等待输入；关掉 stdin，避免命令等待输入导致挂起。
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

        /// <summary>
        /// 把命令文本包成一个 CreateProcess 参数：引号与反斜杠按 CommandLineToArgvW 的规则转义，
        /// 让 powershell.exe 收到的 -Command 值就是原文。
        /// </summary>
        private static string QuoteCommandLineArgument(string argument)
        {
            var builder = new StringBuilder(argument.Length + 8);
            builder.Append('"');

            int backslashes = 0;
            for (int i = 0; i < argument.Length; i++)
            {
                char current = argument[i];
                if (current == '\\')
                {
                    backslashes++;
                    continue;
                }

                if (current == '"')
                {
                    builder.Append('\\', backslashes * 2 + 1);
                    builder.Append('"');
                    backslashes = 0;
                    continue;
                }

                if (backslashes > 0)
                {
                    builder.Append('\\', backslashes);
                    backslashes = 0;
                }

                builder.Append(current);
            }

            if (backslashes > 0)
            {
                builder.Append('\\', backslashes * 2);
            }

            builder.Append('"');
            return builder.ToString();
        }

        private static async Task<ExitWaitResult> WaitForExitAsync(
            Process process,
            int timeoutMs,
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

                Task timeoutTask = Task.Delay(timeoutMs);
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
            string stdoutText = ConsoleOutputDecoder.Decode(stdoutBytes);
            string stderrText = ConsoleOutputDecoder.Decode(stderrBytes);

            var builder = new StringBuilder();
            builder.Append("命令：").Append(plan.Command).Append('\n');
            builder.Append("PowerShell · 目录：").Append(plan.WorkingDirectoryDisplay)
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

            if (wait == ExitWaitResult.Exited && exitCode != 0)
            {
                builder.Append("[exit code: ")
                    .Append(exitCode.ToString(CultureInfo.InvariantCulture))
                    .Append("]\n");
            }

            return builder.ToString();
        }

        private static string BuildSummary(CommandPlan plan, int exitCode)
        {
            return "PowerShell · " + plan.WorkingDirectoryDisplay
                + " · 退出码 " + exitCode.ToString(CultureInfo.InvariantCulture);
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
            public string DescriptionText;
            public string WorkingDirectory;
            public string WorkingDirectoryDisplay;
            public int TimeoutMs;
            public bool Background;
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

    /// <summary>
    /// 把控制台原始字节解码为文本：与 DSH 的 <c>subprocess-local</c> 一致，
    /// 一律按 UTF-8（非严格，坏字节 → U+FFFD），不做 BOM / UTF-16 / OEM 猜测。
    /// PowerShell 启动前已把输出代码页固定为无 BOM UTF-8。
    /// </summary>
    public static class ConsoleOutputDecoder
    {
        public static string Decode(byte[] bytes)
        {
            return bytes == null || bytes.Length == 0
                ? string.Empty
                : TextEncodings.Utf8.GetString(bytes);
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
