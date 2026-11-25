using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MiaoWu.Core.Agents.Tools;
using MiaoWu.Core.Skills;
using MiaoWu.Infrastructure.Skills;

namespace MiaoWu.Infrastructure.Tools
{
    /// <summary>
    /// skill：列出或加载技能。扫描「工作区 .miaowu\skills」与「用户级技能目录」，
    /// 同名时工作区优先；工作区 <c>skills.json</c> 停用的技能不会出现在目录里。
    /// 不带 name 只返回目录（名称 + 一行描述），带 name 才返回正文。
    /// 与能力市场页面共用 <see cref="SkillCatalogService"/>，界面看到的和模型可用的一致。
    /// </summary>
    public sealed class SkillTool : IAgentTool
    {
        private const int MaxNameLength = 200;

        private readonly SkillCatalogService _catalog;

        public SkillTool(string userSkillsDirectory)
        {
            _catalog = new SkillCatalogService(userSkillsDirectory);
        }

        public string Name
        {
            get { return ToolNames.Skill; }
        }

        public string Description
        {
            get { return "列出或加载技能说明：不带 name 列出目录，带 name 返回该技能正文。"; }
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
            try
            {
                ToolArguments arguments = ToolArguments.Parse(call == null ? null : call.ArgumentsJson);
                string name = arguments.GetOptionalString("name", null, MaxNameLength);
                List<SkillDescriptor> skills = LoadCatalog(context);

                if (string.IsNullOrWhiteSpace(name))
                {
                    return Task.FromResult(ToolResult.Success(
                        call,
                        RenderCatalog(skills),
                        "列出技能（" + skills.Count + " 个）",
                        false,
                        false));
                }

                SkillDescriptor match = null;
                foreach (SkillDescriptor entry in skills)
                {
                    if (string.Equals(entry.Name, name.Trim(), StringComparison.OrdinalIgnoreCase))
                    {
                        match = entry;
                        break;
                    }
                }

                if (match == null)
                {
                    return Task.FromResult(ToolResult.Failure(
                        call,
                        ToolResultStatus.Failed,
                        "skill_not_found",
                        "没有名为 “" + name + "” 的技能。可用：" + RenderNames(skills) + "。"));
                }

                string body;
                try
                {
                    body = _catalog.ReadBody(match);
                }
                catch (Exception exception)
                {
                    return Task.FromResult(ToolResult.Failure(
                        call,
                        ToolResultStatus.Failed,
                        "skill_unreadable",
                        "技能文件读不出来：" + match.Name + "（" + exception.Message + "）。"));
                }

                return Task.FromResult(ToolResult.Success(
                    call,
                    body,
                    "加载技能 " + match.Name,
                    false,
                    body.Length >= 256 * 1024));
            }
            catch (ToolArgumentException exception)
            {
                return Task.FromResult(ToolResult.Failure(
                    call,
                    ToolResultStatus.InvalidArguments,
                    exception.Code,
                    exception.Message));
            }
            catch (Exception exception)
            {
                return Task.FromResult(ToolResult.Failure(
                    call,
                    ToolResultStatus.Failed,
                    "skill_failed",
                    "读取技能失败：" + exception.Message));
            }
        }

        private List<SkillDescriptor> LoadCatalog(ToolContext context)
        {
            SkillCatalog catalog = _catalog.Load(context == null ? null : context.WorkspaceRoot);
            var skills = new List<SkillDescriptor>();
            foreach (SkillDescriptor skill in catalog.Skills)
            {
                if (skill.IsValid && skill.IsEnabled)
                {
                    skills.Add(skill);
                }
            }

            return skills;
        }

        private static string RenderCatalog(List<SkillDescriptor> skills)
        {
            if (skills.Count == 0)
            {
                return "（没有可用技能。把技能文件夹放进 .miaowu\\skills 或用户级 skills 目录即可。）";
            }

            var builder = new StringBuilder();
            builder.AppendLine("可用技能（用 skill 带 name 加载正文）：");
            foreach (SkillDescriptor entry in skills)
            {
                builder.Append("- ").Append(entry.Name);
                if (!string.IsNullOrWhiteSpace(entry.Description))
                {
                    builder.Append("：").Append(entry.Description);
                }

                builder.AppendLine();
            }

            return builder.ToString().TrimEnd();
        }

        private static string RenderNames(List<SkillDescriptor> skills)
        {
            if (skills.Count == 0)
            {
                return "（无）";
            }

            var names = new List<string>(skills.Count);
            foreach (SkillDescriptor entry in skills)
            {
                names.Add(entry.Name);
            }

            return string.Join("、", names);
        }
    }
}
