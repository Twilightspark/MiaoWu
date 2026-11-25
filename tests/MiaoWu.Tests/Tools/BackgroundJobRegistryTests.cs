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
        public async Task ChineseOutputIsUtf8()
        {
            BackgroundJobRegistry.BackgroundJob job = _jobs.Start(
                "s1",
                "Write-Output '中文任务'",
                Environment.CurrentDirectory,
                "中文输出");

            string output = await ReadUntilAsync(job, "中文任务", TimeSpan.FromSeconds(10));

            Assert.Contains("中文任务", output);
            Assert.True(output.IndexOf('\uFEFF') < 0, "输出不应带 BOM");
            Assert.True(output.IndexOf('\uFFFD') < 0, "输出不应有替换字符");
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
        public async Task PeekTailReadsWithoutConsuming()
        {
            BackgroundJobRegistry.BackgroundJob job = _jobs.Start(
                "s1",
                "Write-Output 'peek-a'; Write-Output 'peek-b'",
                Environment.CurrentDirectory,
                "输出预览");

            string preview = await PeekUntilAsync(job, "peek-b", TimeSpan.FromSeconds(10));

            Assert.Contains("peek-a", preview);
            Assert.Contains("peek-b", preview);

            // 预览不移动读取位置：再读一次仍能拿到整段输出。
            string read = job.Read();
            Assert.Contains("peek-a", read);
            Assert.Contains("peek-b", read);
        }

        [Fact]
        public void PeekTailKeepsAtMostRequestedCharacters()
        {
            BackgroundJobRegistry.BackgroundJob job = _jobs.Start(
                "s1",
                "Write-Output 'abcdef'",
                Environment.CurrentDirectory,
                "截断预览");

            DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (DateTime.UtcNow < deadline && job.PeekTail(4).Length < 4)
            {
                Thread.Sleep(50);
            }

            Assert.Equal(4, job.PeekTail(4).Length);
        }

        [Fact]
        public async Task RemoveFinishedKeepsRunningJobs()
        {
            BackgroundJobRegistry.BackgroundJob finished = _jobs.Start(
                "s1",
                "Write-Output 'done'",
                Environment.CurrentDirectory,
                "短任务");
            BackgroundJobRegistry.BackgroundJob running = _jobs.Start(
                "s1",
                "Start-Sleep -Seconds 30",
                Environment.CurrentDirectory,
                "长任务");

            DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (DateTime.UtcNow < deadline && finished.Snapshot().Running)
            {
                await Task.Delay(50);
            }

            Assert.False(finished.Snapshot().Running);
            Assert.Equal(1, _jobs.RemoveFinished("s1"));

            BackgroundJobInfo remaining = Assert.Single(_jobs.List("s1"));
            Assert.Equal(running.Id, remaining.Id);
            Assert.True(remaining.Running);
            running.Kill();
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

        private static async Task<string> PeekUntilAsync(
            BackgroundJobRegistry.BackgroundJob job,
            string marker,
            TimeSpan timeout)
        {
            DateTime deadline = DateTime.UtcNow + timeout;
            string text = string.Empty;
            while (DateTime.UtcNow < deadline)
            {
                text = job.PeekTail(8000);
                if (text.IndexOf(marker, StringComparison.Ordinal) >= 0)
                {
                    return text;
                }

                if (!job.Snapshot().Running)
                {
                    break;
                }

                await Task.Delay(100);
            }

            return job.PeekTail(8000);
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
