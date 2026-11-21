using System;
using System.Threading;
using System.Threading.Tasks;
using MiaoWu.Core.Agents.Tools;
using MiaoWu.Infrastructure.Tools;
using Xunit;

namespace MiaoWu.Tests.Tools
{
    /// <summary>job_output 的 waitMs：等新输出、任务结束时提前返回、越界参数直接拒绝。</summary>
    public sealed class JobToolsTests : IDisposable
    {
        private readonly BackgroundJobRegistry _jobs = new BackgroundJobRegistry();

        public void Dispose()
        {
            _jobs.Dispose();
        }

        [Fact]
        public async Task JobOutputWaitsForLateOutput()
        {
            BackgroundJobRegistry.BackgroundJob job = _jobs.Start(
                "s1",
                "Start-Sleep -Milliseconds 300; Write-Output 'tool-late'",
                Environment.CurrentDirectory,
                "延迟输出");

            var tool = new JobOutputTool(_jobs);
            ToolResult result = await tool.ExecuteAsync(
                Call("{\"id\":\"" + job.Id + "\",\"waitMs\":8000}"),
                new ToolContext(),
                CancellationToken.None);

            Assert.Equal(ToolResultStatus.Succeeded, result.Status);
            Assert.Contains("tool-late", result.Content);
        }

        [Fact]
        public async Task JobOutputRejectsNonNumericWait()
        {
            BackgroundJobRegistry.BackgroundJob job = _jobs.Start(
                "s1",
                "Start-Sleep -Seconds 5",
                Environment.CurrentDirectory,
                "长任务");

            try
            {
                var tool = new JobOutputTool(_jobs);
                ToolResult result = await tool.ExecuteAsync(
                    Call("{\"id\":\"" + job.Id + "\",\"waitMs\":\"soon\"}"),
                    new ToolContext(),
                    CancellationToken.None);

                Assert.Equal(ToolResultStatus.InvalidArguments, result.Status);
            }
            finally
            {
                job.Kill();
            }
        }

        private static ToolCall Call(string argumentsJson)
        {
            return new ToolCall
            {
                Id = "call_job_output",
                Name = ToolNames.JobOutput,
                ArgumentsJson = argumentsJson
            };
        }
    }
}
