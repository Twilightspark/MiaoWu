using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using MiaoWu.Core.Agents.Tools;
using MiaoWu.Infrastructure.Json;

namespace MiaoWu.Infrastructure.Tools
{
    /// <summary>
    /// 工具展示投影（对齐 DSH 的 presentCall / presentResult 的 C# 等价物）：
    /// 从调用参数里取出“动作对象”，从结果里取出“发生了什么”，供工具行显示。
    /// 只读字符串，不改执行语义；解析失败一律回落到安全摘要。
    /// </summary>
    public static class ToolPresentationBuilder
    {
        /// <summary>调用标题 + 摘要。</summary>
        public static ToolPresentation ProjectCall(string toolName, string argumentsJson)
        {
            var presentation = new ToolPresentation { Title = TitleOf(toolName) };
            string name = (toolName ?? string.Empty).Trim();
            JsonValue root = null;
            try
            {
                if (!string.IsNullOrWhiteSpace(argumentsJson))
                {
                    root = JsonParser.Parse(argumentsJson);
                }
            }
            catch (Exception)
            {
                root = null;
            }

            string summary = null;
            if (root != null)
            {
                switch (name)
                {
                    case ToolNames.Read:
                    case ToolNames.Write:
                    case ToolNames.Edit:
                        summary = Read(root, "path");
                        break;
                    case ToolNames.Glob:
                        summary = Read(root, "pattern");
                        break;
                    case ToolNames.Grep:
                        summary = Join(Read(root, "pattern"), Read(root, "path"));
                        break;
                    case ToolNames.Pwsh:
                        summary = Read(root, "command");
                        break;
                    case ToolNames.WebFetch:
                        summary = Read(root, "url");
                        break;
                    case ToolNames.WebSearch:
                        summary = Read(root, "query");
                        break;
                    case ToolNames.JobOutput:
                    case ToolNames.JobKill:
                        summary = Read(root, "id");
                        break;
                    case ToolNames.Present:
                        summary = CountList(root, "files", "个文件");
                        break;
                    case ToolNames.CreateGoal:
                        summary = Read(root, "objective");
                        break;
                    case ToolNames.GetGoal:
                    case ToolNames.UpdateGoal:
                        summary = Join(Read(root, "id"), Read(root, "action"));
                        break;
                    case ToolNames.AskUserQuestion:
                        summary = Read(root, "header");
                        if (string.IsNullOrWhiteSpace(summary))
                        {
                            summary = Read(root, "question");
                        }

                        break;
                    case ToolNames.ExitPlanMode:
                        summary = FirstLine(Read(root, "plan"), 60);
                        break;
                    case ToolNames.ScheduleCreate:
                        summary = Read(root, "title");
                        break;
                    case ToolNames.ScheduleEdit:
                        summary = Read(root, "id");
                        break;
                    case ToolNames.ScheduleView:
                        summary = Read(root, "id");
                        break;
                    case ToolNames.Skill:
                        summary = Read(root, "name");
                        break;
                    case ToolNames.WorkingDirectory:
                        summary = Read(root, "path");
                        break;
                }
            }

            presentation.Summary = Clip(summary, 160);
            return presentation;
        }

        /// <summary>结果摘要；失败时退回错误码 / 首行，永不返回 null。</summary>
        public static string ProjectResult(string toolName, ToolResult result)
        {
            if (result == null)
            {
                return string.Empty;
            }

            if (result.Status != ToolResultStatus.Succeeded)
            {
                if (!string.IsNullOrWhiteSpace(result.ErrorCode))
                {
                    return ErrorText(result.ErrorCode);
                }

                if (result.Status == ToolResultStatus.Denied
                    || result.Status == ToolResultStatus.Blocked
                    || result.Status == ToolResultStatus.Cancelled)
                {
                    return StatusText(result.Status);
                }

                string line = FirstLine(result.Content, 40);
                return string.IsNullOrWhiteSpace(line) ? "失败" : line;
            }

            string name = (toolName ?? string.Empty).Trim();
            string content = result.Content ?? string.Empty;
            switch (name)
            {
                case ToolNames.Read:
                    return "已读取 " + LineCount(content) + " 行";
                case ToolNames.Write:
                    return "已写入 " + LineCount(content) + " 行";
                case ToolNames.Edit:
                    return "已修改";
                case ToolNames.Glob:
                    return "找到 " + LineCount(content) + " 项";
                case ToolNames.Grep:
                    return "命中 " + LineCount(content) + " 行";
                case ToolNames.Pwsh:
                    return ExitCodeOf(content);
                case ToolNames.WebFetch:
                    return "已抓取 " + content.Length + " 字";
                case ToolNames.WebSearch:
                    return "搜索完成";
                case ToolNames.JobList:
                    return "后台任务已列出";
                case ToolNames.JobOutput:
                    return content.Length == 0 ? "没有新输出" : "已读取输出";
                case ToolNames.JobKill:
                    return "已终止";
                case ToolNames.Present:
                    return "已交付 " + CountMatches(content, "^- ") + " 个文件";
                case ToolNames.AskUserQuestion:
                    return content.IndexOf("跳过", StringComparison.Ordinal) >= 0 ? "已跳过" : "已回答";
                case ToolNames.ExitPlanMode:
                    return content.IndexOf("已退出计划模式", StringComparison.Ordinal) >= 0
                        ? "已退出计划模式"
                        : "计划已记录";
                case ToolNames.ScheduleCreate:
                    return "已创建";
                case ToolNames.ScheduleEdit:
                    return "已更新";
                case ToolNames.ScheduleList:
                case ToolNames.ScheduleView:
                    return "已读取日程";
                case ToolNames.UpdateGoal:
                    return content.IndexOf("blocked", StringComparison.OrdinalIgnoreCase) >= 0
                        ? "目标已阻塞"
                        : "目标已更新";
                default:
                    return "完成";
            }
        }

        private static string TitleOf(string toolName)
        {
            switch ((toolName ?? string.Empty).Trim())
            {
                case ToolNames.Read: return "读取文件";
                case ToolNames.Write: return "写入文件";
                case ToolNames.Edit: return "编辑文件";
                case ToolNames.Glob: return "查找文件";
                case ToolNames.Grep: return "搜索文件";
                case ToolNames.Pwsh: return "执行命令";
                case ToolNames.Skill: return "加载技能";
                case ToolNames.WorkingDirectory: return "工作目录";
                case ToolNames.WebFetch: return "抓取网页";
                case ToolNames.WebSearch: return "搜索网页";
                case ToolNames.JobList: return "后台任务";
                case ToolNames.JobOutput: return "读取后台输出";
                case ToolNames.JobKill: return "终止后台任务";
                case ToolNames.Present: return "交付文件";
                case ToolNames.CreateGoal: return "建立目标";
                case ToolNames.GetGoal: return "读取目标";
                case ToolNames.UpdateGoal: return "更新目标";
                case ToolNames.AskUserQuestion: return "向你提问";
                case ToolNames.ExitPlanMode: return "退出计划模式";
                case ToolNames.ScheduleCreate: return "新建定时任务";
                case ToolNames.ScheduleEdit: return "编辑定时任务";
                case ToolNames.ScheduleList: return "列出定时任务";
                case ToolNames.ScheduleView: return "查看定时任务";
                default: return string.IsNullOrWhiteSpace(toolName) ? "工具调用" : toolName;
            }
        }

        private static string ErrorText(string code)
        {
            switch (code)
            {
                case "tool_timeout": return "超时";
                case "invalid_arguments": return "参数无效";
                case "permission_denied":
                case "denied": return "已拒绝";
                case "blocked": return "已拦截";
                case "not_found":
                case "file_not_found": return "找不到文件";
                case "schedule_not_found": return "找不到任务";
                case "job_not_found": return "找不到后台任务";
                case "cancelled": return "已取消";
                default: return code;
            }
        }

        private static string StatusText(ToolResultStatus status)
        {
            switch (status)
            {
                case ToolResultStatus.Denied: return "已拒绝";
                case ToolResultStatus.Blocked: return "已拦截";
                case ToolResultStatus.Cancelled: return "已取消";
                default: return "失败";
            }
        }

        private static string ExitCodeOf(string content)
        {
            Match match = Regex.Match(content ?? string.Empty, @"\[exit code: (-?\d+)\]");
            return match.Success ? "退出码 " + match.Groups[1].Value : "已完成";
        }

        private static int LineCount(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return 0;
            }

            int lines = 1;
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] == '\n')
                {
                    lines++;
                }
            }

            return lines;
        }

        private static int CountMatches(string text, string pattern)
        {
            return Regex.Matches(text ?? string.Empty, pattern, RegexOptions.Multiline).Count;
        }

        private static string CountList(JsonValue root, string name, string unit)
        {
            JsonValue node = root.Get(name);
            if (node == null || node.Kind != JsonKind.Array)
            {
                return null;
            }

            return node.Items.Count.ToString(CultureInfo.InvariantCulture) + " " + unit;
        }

        private static string Read(JsonValue root, string name)
        {
            JsonValue node = root == null ? null : root.Get(name);
            return node == null ? null : node.AsString(null);
        }

        private static string Join(string first, string second)
        {
            if (string.IsNullOrWhiteSpace(first))
            {
                return second;
            }

            return string.IsNullOrWhiteSpace(second) ? first : first + " · " + second;
        }

        private static string FirstLine(string text, int max)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return string.Empty;
            }

            string value = text.Replace("\r", string.Empty);
            int index = value.IndexOf('\n');
            if (index >= 0)
            {
                value = value.Substring(0, index);
            }

            value = value.Trim().TrimStart('#').Trim();
            return value.Length > max ? value.Substring(0, max) + "…" : value;
        }

        private static string Clip(string text, int max)
        {
            string value = (text ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
            return value.Length > max ? value.Substring(0, max) + "…" : value;
        }
    }

    /// <summary>工具行的一次投影：标题 + 调用摘要。</summary>
    public sealed class ToolPresentation
    {
        public string Title { get; set; }

        public string Summary { get; set; }
    }
}
