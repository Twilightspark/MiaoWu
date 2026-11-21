using System;
using System.Threading;
using System.Threading.Tasks;
using MiaoWu.Core.Agents.Tools;
using MiaoWu.Infrastructure.Tools;
using Xunit;

namespace MiaoWu.Tests.Tools
{
    public sealed class BackgroundJobRegistryTests : IDisposable
    {
        private readonly BackgroundJobRegistry _jobs = new BackgroundJobRegistry();

        public void Dispose()
        {
            _jobs.Dispose();
        }

        [Fact]
        public async Task RunsCommandAndReadsOutput()
        {
            BackgroundJobRegistry.BackgroundJob job = _jobs.Start(
                "s1",
                "Write-Output 'hello-job'",
                Environment.CurrentDirectory,
                "输出测试");

            string output = await ReadUntilAsync(job, "hello-job", TimeSpan.FromSeconds(10));

            Assert.Contains("hello-job", output);
        }

        [Fact]
        public void ListIsScopedToSession()
        {
            BackgroundJobRegistry.BackgroundJob mine = _jobs.Start(
                "s1",
                "Write-Output 'a'",
                Environment.CurrentDirectory,
                "s1");

            _jobs.Start("s2", "Write-Output 'b'", Environment.CurrentDirectory, "s2");

            var list = _jobs.List("s1");
            Assert.Single(list);
            Assert.Equal(mine.Id, list[0].Id);
            Assert.Equal(2, _jobs.List(null).Count);
        }

        [Fact]
        public async Task KillStopsLongRunningJob()
        {
            BackgroundJobRegistry.BackgroundJob job = _jobs.Start(
                "s1",
                "Start-Sleep -Seconds 30",
                Environment.CurrentDirectory,
                "长任务");

            job.Kill();
            await Task.Delay(600);

            BackgroundJobInfo info = job.Snapshot();
            Assert.False(info.Running);
            Assert.True(info.Killed);
        }

        [Fact]
        public void UnknownJobLookupReturnsNull()
        {
            Assert.Null(_jobs.Find("job_missing"));
        }

        [Fact]
        public async Task WaitForReadReturnsLateOutput()
        {
            BackgroundJobRegistry.BackgroundJob job = _jobs.Start(
                "s1",
                "Start-Sleep -Milliseconds 400; Write-Output 'late-line'",
                Environment.CurrentDirectory,
                "延迟输出");

            string output = await job.WaitForReadAsync(8000, CancellationToken.None);

            Assert.Contains("late-line", output);
        }

        [Fact]
        public async Task WaitForReadReturnsWhenJobFinishesWithoutOutput()
        {
            BackgroundJobRegistry.BackgroundJob job = _jobs.Start(
                "s1",
                "Start-Sleep -Milliseconds 200",
                Environment.CurrentDirectory,
                "无输出任务");

            string output = await job.WaitForReadAsync(8000, CancellationToken.None);

            Assert.Contains("没有更多输出", output);
        }

        [Fact]
        public async Task WaitForReadTimesOutOnRunningJob()
        {
            BackgroundJobRegistry.BackgroundJob job = _jobs.Start(
                "s1",
                "Start-Sleep -Seconds 30",
                Environment.CurrentDirectory,
                "静默长任务");

            try
            {
                DateTime started = DateTime.UtcNow;
                string output = await job.WaitForReadAsync(250, CancellationToken.None);
                TimeSpan elapsed = DateTime.UtcNow - started;

                Assert.Contains("还没有新输出", output);
                Assert.True(elapsed >= TimeSpan.FromMilliseconds(150), "waitMs 还没到就返回了：" + elapsed);
                Assert.True(elapsed < TimeSpan.FromSeconds(10), "waitMs 超时太久：" + elapsed);
            }
            finally
            {
                job.Kill();
            }
        }

        private static async Task<string> ReadUntilAsync(
            BackgroundJobRegistry.BackgroundJob job,
            string marker,
            TimeSpan timeout)
        {
            DateTime deadline = DateTime.UtcNow + timeout;
            string output = string.Empty;
            while (DateTime.UtcNow < deadline)
            {
                output = job.Read();
                if (output.IndexOf(marker, StringComparison.Ordinal) >= 0)
                {
                    return output;
                }

                if (!job.Snapshot().Running)
                {
                    break;
                }

                await Task.Delay(100);
            }

            // 挂钟到了也再读一次，收集退出前的最后输出。
            output = job.Read();
            return output;
        }
    }
}
