using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MiaoWu.Core.Agents.Schedules;
using MiaoWu.Core.Agents.Tools;

namespace MiaoWu.Infrastructure.Tools
{
    /// <summary>日程的统一文本格式：列表、详情和创建 / 编辑的返回共用。</summary>
    public static class ScheduleText
    {
        public static string Describe(ScheduleDefinition definition)
        {
            if (definition == null)
            {
                return string.Empty;
            }

            var builder = new StringBuilder();
            builder.Append(definition.Id).Append(" · ").Append(definition.Title).Append('\n');
            builder.Append("  规则：").Append(DescribeRule(definition.Rule)).Append('\n');
            builder.Append("  目标：").Append(DescribeTarget(definition.Target)).Append('\n');
            builder.Append("  状态：").Append(definition.Active ? "启用" : "已停用").Append('\n');
            builder.Append("  下次：").Append(Format(definition.NextRunAt)).Append('\n');
            builder.Append("  已投递：").Append(definition.RunCount.ToString(CultureInfo.InvariantCulture)).Append(" 次");
            if (definition.LastRunAt.HasValue)
            {
                builder.Append("，上次 ").Append(Format(definition.LastRunAt));
            }

            builder.Append('\n');
            builder.Append("  内容：").Append(Trim(definition.Prompt, 120));
            return builder.ToString();
        }

        /// <summary>只取规则摘要；规则坏掉时退回原文，不让列表整体失败。</summary>
        public static string DescribeRule(string rule)
        {
            try
            {
                return ScheduleRecurrence.Parse(rule).Describe();
            }
            catch (ScheduleException)
            {
                return Trim(rule, 80);
            }
        }

        private static string DescribeTarget(ScheduleTarget target)
        {
            switch (target)
            {
                case ScheduleTarget.NewOnce: return "新建会话（复用同一个）";
                case ScheduleTarget.New: return "每次新建会话";
                default: return "当前会话";
            }
        }

        private static string Format(DateTimeOffset? value)
        {
            return value.HasValue
                ? value.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
                : "无";
        }

        private static string Trim(string text, int max)
        {
            string value = (text ?? string.Empty).Replace("\r", string.Empty).Replace('\n', ' ').Trim();
            return value.Length > max ? value.Substring(0, max) + "…" : value;
        }
    }

    /// <summary>schedule_create：新建一条定时任务。</summary>
    public sealed class ScheduleCreateTool : IAgentTool
    {
        private readonly IScheduleService _schedules;

        public ScheduleCreateTool(IScheduleService schedules)
        {
            _schedules = schedules ?? throw new ArgumentNullException(nameof(schedules));
        }

        public string Name
        {
            get { return ToolNames.ScheduleCreate; }
        }

        public string Description
        {
            get { return "新建定时任务：到点把 prompt 作为用户消息投递到会话。"; }
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
            string title = null;
            try
            {
                title = ToolArguments.Parse(call == null ? null : call.ArgumentsJson)
                    .GetOptionalString("title", null, 200);
            }
            catch (ToolArgumentException)
            {
            }

            return new ToolApprovalPreview
            {
                Title = "新建定时任务",
                Target = title ?? string.Empty,
                Summary = "到点向会话投递一条消息",
                StateToken = null
            };
        }

        public Task<ToolResult> ExecuteAsync(
            ToolCall call,
            ToolContext context,
            CancellationToken cancellationToken)
        {
            string title;
            string prompt;
            string rule;
            ScheduleTarget target;
            try
            {
                ToolArguments arguments = ToolArguments.Parse(call == null ? null : call.ArgumentsJson);
                title = arguments.GetRequiredString("title", 200);
                prompt = arguments.GetRequiredString("prompt", 8000);
                rule = arguments.GetRequiredString("rrule", 4000);
                target = ParseTarget(arguments.GetOptionalString("target", "this_session", 40));
            }
            catch (ToolArgumentException exception)
            {
                return Task.FromResult(ToolResult.Failure(
                    call,
                    ToolResultStatus.InvalidArguments,
                    exception.Code,
                    exception.Message));
            }

            if (_schedules == null)
            {
                return Task.FromResult(ToolResult.Failure(
                    call,
                    ToolResultStatus.Failed,
                    "schedule_unavailable",
                    "当前会话没有定时任务服务。"));
            }

            try
            {
                ScheduleDefinition definition = _schedules.Create(
                    title,
                    prompt,
                    rule,
                    target,
                    context == null ? null : context.SessionId,
                    context == null ? null : context.WorkspaceId);

                return Task.FromResult(ToolResult.Success(
                    call,
                    "已创建定时任务：\n" + ScheduleText.Describe(definition),
                    "新建定时任务 " + definition.Title,
                    true,
                    false));
            }
            catch (ScheduleException exception)
            {
                return Task.FromResult(ToolResult.Failure(
                    call,
                    ToolResultStatus.InvalidArguments,
                    exception.Code,
                    exception.Message));
            }
        }

        private static ScheduleTarget ParseTarget(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return ScheduleTarget.ThisSession;
            }

            switch (value.Trim().ToLowerInvariant())
            {
                case "this_session":
                case "this-session":
                    return ScheduleTarget.ThisSession;
                case "new_once":
                case "new-once":
                    return ScheduleTarget.NewOnce;
                case "new":
                    return ScheduleTarget.New;
                default:
                    throw new ToolArgumentException(
                        "invalid_arguments",
                        "target 只支持 this_session / new_once / new。");
            }
        }
    }

    /// <summary>schedule_edit：改标题 / 内容 / 规则，或停用 / 恢复。</summary>
    public sealed class ScheduleEditTool : IAgentTool
    {
        private readonly IScheduleService _schedules;

        public ScheduleEditTool(IScheduleService schedules)
        {
            _schedules = schedules ?? throw new ArgumentNullException(nameof(schedules));
        }

        public string Name
        {
            get { return ToolNames.ScheduleEdit; }
        }

        public string Description
        {
            get { return "编辑定时任务：标题、投递内容、规则，或停用 / 恢复。"; }
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
            return null;
        }

        public Task<ToolResult> ExecuteAsync(
            ToolCall call,
            ToolContext context,
            CancellationToken cancellationToken)
        {
            string id;
            string title = null;
            string prompt = null;
            string rule = null;
            bool? active = null;
            try
            {
                ToolArguments arguments = ToolArguments.Parse(call == null ? null : call.ArgumentsJson);
                id = arguments.GetRequiredString("id", 80);
                if (arguments.Has("title"))
                {
                    title = arguments.GetOptionalString("title", null, 200);
                }

                if (arguments.Has("prompt"))
                {
                    prompt = arguments.GetOptionalString("prompt", null, 8000);
                }

                if (arguments.Has("rrule"))
                {
                    rule = arguments.GetOptionalString("rrule", null, 4000);
                }

                if (arguments.Has("active"))
                {
                    active = arguments.GetOptionalBool("active", true);
                }
            }
            catch (ToolArgumentException exception)
            {
                return Task.FromResult(ToolResult.Failure(
                    call,
                    ToolResultStatus.InvalidArguments,
                    exception.Code,
                    exception.Message));
            }

            try
            {
                ScheduleUpdateResult result = _schedules == null
                    ? null
                    : _schedules.Update(id, title, prompt, rule, active);
                if (result == null || result.Status == ScheduleUpdateStatus.NotFound)
                {
                    return Task.FromResult(ToolResult.Failure(
                        call,
                        ToolResultStatus.Failed,
                        "schedule_not_found",
                        "找不到定时任务：" + id + "。"));
                }

                if (result.Status == ScheduleUpdateStatus.Conflict)
                {
                    return Task.FromResult(ToolResult.Failure(
                        call,
                        ToolResultStatus.Failed,
                        "schedule_conflict",
                        "定时任务已在别处被修改，请重新读取后再改：" + id + "。"));
                }

                ScheduleDefinition definition = result.Definition;
                return Task.FromResult(ToolResult.Success(
                    call,
                    "已更新：\n" + ScheduleText.Describe(definition),
                    "编辑定时任务 " + id,
                    true,
                    false));
            }
            catch (ScheduleException exception)
            {
                return Task.FromResult(ToolResult.Failure(
                    call,
                    ToolResultStatus.InvalidArguments,
                    exception.Code,
                    exception.Message));
            }
        }
    }

    /// <summary>schedule_list：按下次触发时间排序列出任务。</summary>
    public sealed class ScheduleListTool : IAgentTool
    {
        private readonly IScheduleService _schedules;

        public ScheduleListTool(IScheduleService schedules)
        {
            _schedules = schedules;
        }

        public string Name
        {
            get { return ToolNames.ScheduleList; }
        }

        public string Description
        {
            get { return "列出定时任务（默认只列启用的）。"; }
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
            bool all = false;
            try
            {
                ToolArguments arguments = ToolArguments.Parse(call == null ? null : call.ArgumentsJson);
                all = arguments.GetOptionalBool("all", false);
            }
            catch (ToolArgumentException exception)
            {
                return Task.FromResult(ToolResult.Failure(
                    call,
                    ToolResultStatus.InvalidArguments,
                    exception.Code,
                    exception.Message));
            }

            if (_schedules == null)
            {
                return Task.FromResult(ToolResult.Failure(
                    call,
                    ToolResultStatus.Failed,
                    "schedule_unavailable",
                    "当前会话没有定时任务服务。"));
            }

            IReadOnlyList<ScheduleDefinition> list = _schedules.List(all);
            if (list.Count == 0)
            {
                return Task.FromResult(ToolResult.Success(
                    call,
                    all ? "还没有定时任务。" : "没有启用的定时任务（加 all=true 可以看已停用的）。",
                    "列出定时任务",
                    true,
                    false));
            }

            var builder = new StringBuilder();
            builder.Append(all ? "全部定时任务：" : "启用的定时任务：").Append(list.Count).Append(" 个\n\n");
            for (int i = 0; i < list.Count; i++)
            {
                builder.Append(ScheduleText.Describe(list[i]));
                if (i < list.Count - 1)
                {
                    builder.Append('\n');
                }
            }

            return Task.FromResult(ToolResult.Success(
                call,
                builder.ToString(),
                "列出定时任务",
                true,
                false));
        }
    }

    /// <summary>schedule_view：看一条任务的完整状态。</summary>
    public sealed class ScheduleViewTool : IAgentTool
    {
        private readonly IScheduleService _schedules;

        public ScheduleViewTool(IScheduleService schedules)
        {
            _schedules = schedules;
        }

        public string Name
        {
            get { return ToolNames.ScheduleView; }
        }

        public string Description
        {
            get { return "查看一条定时任务的规则与下次触发时间。"; }
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
            string id;
            try
            {
                ToolArguments arguments = ToolArguments.Parse(call == null ? null : call.ArgumentsJson);
                id = arguments.GetRequiredString("id", 80);
            }
            catch (ToolArgumentException exception)
            {
                return Task.FromResult(ToolResult.Failure(
                    call,
                    ToolResultStatus.InvalidArguments,
                    exception.Code,
                    exception.Message));
            }

            ScheduleDefinition definition = _schedules == null ? null : _schedules.Get(id);
            if (definition == null)
            {
                return Task.FromResult(ToolResult.Failure(
                    call,
                    ToolResultStatus.Failed,
                    "schedule_not_found",
                    "找不到定时任务：" + id + "。"));
            }

            return Task.FromResult(ToolResult.Success(
                call,
                ScheduleText.Describe(definition) + "\n\n规则原文：\n" + definition.Rule,
                "查看定时任务 " + id,
                true,
                false));
        }
    }
}
