using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MiaoWu.Core.Agents.Tools;

namespace MiaoWu.Infrastructure.Tools
{
    /// <summary>job_list：列出本会话的后台任务。</summary>
    public sealed class JobListTool : IAgentTool
    {
        private readonly BackgroundJobRegistry _jobs;

        public JobListTool(BackgroundJobRegistry jobs)
        {
            _jobs = jobs;
        }

        public string Name
        {
            get { return ToolNames.JobList; }
        }

        public string Description
        {
            get { return "列出本会话的后台任务（id、状态、命令、退出码）。"; }
        }

        public ToolRisk Risk
        {
            get { return ToolRisk.Low; }
        }

        public bool RequiresApproval
        {
            get { return false; }
        }

        public ToolApprovalPreview Describe(ToolCall call, ToolContext context)
        {
            return null;
        }

        public Task<ToolResult> ExecuteAsync(
            ToolCall call,
            ToolContext context,
            CancellationToken cancellationToken)
        {
            IReadOnlyList<BackgroundJobInfo> jobs = _jobs.List(context == null ? null : context.SessionId);
            if (jobs.Count == 0)
            {
                return Task.FromResult(ToolResult.Success(call, "（没有后台任务）", "后台任务 0 个", false, false));
            }

            var builder = new StringBuilder();
            foreach (BackgroundJobInfo job in jobs)
            {
                builder.Append("- ").Append(job.Id).Append(" · ")
                    .Append(job.Running ? "运行中" : "已结束");
                if (job.ExitCode.HasValue)
                {
                    builder.Append(" · 退出码 ").Append(job.ExitCode.Value);
                }

                builder.Append(" · ").AppendLine(Summarize(job.Command));
            }

            return Task.FromResult(ToolResult.Success(
                call,
                builder.ToString().TrimEnd(),
                "后台任务 " + jobs.Count + " 个",
                true,
                false));
        }

        internal static string Summarize(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return "(空)";
            }

            string oneLine = text.Replace("\r", " ").Replace("\n", " ").Trim();
            return oneLine.Length <= 120 ? oneLine : oneLine.Substring(0, 120) + "…";
        }
    }

    /// <summary>job_output：读取某个后台任务上次读取之后的新输出。</summary>
    public sealed class JobOutputTool : IAgentTool
    {
        private readonly BackgroundJobRegistry _jobs;

        public JobOutputTool(BackgroundJobRegistry jobs)
        {
            _jobs = jobs;
        }

        public string Name
        {
            get { return ToolNames.JobOutput; }
        }

        public string Description
        {
            get { return "读取后台任务自上次读取之后的新输出；可等一段时间再读。"; }
        }

        public ToolRisk Risk
        {
            get { return ToolRisk.Low; }
        }

        public bool RequiresApproval
        {
            get { return false; }
        }

        public ToolApprovalPreview Describe(ToolCall call, ToolContext context)
        {
            return null;
        }

        public async Task<ToolResult> ExecuteAsync(
            ToolCall call,
            ToolContext context,
            CancellationToken cancellationToken)
        {
            try
            {
                ToolArguments arguments = ToolArguments.Parse(call == null ? null : call.ArgumentsJson);
                string id = arguments.GetRequiredString("id", 80);
                int waitMs = arguments.GetOptionalInt("waitMs", 0, 0, 60000);
                BackgroundJobRegistry.BackgroundJob job = _jobs.Find(id);
                if (job == null)
                {
                    return ToolResult.Failure(call, ToolResultStatus.Failed, "job_not_found", "找不到后台任务：" + id + "。");
                }

                string body = waitMs > 0
                    ? await job.WaitForReadAsync(waitMs, cancellationToken).ConfigureAwait(false)
                    : job.Read();

                return ToolResult.Success(
                    call,
                    "[" + job.Describe() + "]\n" + body,
                    "读取 " + id,
                    true,
                    false);
            }
            catch (ToolArgumentException exception)
            {
                return ToolResult.Failure(call, ToolResultStatus.InvalidArguments, exception.Code, exception.Message);
            }
        }
    }

    /// <summary>job_kill：终止后台任务及其子进程。</summary>
    public sealed class JobKillTool : IAgentTool
    {
        private readonly BackgroundJobRegistry _jobs;

        public JobKillTool(BackgroundJobRegistry jobs)
        {
            _jobs = jobs;
        }

        public string Name
        {
            get { return ToolNames.JobKill; }
        }

        public string Description
        {
            get { return "终止一个后台任务（含子进程）。"; }
        }

        public ToolRisk Risk
        {
            get { return ToolRisk.Medium; }
        }

        public bool RequiresApproval
        {
            get { return false; }
        }

        public ToolApprovalPreview Describe(ToolCall call, ToolContext context)
        {
            return null;
        }

        public Task<ToolResult> ExecuteAsync(
            ToolCall call,
            ToolContext context,
            CancellationToken cancellationToken)
        {
            try
            {
                ToolArguments arguments = ToolArguments.Parse(call == null ? null : call.ArgumentsJson);
                string id = arguments.GetRequiredString("id", 80);
                BackgroundJobRegistry.BackgroundJob job = _jobs.Find(id);
                if (job == null)
                {
                    return Task.FromResult(ToolResult.Failure(
                        call,
                        ToolResultStatus.Failed,
                        "job_not_found",
                        "找不到后台任务：" + id + "。"));
                }

                job.Kill();
                return Task.FromResult(ToolResult.Success(
                    call,
                    "已请求终止 " + id + "。",
                    "终止 " + id,
                    false,
                    false));
            }
            catch (ToolArgumentException exception)
            {
                return Task.FromResult(ToolResult.Failure(
                    call,
                    ToolResultStatus.InvalidArguments,
                    exception.Code,
                    exception.Message));
            }
        }
    }
}
