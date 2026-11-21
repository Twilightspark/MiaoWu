using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MiaoWu.Core.Agents.Tools;

namespace MiaoWu.Infrastructure.Tools
{
    /// <summary>
    /// skill：列出或加载技能。扫描「工作区 .agent\skills」与「用户级技能目录」，
    /// 同名时工作区优先；不带 name 只返回目录（名称 + 一行描述），带 name 才返回正文。
    /// 对齐 DSH「目录常驻、正文按需加载」的用法。
    /// </summary>
    public sealed class SkillTool : IAgentTool
    {
        private const int MaxNameLength = 200;
        private const int MaxBodyBytes = 262144;

        private readonly string _userSkillsDirectory;

        public SkillTool(string userSkillsDirectory)
        {
            _userSkillsDirectory = userSkillsDirectory;
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
                List<SkillEntry> skills = LoadCatalog(context);

                if (string.IsNullOrWhiteSpace(name))
                {
                    return Task.FromResult(ToolResult.Success(
                        call,
                        RenderCatalog(skills),
                        "列出技能（" + skills.Count + " 个）",
                        false,
                        false));
                }

                SkillEntry match = null;
                foreach (SkillEntry entry in skills)
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

                string body = ReadBody(match.Path);
                if (body == null)
                {
                    return Task.FromResult(ToolResult.Failure(
                        call,
                        ToolResultStatus.Failed,
                        "skill_unreadable",
                        "技能文件读不出来：" + match.Name + "。"));
                }

                return Task.FromResult(ToolResult.Success(
                    call,
                    body,
                    "加载技能 " + match.Name,
                    false,
                    body.Length >= MaxBodyBytes));
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

        private List<SkillEntry> LoadCatalog(ToolContext context)
        {
            var skills = new List<SkillEntry>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            string workspaceRoot = context == null ? null : context.WorkspaceRoot;
            if (!string.IsNullOrWhiteSpace(workspaceRoot))
            {
                AddDirectory(skills, seen, Path.Combine(workspaceRoot, ".agent", "skills"));
            }

            AddDirectory(skills, seen, _userSkillsDirectory);
            skills.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            return skills;
        }

        private static void AddDirectory(List<SkillEntry> skills, HashSet<string> seen, string directory)
        {
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            {
                return;
            }

            string[] files;
            try
            {
                files = Directory.GetFiles(directory, "*.md", SearchOption.TopDirectoryOnly);
            }
            catch (Exception)
            {
                return;
            }

            Array.Sort(files, StringComparer.OrdinalIgnoreCase);
            foreach (string file in files)
            {
                string name = Path.GetFileNameWithoutExtension(file);
                if (string.IsNullOrWhiteSpace(name) || !seen.Add(name))
                {
                    continue;
                }

                skills.Add(new SkillEntry
                {
                    Name = name,
                    Path = file,
                    Description = ReadDescription(file)
                });
            }
        }

        private static string ReadDescription(string path)
        {
            try
            {
                string[] lines = File.ReadAllLines(path);
                bool inFrontMatter = false;
                for (int i = 0; i < lines.Length && i < 40; i++)
                {
                    string line = lines[i].Trim();
                    if (line.Length == 0)
                    {
                        continue;
                    }

                    if (line == "---")
                    {
                        inFrontMatter = !inFrontMatter;
                        continue;
                    }

                    if (inFrontMatter)
                    {
                        continue;
                    }

                    string text = line.TrimStart('#').Trim();
                    if (text.Length == 0)
                    {
                        continue;
                    }

                    return text.Length <= 160 ? text : text.Substring(0, 160) + "…";
                }
            }
            catch (Exception)
            {
            }

            return string.Empty;
        }

        private static string ReadBody(string path)
        {
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists || info.Length > MaxBodyBytes)
                {
                    return null;
                }

                return File.ReadAllText(path).TrimEnd();
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static string RenderCatalog(List<SkillEntry> skills)
        {
            if (skills.Count == 0)
            {
                return "（没有可用技能。把 Markdown 放进 .agent\\skills 或用户级 skills 目录即可。）";
            }

            var builder = new StringBuilder();
            builder.AppendLine("可用技能（用 skill 带 name 加载正文）：");
            foreach (SkillEntry entry in skills)
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

        private static string RenderNames(List<SkillEntry> skills)
        {
            if (skills.Count == 0)
            {
                return "（无）";
            }

            var names = new List<string>(skills.Count);
            foreach (SkillEntry entry in skills)
            {
                names.Add(entry.Name);
            }

            return string.Join("、", names);
        }

        private sealed class SkillEntry
        {
            public string Name;
            public string Path;
            public string Description;
        }
    }
}
