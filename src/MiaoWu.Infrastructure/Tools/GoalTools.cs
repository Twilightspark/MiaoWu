using System;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MiaoWu.Core.Agents.Goals;
using MiaoWu.Core.Agents.Tools;

namespace MiaoWu.Infrastructure.Tools
{
    /// <summary>
    /// create_goal：为当前会话建立一个可跟踪的长期目标。同一时刻只有一个目标；
    /// 目标只写工作区 <c>.agent\goals</c>（由宿主注入的持久化实现负责），不碰用户文件。
    /// </summary>
    public sealed class CreateGoalTool : IAgentTool, IToolAuditRedactor
    {
        private const int MaxObjectiveArgumentLength = 4000;

        public string Name
        {
            get { return ToolNames.CreateGoal; }
        }

        public string Description
        {
            get
            {
                return "为当前会话建立一个可跟踪的目标；写入 objective 与可选的 max_goal_rounds，"
                    + "已有进行中的目标时会拒绝。";
            }
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
            ISessionGoalState goals = GoalToolText.GetState(context);
            if (goals == null)
            {
                return Task.FromResult(GoalToolText.Unavailable(call));
            }

            try
            {
                ToolArguments arguments = ToolArguments.Parse(call == null ? null : call.ArgumentsJson);
                string objective = arguments.GetRequiredString("objective", MaxObjectiveArgumentLength);
                int? maxRounds = arguments.Has("max_goal_rounds")
                    ? (int?)arguments.GetOptionalInt("max_goal_rounds", SessionGoalState.DefaultMaxGoalRounds, 0, int.MaxValue)
                    : null;

                GoalMutationResult result = goals.Create(objective, maxRounds);
                if (!result.IsSuccess)
                {
                    return Task.FromResult(ToolResult.Failure(
                        call,
                        ToolResultStatus.Failed,
                        result.ErrorCode,
                        result.ErrorMessage));
                }

                return Task.FromResult(ToolResult.Success(
                    call,
                    GoalToolText.Describe(goals, result.Goal)
                        + "\n\n改目标时用 update_goal，并带上这里显示的 revision。",
                    "创建目标 " + result.Goal.Id,
                    false,
                    false));
            }
            catch (ToolArgumentException exception)
            {
                return Task.FromResult(ToolResultFactory.InvalidArguments(call, exception));
            }
            catch (Exception exception)
            {
                return Task.FromResult(ToolResult.Failure(
                    call,
                    ToolResultStatus.Failed,
                    "goal_create_failed",
                    "创建目标失败：" + exception.Message));
            }
        }

        public string GetAuditArguments(ToolCall call, ToolContext context)
        {
            try
            {
                ToolArguments arguments = ToolArguments.Parse(call == null ? null : call.ArgumentsJson);
                string objective = arguments.GetOptionalString("objective", string.Empty, MaxObjectiveArgumentLength);
                string summary = "objective=" + (objective ?? string.Empty).Length + " 字符";
                if (arguments.Has("max_goal_rounds"))
                {
                    summary += "；max_goal_rounds=" + arguments
                        .GetOptionalInt("max_goal_rounds", 0, 0, int.MaxValue)
                        .ToString(CultureInfo.InvariantCulture);
                }

                return summary;
            }
            catch (Exception)
            {
                return "create_goal";
            }
        }
    }

    /// <summary>
    /// get_goal：读取当前会话目标。没有目标时返回一句说明，不当作错误。
    /// </summary>
    public sealed class GetGoalTool : IAgentTool
    {
        public string Name
        {
            get { return ToolNames.GetGoal; }
        }

        public string Description
        {
            get { return "读取当前会话的目标（id、revision、阶段、轮次与阻塞原因）；改目标前先调用它。"; }
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
            ISessionGoalState goals = GoalToolText.GetState(context);
            if (goals == null)
            {
                return Task.FromResult(GoalToolText.Unavailable(call));
            }

            try
            {
                AgentGoal goal = goals.Current;
                if (goal == null)
                {
                    return Task.FromResult(ToolResult.Success(
                        call,
                        "当前会话没有目标。需要长期跟踪时先用 create_goal。",
                        "无目标",
                        false,
                        false));
                }

                return Task.FromResult(ToolResult.Success(
                    call,
                    GoalToolText.Describe(goals, goal),
                    "读取目标 " + goal.Id,
                    false,
                    false));
            }
            catch (Exception exception)
            {
                return Task.FromResult(ToolResult.Failure(
                    call,
                    ToolResultStatus.Failed,
                    "goal_read_failed",
                    "读取目标失败：" + exception.Message));
            }
        }
    }

    /// <summary>
    /// update_goal：按 goal_id + revision 修改目标；revision 不匹配按冲突返回，
    /// 让模型先 get_goal 再重试。
    /// </summary>
    public sealed class UpdateGoalTool : IAgentTool, IToolAuditRedactor
    {
        private const int MaxGoalIdLength = 64;
        private const int MaxActionLength = 32;
        private const int MaxObjectiveArgumentLength = 4000;
        private const int MaxBlockedReasonLength = 1000;

        public string Name
        {
            get { return ToolNames.UpdateGoal; }
        }

        public string Description
        {
            get
            {
                return "修改当前会话目标：action 取 edit / pause / resume / complete / blocked；"
                    + "必须带 goal_id 与当前 revision。";
            }
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
            ISessionGoalState goals = GoalToolText.GetState(context);
            if (goals == null)
            {
                return Task.FromResult(GoalToolText.Unavailable(call));
            }

            try
            {
                ToolArguments arguments = ToolArguments.Parse(call == null ? null : call.ArgumentsJson);
                string goalId = arguments.GetRequiredString("goal_id", MaxGoalIdLength);
                long revision = arguments.GetRequiredLong("revision");
                string action = arguments.GetRequiredString("action", MaxActionLength);
                string objective = arguments.Has("objective")
                    ? arguments.GetOptionalString("objective", null, MaxObjectiveArgumentLength)
                    : null;
                int? maxRounds = arguments.Has("max_goal_rounds")
                    ? (int?)arguments.GetOptionalInt("max_goal_rounds", 0, 0, int.MaxValue)
                    : null;
                string blockedReason = arguments.Has("blocked_reason")
                    ? arguments.GetOptionalString("blocked_reason", null, MaxBlockedReasonLength)
                    : null;

                GoalMutationResult result = goals.Update(
                    goalId,
                    revision,
                    action,
                    objective,
                    maxRounds,
                    blockedReason);
                if (!result.IsSuccess)
                {
                    return Task.FromResult(ToolResult.Failure(
                        call,
                        ToolResultStatus.Failed,
                        result.ErrorCode,
                        result.ErrorMessage));
                }

                return Task.FromResult(ToolResult.Success(
                    call,
                    GoalToolText.Describe(goals, result.Goal),
                    "更新目标 " + result.Goal.Id + "：" + action.Trim().ToLowerInvariant(),
                    false,
                    false));
            }
            catch (ToolArgumentException exception)
            {
                return Task.FromResult(ToolResultFactory.InvalidArguments(call, exception));
            }
            catch (Exception exception)
            {
                return Task.FromResult(ToolResult.Failure(
                    call,
                    ToolResultStatus.Failed,
                    "goal_update_failed",
                    "更新目标失败：" + exception.Message));
            }
        }

        public string GetAuditArguments(ToolCall call, ToolContext context)
        {
            try
            {
                ToolArguments arguments = ToolArguments.Parse(call == null ? null : call.ArgumentsJson);
                var builder = new StringBuilder();
                builder.Append("goal=");
                builder.Append(arguments.GetOptionalString("goal_id", "?", MaxGoalIdLength));
                builder.Append("；action=");
                builder.Append(arguments.GetOptionalString("action", "?", MaxActionLength));
                if (arguments.Has("revision"))
                {
                    builder.Append("；revision=");
                    builder.Append(arguments.GetRequiredLong("revision").ToString(CultureInfo.InvariantCulture));
                }

                if (arguments.Has("blocked_reason"))
                {
                    string reason = arguments.GetOptionalString("blocked_reason", string.Empty, MaxBlockedReasonLength);
                    builder.Append("；blocked_reason=");
                    builder.Append((reason ?? string.Empty).Length.ToString(CultureInfo.InvariantCulture));
                    builder.Append(" 字符");
                }

                return builder.ToString();
            }
            catch (Exception)
            {
                return "update_goal";
            }
        }
    }

    /// <summary>goal 三件套共用的结果格式化与可用性检查。</summary>
    internal static class GoalToolText
    {
        public static ISessionGoalState GetState(ToolContext context)
        {
            return context == null ? null : context.Goals;
        }

        public static ToolResult Unavailable(ToolCall call)
        {
            return ToolResult.Failure(
                call,
                ToolResultStatus.Failed,
                "goal_unavailable",
                "当前会话没有目标上下文（宿主未注入目标状态）。");
        }

        /// <summary>目标的可读摘要；revision 与轮次对模型可见，用来构造下一次 update。</summary>
        public static string Describe(ISessionGoalState state, AgentGoal goal)
        {
            if (goal == null)
            {
                return "当前会话没有目标。";
            }

            var builder = new StringBuilder();
            builder.Append("目标：");
            builder.Append(goal.Id);
            builder.Append("\n正文：");
            builder.Append(goal.Objective);
            builder.Append("\n状态：");
            builder.Append(GoalPhaseText.ToDisplay(goal.Phase));
            builder.Append("（");
            builder.Append(GoalPhaseText.ToWire(goal.Phase));
            builder.Append("）");
            builder.Append("\n修订：");
            builder.Append(goal.Revision.ToString(CultureInfo.InvariantCulture));
            builder.Append("\n轮次：");
            builder.Append(goal.RoundsStarted.ToString(CultureInfo.InvariantCulture));
            builder.Append(" / ");
            builder.Append(goal.MaxGoalRounds.ToString(CultureInfo.InvariantCulture));
            builder.Append("\n创建：");
            builder.Append(FormatTime(goal.CreatedAt));
            builder.Append("\n更新：");
            builder.Append(FormatTime(goal.UpdatedAt));

            if (goal.Phase == GoalPhase.Blocked && !string.IsNullOrWhiteSpace(goal.BlockedReason))
            {
                builder.Append("\n阻塞原因：");
                builder.Append(goal.BlockedReason);
            }

            string persistenceError = state == null ? null : state.LastPersistenceError;
            if (!string.IsNullOrWhiteSpace(persistenceError))
            {
                builder.Append("\n注意：目标状态未能写入磁盘（");
                builder.Append(persistenceError);
                builder.Append("），重启后会丢失。");
            }

            return builder.ToString();
        }

        private static string FormatTime(DateTimeOffset value)
        {
            return value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        }
    }
}
