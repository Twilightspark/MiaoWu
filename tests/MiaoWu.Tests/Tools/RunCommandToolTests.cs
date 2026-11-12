using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using MiaoWu.Core.Agents.Tools;
using MiaoWu.Infrastructure.Json;
using MiaoWu.Infrastructure.Tools;
using Xunit;

namespace MiaoWu.Tests.Tools
{
    public sealed class RunCommandToolTests : IDisposable
    {
        private readonly string _workspace;
        private readonly RunCommandTool _tool = new RunCommandTool();
        private readonly ToolContext _context;

        public RunCommandToolTests()
        {
            _workspace = Path.Combine(
                Path.GetTempPath(),
                "miaowu-command-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_workspace);

            _context = new ToolContext
            {
                SessionId = "s1",
                WorkspaceId = "w1",
                WorkspaceRoot = _workspace,
                Sandbox = new WorkspacePathSandbox(_workspace, FileSystemPathProbe.Instance),
                CallId = "call-1",
                Now = DateTimeOffset.UtcNow
            };
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_workspace))
                {
                    Directory.Delete(_workspace, true);
                }
            }
            catch (IOException)
            {
            }
        }

        [Fact]
        public void PowerShellWritesToStdout()
        {
            ToolResult result = Run("Write-Output 'hello'");

            Assert.True(result.IsSuccess, result.Content);
            Assert.Contains("hello", result.Content);
            Assert.Contains("退出码：0", result.Content);
            Assert.True(result.IsUntrusted);
        }

        [Fact]
        public void CmdWritesToStdout()
        {
            ToolResult result = Run("echo hello", "cmd");

            Assert.True(result.IsSuccess, result.Content);
            Assert.Contains("hello", result.Content);
        }

        [Fact]
        public void NonZeroExitCodeIsReported()
        {
            ToolResult result = Run("exit 3");

            Assert.Equal(ToolResultStatus.Failed, result.Status);
            Assert.Equal("nonzero_exit", result.ErrorCode);
            Assert.Contains("退出码：3", result.Content);
            Assert.True(result.Truncated == false);
        }

        [Fact]
        public void StderrIsCaptured()
        {
            ToolResult result = Run("[Console]::Error.WriteLine('oops')");

            Assert.True(result.IsSuccess, result.Content);
            Assert.Contains("[stderr]", result.Content);
            Assert.Contains("oops", result.Content);
        }

        [Fact]
        public void WorkingDirectoryIsHonored()
        {
            string sub = Path.Combine(_workspace, "sub");
            Directory.CreateDirectory(sub);

            ToolResult result = Run("Write-Output (Get-Location).Path", "powershell", "sub");

            Assert.True(result.IsSuccess, result.Content);
            Assert.Contains(sub, result.Content);
        }

        [Fact]
        public void WorkingDirectoryOutsideWorkspaceIsRejected()
        {
            ToolResult result = Run("Write-Output 1", "powershell", @"..\..\Windows");

            Assert.Equal(ToolResultStatus.Failed, result.Status);
            Assert.Equal("path_outside_workspace", result.ErrorCode);
        }

        [Fact]
        public void InvalidShellIsRejected()
        {
            ToolResult result = Run("echo", "bash");

            Assert.Equal(ToolResultStatus.InvalidArguments, result.Status);
            Assert.Equal("invalid_shell", result.ErrorCode);
        }

        [Fact]
        public void MissingCommandIsRejected()
        {
            ToolResult result = _tool.ExecuteAsync(
                new ToolCall { Id = "c1", Name = "run_command", ArgumentsJson = "{}" },
                _context,
                CancellationToken.None).GetAwaiter().GetResult();

            Assert.Equal(ToolResultStatus.InvalidArguments, result.Status);
            Assert.Equal("missing_argument", result.ErrorCode);
        }

        [Fact]
        public void BlockedCommandIsClassified()
        {
            ToolRiskAssessment assessment = _tool.AssessRisk(
                Call("format C:", "powershell"),
                _context);

            Assert.Equal(ToolRisk.Blocked, assessment.Risk);
        }

        [Fact]
        public void TimeoutKillsProcess()
        {
            var stopwatch = Stopwatch.StartNew();
            ToolResult result = Run("Start-Sleep -Seconds 10", "powershell", ".", 1);
            stopwatch.Stop();

            Assert.Equal(ToolResultStatus.Failed, result.Status);
            Assert.Equal("command_timeout", result.ErrorCode);
            Assert.True(stopwatch.ElapsedMilliseconds < 8000, "超时后应立即返回");
            Assert.Contains("超时", result.Content);
        }

        [Fact]
        public void CancellationKillsProcess()
        {
            var cts = new CancellationTokenSource();
            cts.CancelAfter(300);

            ToolCall call = Call("Start-Sleep -Seconds 10", "powershell");
            _context.ApprovalPreview = _tool.Describe(call, _context);

            ToolResult result = _tool.ExecuteAsync(
                call,
                _context,
                cts.Token).GetAwaiter().GetResult();

            Assert.Equal(ToolResultStatus.Cancelled, result.Status);
            Assert.Contains("已取消", result.Content);
        }

        [Fact]
        public void OutputIsTruncatedAtOneMegabyte()
        {
            ToolResult result = Run("[Console]::Write(('x' * 1200000))", "powershell", ".", 30);

            Assert.True(result.IsSuccess, result.Content);
            Assert.True(result.Truncated);
            Assert.Contains("已截断", result.Content);
            Assert.True(result.Content.Length < 1_100_000);
        }

        [Fact]
        public void MultiLinePowerShellRuns()
        {
            ToolResult result = Run("Write-Output 'a'\nWrite-Output 'b'");

            Assert.True(result.IsSuccess, result.Content);
            Assert.Contains("a", result.Content);
            Assert.Contains("b", result.Content);
        }

        [Fact]
        public void ChineseOutputIsDecodedWithOemCodePage()
        {
            try
            {
                if (CultureInfo.CurrentCulture.TextInfo.OEMCodePage != 936)
                {
                    return;
                }
            }
            catch (Exception)
            {
                return;
            }

            ToolResult result = Run("echo 中文", "cmd");

            Assert.True(result.IsSuccess, result.Content);
            Assert.Contains("中文", result.Content);
        }

        [Fact]
        public void DecoderPrefersUtf8Bom()
        {
            byte[] bytes = new byte[] { 0xEF, 0xBB, 0xBF, (byte)'h', (byte)'i' };

            Assert.Equal("hi", ConsoleOutputDecoder.Decode(bytes, 936));
        }

        [Fact]
        public void DecoderUsesOemCodePageWithoutBom()
        {
            Encoding gbk;
            try
            {
                gbk = Encoding.GetEncoding(936);
            }
            catch (ArgumentException)
            {
                return;
            }

            byte[] bytes = gbk.GetBytes("中文");

            Assert.Equal("中文", ConsoleOutputDecoder.Decode(bytes, 936));
        }

        private ToolResult Run(
            string command,
            string shell = "powershell",
            string cwd = ".",
            int timeoutSeconds = 60)
        {
            ToolCall call = Call(command, shell, cwd, timeoutSeconds);
            try
            {
                _context.ApprovalPreview = _tool.Describe(call, _context);
            }
            catch (ToolPreparationException)
            {
                // 参数/路径错误由 Execute 转成结构化结果。
            }

            return _tool.ExecuteAsync(
                call,
                _context,
                CancellationToken.None).GetAwaiter().GetResult();
        }

        private static ToolCall Call(
            string command,
            string shell,
            string cwd = ".",
            int timeoutSeconds = 60)
        {
            var args = JsonValue.NewObject();
            args.Add("command", JsonValue.From(command));
            args.Add("shell", JsonValue.From(shell));
            args.Add("cwd", JsonValue.From(cwd));
            args.Add("timeoutSeconds", JsonValue.From((double)timeoutSeconds));

            return new ToolCall
            {
                Id = "call-1",
                Name = "run_command",
                ArgumentsJson = JsonWriter.Write(args)
            };
        }
    }
}
