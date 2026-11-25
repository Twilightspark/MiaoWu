using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using MiaoWu.Core.Agents.Tools;
using MiaoWu.Infrastructure.Json;
using MiaoWu.Infrastructure.Tools;
using Xunit;

namespace MiaoWu.Tests.Tools
{
    public sealed class PwshToolTests : IDisposable
    {
        private readonly string _workspace;
        private readonly PwshTool _tool = new PwshTool();
        private readonly ToolContext _context;

        public PwshToolTests()
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
        public void CommandWithDoubleQuotesRuns()
        {
            ToolResult result = Run("Write-Output \"hello world\"");

            Assert.True(result.IsSuccess, result.Content);
            Assert.Contains("hello world", result.Content);
        }

        [Fact]
        public void NonZeroExitCodeIsReportedWithMarker()
        {
            ToolResult result = Run("exit 3");

            Assert.Equal(ToolResultStatus.Failed, result.Status);
            Assert.Equal("nonzero_exit", result.ErrorCode);
            Assert.Contains("退出码：3", result.Content);
            Assert.Contains("[exit code: 3]", result.Content);
            Assert.EndsWith("[exit code: 3]\n", result.Content.Replace("\r\n", "\n"));
            Assert.False(result.Truncated);
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

            ToolResult result = Run("Write-Output (Get-Location).Path", "sub");

            Assert.True(result.IsSuccess, result.Content);
            Assert.Contains(sub, result.Content);
        }

        [Fact]
        public void WorkingDirectoryOutsideWorkspaceIsRejected()
        {
            ToolResult result = Run("Write-Output 1", @"..\..\Windows");

            Assert.Equal(ToolResultStatus.Failed, result.Status);
            Assert.Equal("path_outside_workspace", result.ErrorCode);
        }

        [Fact]
        public void MissingCommandIsRejected()
        {
            ToolResult result = _tool.ExecuteAsync(
                new ToolCall { Id = "c1", Name = "pwsh", ArgumentsJson = "{}" },
                _context,
                CancellationToken.None).GetAwaiter().GetResult();

            Assert.Equal(ToolResultStatus.InvalidArguments, result.Status);
            Assert.Equal("missing_argument", result.ErrorCode);
        }

        [Fact]
        public void DescriptionIsShownInPreview()
        {
            ToolCall call = Call("Write-Output 1", ".", 120000, "验证描述进入审批");
            ToolApprovalPreview preview = _tool.Describe(call, _context);

            Assert.NotNull(preview);
            Assert.Equal("执行命令", preview.Title);
            Assert.Contains("验证描述进入审批", preview.Summary);
            Assert.Contains("PowerShell", preview.Summary);
            Assert.False(preview.CanApproveForSession);
        }

        [Fact]
        public void BlockedCommandIsClassified()
        {
            ToolRiskAssessment assessment = _tool.AssessRisk(
                Call("format C:"),
                _context);

            Assert.Equal(ToolRisk.Blocked, assessment.Risk);
        }

        [Fact]
        public void TimeoutKillsProcess()
        {
            var stopwatch = Stopwatch.StartNew();
            ToolResult result = Run("Start-Sleep -Seconds 10", ".", 1000);
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

            ToolCall call = Call("Start-Sleep -Seconds 10");
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
            ToolResult result = Run("[Console]::Write(('x' * 1200000))", ".", 30000);

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
        public void ChineseOutputIsDecodedAsUtf8()
        {
            ToolResult result = Run("Write-Output '中文'");

            Assert.True(result.IsSuccess, result.Content);
            Assert.Contains("中文", result.Content);
            Assert.True(result.Content.IndexOf('\uFEFF') < 0, "输出不应带 BOM");
            Assert.True(result.Content.IndexOf('\uFFFD') < 0, "输出不应有替换字符");
        }

        [Fact]
        public void NativeCommandChineseOutputIsUtf8()
        {
            ToolResult result = Run("cmd /c echo 中文");

            Assert.True(result.IsSuccess, result.Content);
            Assert.Contains("中文", result.Content);
            Assert.True(result.Content.IndexOf('\uFEFF') < 0, "输出不应带 BOM");
            Assert.True(result.Content.IndexOf('\uFFFD') < 0, "输出不应有替换字符");
        }

        [Fact]
        public void DecoderKeepsBomAsText()
        {
            // 与 DSH 的 Buffer.toString('utf8') 一致：解码器不做 BOM 剥离；
            // PowerShell 启动前已经固定为无 BOM 输出，所以正常命令不会出现这个字符。
            byte[] bytes = new byte[] { 0xEF, 0xBB, 0xBF, (byte)'h', (byte)'i' };

            Assert.Equal("\uFEFFhi", ConsoleOutputDecoder.Decode(bytes));
        }

        [Fact]
        public void DecoderReplacesInvalidUtf8Bytes()
        {
            Assert.Contains("\uFFFD", ConsoleOutputDecoder.Decode(new byte[] { 0xD6, 0xD0 }));
        }

        [Fact]
        public void DecoderUsesUtf8WithoutBom()
        {
            byte[] bytes = Encoding.UTF8.GetBytes("中文");

            Assert.Equal("中文", ConsoleOutputDecoder.Decode(bytes));
        }

        private ToolResult Run(
            string command,
            string workdir = ".",
            int timeoutMs = 120000,
            string description = null)
        {
            ToolCall call = Call(command, workdir, timeoutMs, description);
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
            string workdir = ".",
            int timeoutMs = 120000,
            string description = null)
        {
            var args = JsonValue.NewObject();
            args.Add("command", JsonValue.From(command));
            args.Add("workdir", JsonValue.From(workdir));
            args.Add("timeoutMs", JsonValue.From((double)timeoutMs));
            if (description != null)
            {
                args.Add("description", JsonValue.From(description));
            }

            return new ToolCall
            {
                Id = "call-1",
                Name = "pwsh",
                ArgumentsJson = JsonWriter.Write(args)
            };
        }
    }
}
