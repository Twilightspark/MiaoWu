using System;

namespace MiaoWu.Core.Agents.Tools
{
    /// <summary>
    /// 工具失败后的下一步建议。自测里最贵的一次浪费是 web_search 超时后原样重试
    /// （连续两次 20 秒、两步），而模型并不知道该换成 web_fetch；这类“已知失败模式 → 可执行动作”
    /// 的映射放在循环里统一生成，拼在工具结果之后交给模型，不改写工具自己的错误正文。
    /// </summary>
    public static class ToolFailureGuidance
    {
        /// <summary>
        /// 建议在工具结果正文里的起始标记。循环按它拼接，界面按它裁掉——
        /// 建议是给模型的调度提示，不属于工具输出，工具卡只显示工具自己的话。
        /// </summary>
        public const string Marker = "\n\n[下一步建议]";

        /// <summary>
        /// 去掉正文里的建议块，用于工具卡与导出预览；没有标记时原样返回。
        /// </summary>
        public static string Strip(string content)
        {
            if (string.IsNullOrEmpty(content))
            {
                return content ?? string.Empty;
            }

            int index = content.IndexOf(Marker, StringComparison.Ordinal);
            return index < 0 ? content : content.Substring(0, index);
        }

        /// <summary>
        /// 生成建议；成功结果或没有匹配规则时返回 <c>null</c>。
        /// <paramref name="consecutiveFailures"/> ≥ 2 时追加“停止原样重试”的提醒。
        /// </summary>
        public static string For(ToolResult result, int consecutiveFailures)
        {
            if (result == null || result.Status == ToolResultStatus.Succeeded)
            {
                return null;
            }

            string hint = ByStatus(result) ?? ByToolOrCode(result);

            if (consecutiveFailures >= 2)
            {
                string repeat = "同一调用已连续失败 " + consecutiveFailures
                    + " 次；停止原样重试，换工具、换参数，或向用户说明卡在哪里。";
                hint = string.IsNullOrEmpty(hint) ? repeat : hint + " " + repeat;
            }

            return hint;
        }

        private static string ByStatus(ToolResult result)
        {
            switch (result.Status)
            {
                case ToolResultStatus.Denied:
                    return "用户拒绝了这次调用；不要用同样参数重试，先说明影响或换一种做法。";

                case ToolResultStatus.Blocked:
                    return "安全策略会拦截同样的调用；换一种做法，或让用户自己执行。";

                case ToolResultStatus.InvalidArguments:
                    return "参数不符合工具声明；按参数表重写调用，不要带多余字段。";

                case ToolResultStatus.Cancelled:
                    return "这次调用被取消或超时；确实还需要时重新发起一次，不要原样连发。";

                default:
                    return null;
            }
        }

        private static string ByToolOrCode(ToolResult result)
        {
            string tool = result.ToolName ?? string.Empty;
            string code = result.ErrorCode ?? string.Empty;

            if (Same(tool, ToolNames.WebSearch))
            {
                return code == "timeout" || code == "network_error"
                    ? "web_search 后端不可达时改用 web_fetch 直接抓已知 URL，或换更短的关键词；不要用相同查询重试。"
                    : "web_search 失败时改用 web_fetch 抓具体地址。";
            }

            if (Same(tool, ToolNames.WebFetch))
            {
                return code == "timeout"
                    ? "抓取超时；换一个来源或稍后再试，不要原样重试同一地址。"
                    : "抓取失败；换一个来源，或先用 web_search 找别的地址。";
            }

            if (code == "not_found")
            {
                return "路径不存在；先用 glob 或 read 上级目录确认实际路径，再重试。";
            }

            if (code == "file_changed")
            {
                return "文件在审批后被外部修改；重新 read 拿到最新内容再改。";
            }

            if (code == "tool_call_budget_exceeded")
            {
                return "本步工具调用超过上限；把调用拆到下一步，或合并成一次。";
            }

            if (code == "tool_timeout")
            {
                return "工具超时；缩小范围（更短命令、更少行数）后重试，不要原样重复。";
            }

            if (code == "unknown_tool")
            {
                return "工具名不在当前清单里；只能用系统提示 <tools> 里列出的工具。";
            }

            if (code == "risk_blocked")
            {
                return "该命令被风险分级拦截；缩小操作范围，或让用户自己执行。";
            }

            return null;
        }

        private static bool Same(string left, string right)
        {
            return string.Equals(left, right, StringComparison.Ordinal);
        }
    }
}
