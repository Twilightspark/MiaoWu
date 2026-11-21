using System;
using System.Threading;
using System.Threading.Tasks;
using MiaoWu.Core.Agents.Tools;

namespace MiaoWu.Infrastructure.Tools
{
    /// <summary>
    /// exit_plan_mode：计划模式下的唯一出口。模型先只读调研，再提交一份计划；
    /// 工具需要审批，批准后把会话切回可执行状态，后续步骤就能看到写文件 / 命令类工具。
    /// </summary>
    public sealed class ExitPlanModeTool : IAgentTool
    {
        private const int MaxPlanLength = 16000;

        public string Name
        {
            get { return ToolNames.ExitPlanMode; }
        }

        public string Description
        {
            get { return "提交计划并请求退出计划模式；批准后才能改文件或执行命令。"; }
        }

        public ToolRisk Risk
        {
            get { return ToolRisk.Medium; }
        }

        public bool RequiresApproval
        {
            get { return true; }
        }

        public ToolApprovalPreview Describe(ToolCall call, ToolContext context)
        {
            string plan = null;
            try
            {
                ToolArguments arguments = ToolArguments.Parse(call == null ? null : call.ArgumentsJson);
                plan = arguments.GetRequiredString("plan", MaxPlanLength);
            }
            catch (ToolArgumentException)
            {
            }

            string summary = string.IsNullOrWhiteSpace(plan)
                ? "计划为空"
                : "计划 " + plan.Length + " 字";

            return new ToolApprovalPreview
            {
                Title = "退出计划模式",
                Target = FirstLine(plan),
                Summary = summary,
                StateToken = null
            };
        }

        public Task<ToolResult> ExecuteAsync(
            ToolCall call,
            ToolContext context,
            CancellationToken cancellationToken)
        {
            string plan;
            try
            {
                ToolArguments arguments = ToolArguments.Parse(call == null ? null : call.ArgumentsJson);
                plan = arguments.GetRequiredString("plan", MaxPlanLength).Trim();
            }
            catch (ToolArgumentException exception)
            {
                return Task.FromResult(ToolResult.Failure(
                    call,
                    ToolResultStatus.InvalidArguments,
                    exception.Code,
                    exception.Message));
            }

            if (string.IsNullOrWhiteSpace(plan))
            {
                return Task.FromResult(ToolResult.Failure(
                    call,
                    ToolResultStatus.InvalidArguments,
                    "invalid_arguments",
                    "计划不能为空。"));
            }

            ISessionPlanMode mode = context == null ? null : context.Plan;
            if (mode == null)
            {
                return Task.FromResult(ToolResult.Failure(
                    call,
                    ToolResultStatus.Failed,
                    "plan_unavailable",
                    "当前会话没有计划模式。"));
            }

            if (!mode.IsActive)
            {
                return Task.FromResult(ToolResult.Success(
                    call,
                    "当前不在计划模式；计划已记录，可以直接开始执行：\n\n" + plan,
                    "计划已记录",
                    true,
                    false));
            }

            mode.ExitPlanMode();
            return Task.FromResult(ToolResult.Success(
                call,
                "计划已获批准，已退出计划模式，可以开始执行：\n\n" + plan,
                "退出计划模式",
                true,
                false));
        }

        private static string FirstLine(string plan)
        {
            if (string.IsNullOrWhiteSpace(plan))
            {
                return string.Empty;
            }

            int index = plan.IndexOf('\n');
            string line = index < 0 ? plan : plan.Substring(0, index);
            line = line.Trim().TrimStart('#').Trim();
            return line.Length > 60 ? line.Substring(0, 60) + "…" : line;
        }
    }
}
