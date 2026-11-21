using System;
using System.Collections.Generic;

namespace MiaoWu.Core.Agents.Tools
{
    /// <summary>
    /// 计划模式开关。会话把它放进 <see cref="ToolContext.Plan"/>，`exit_plan_mode` 在审批通过后
    /// 通过它把会话切回可执行状态；工具本身不直接改会话字段。
    /// </summary>
    public interface ISessionPlanMode
    {
        /// <summary>当前是否处于计划模式。</summary>
        bool IsActive { get; }

        /// <summary>退出计划模式；退出后下一步就能看到写文件 / 命令类工具。</summary>
        void ExitPlanMode();
    }

    /// <summary>
    /// 计划模式下的工具可见性：只保留只读工具与 `exit_plan_mode`，
    /// 让模型先调研再给计划，而不是边想边改。
    /// </summary>
    public static class PlanModeToolFilter
    {
        /// <summary>计划模式允许的工具名（不含 `exit_plan_mode`，它总是保留）。</summary>
        public static readonly IReadOnlyList<string> ReadOnlyToolNames = new[]
        {
            ToolNames.Read,
            ToolNames.Glob,
            ToolNames.Grep,
            ToolNames.WebFetch,
            ToolNames.WebSearch,
            ToolNames.Skill,
            ToolNames.WorkingDirectory,
            ToolNames.JobList,
            ToolNames.JobOutput,
            ToolNames.GetGoal,
            ToolNames.Present,
            ToolNames.AskUserQuestion
        };

        /// <summary>计划模式下过滤工具声明；关闭时原样返回。</summary>
        public static IReadOnlyList<ToolDefinition> Filter(
            IReadOnlyList<ToolDefinition> definitions,
            bool planMode)
        {
            if (definitions == null)
            {
                return new ToolDefinition[0];
            }

            if (!planMode)
            {
                return definitions;
            }

            var kept = new List<ToolDefinition>(definitions.Count);
            for (int i = 0; i < definitions.Count; i++)
            {
                ToolDefinition definition = definitions[i];
                if (definition == null)
                {
                    continue;
                }

                if (string.Equals(definition.Name, ToolNames.ExitPlanMode, StringComparison.Ordinal)
                    || ContainsName(ReadOnlyToolNames, definition.Name))
                {
                    kept.Add(definition);
                }
            }

            return kept;
        }

        private static bool ContainsName(IReadOnlyList<string> names, string name)
        {
            for (int i = 0; i < names.Count; i++)
            {
                if (string.Equals(names[i], name, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
