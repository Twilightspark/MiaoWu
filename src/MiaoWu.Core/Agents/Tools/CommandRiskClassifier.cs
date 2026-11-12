using System;
using System.Collections.Generic;
using System.Globalization;

namespace MiaoWu.Core.Agents.Tools
{
    /// <summary>一条命令的风险评估结果。</summary>
    public sealed class CommandRiskAssessment
    {
        public CommandRiskAssessment(ToolRisk risk, string reason, string matchedRule)
        {
            Risk = risk;
            Reason = reason ?? string.Empty;
            MatchedRule = matchedRule ?? string.Empty;
        }

        public ToolRisk Risk { get; }

        /// <summary>给用户与审批通知看的中文说明。</summary>
        public string Reason { get; }

        /// <summary>稳定的规则名，便于测试与审计统计。</summary>
        public string MatchedRule { get; }

        public bool IsBlocked
        {
            get { return Risk == ToolRisk.Blocked; }
        }
    }

    /// <summary>
    /// 命令风险分级。策略是“宁可高估”：未知命令默认 Medium，网络类至少 High，
    /// 危险命令直接 Blocked；不存在“始终允许”的低风险命令。
    /// </summary>
    /// <remarks>
    /// 这只是审批前的分级，不替代沙箱：命令本身可以在工作区外读写，
    /// 因此命令工具始终 RequiresApproval，且不提供会话级授权。
    /// </remarks>
    public static class CommandRiskClassifier
    {
        private static readonly HashSet<string> NetworkTokens = new HashSet<string>(StringComparer.Ordinal)
        {
            "curl", "wget", "ssh", "scp", "sftp", "ftp", "tftp", "telnet", "bitsadmin",
            "invoke-webrequest", "iwr", "invoke-restmethod", "irm",
            "test-netconnection", "tnc", "nslookup", "tracert", "ping",
            "netcat", "nc"
        };

        private static readonly HashSet<string> DestructiveTokens = new HashSet<string>(StringComparer.Ordinal)
        {
            "del", "erase", "rd", "rmdir", "rm", "move", "move-item", "remove-item",
            "ren", "rename", "rename-item", "takeown", "icacls", "attrib", "chmod", "chown"
        };

        private static readonly HashSet<string> ElevatedTokens = new HashSet<string>(StringComparer.Ordinal)
        {
            "runas", "sudo"
        };

        private static readonly HashSet<string> DynamicExecutionTokens = new HashSet<string>(StringComparer.Ordinal)
        {
            "iex", "invoke-expression"
        };

        private static readonly HashSet<string> ServiceTokens = new HashSet<string>(StringComparer.Ordinal)
        {
            "sc", "schtasks", "taskkill", "reg", "regedit", "net", "net1"
        };

        private static readonly HashSet<string> PackageTokens = new HashSet<string>(StringComparer.Ordinal)
        {
            "pip", "pip3", "npm", "yarn", "pnpm", "choco", "winget", "scoop", "nuget"
        };

        private static readonly HashSet<string> ReadOnlyTokens = new HashSet<string>(StringComparer.Ordinal)
        {
            "dir", "ls", "type", "cat", "get-childitem", "gci", "get-content", "gc",
            "select-string", "sls", "findstr", "where", "which", "netstat", "ipconfig",
            "whoami", "hostname", "systeminfo", "tasklist", "echo", "pwd", "cd", "tree",
            "head", "tail", "more", "find", "grep", "wc", "measure-object", "sort-object",
            "format-table", "out-string", "get-item", "gi", "resolve-path", "test-path",
            "get-command", "get-help", "get-process", "gps", "get-service", "gsv"
        };

        private static readonly HashSet<string> ReadOnlyGitSubcommands = new HashSet<string>(StringComparer.Ordinal)
        {
            "status", "diff", "log", "show", "branch", "rev-parse", "remote", "tag",
            "describe", "blame", "bisect", "ls-files", "ls-tree", "shortlog", "config"
        };

        private static readonly string[] BlockedRules =
        {
            @"(?:^|[\s&|;(\\/])format(?:\.com|\.exe)?(?:\s|$)",
            @"(?:^|[\s&|;(\\/])diskpart(?:\.exe)?(?:\s|$)",
            @"(?:^|[\s&|;(\\/])bcdedit(?:\.exe)?(?:\s|$)",
            @"vssadmin\s+delete\s+shadows?",
            @"cipher\s+/w",
            @"reg(?:\.exe)?\s+delete\s+""?(?:hklm|hkcr|hku|hkey_local_machine|hkey_classes_root|hkey_users)",
            @"(?:^|[\s&|;(\\/])shutdown(?:\.exe)?(?:\s|$)",
            @"(?:^|[\s&|;(\\/])logoff(?:\.exe)?(?:\s|$)",
            @"net\s+user\s+\S+\s+/delete",
            @"(?:rd|rmdir)\s+/s\s+/q\s+""?[a-z]:[\\/]?\*?""?\s*$",
            @"del\s+/[fsq]+\s+""?[a-z]:[\\/]\*",
            @"remove-item\s+.*[a-z]:[\\/]?\s+-recurse",
            @"schtasks\s+/delete",
            @"sc(?:\.exe)?\s+delete",
            @"wmic\s+.*\bdelete\b"
        };

        private static readonly string[] BlockedRuleNames =
        {
            "format", "diskpart", "bcdedit", "vssadmin_delete_shadows", "cipher_wipe",
            "reg_delete_hive", "shutdown", "logoff", "net_user_delete",
            "rd_system_root", "del_system_root", "remove_item_system_root",
            "schtasks_delete", "sc_delete", "wmic_delete"
        };

        /// <summary>评估一条命令行；null/空白返回 Medium，不抛异常。</summary>
        public static CommandRiskAssessment Classify(string commandLine)
        {
            if (string.IsNullOrWhiteSpace(commandLine))
            {
                return new CommandRiskAssessment(ToolRisk.Medium, "命令为空。", "empty");
            }

            string normalized = commandLine.ToLowerInvariant();

            int blockedIndex = FindBlockedRule(normalized);
            if (blockedIndex >= 0)
            {
                return new CommandRiskAssessment(
                    ToolRisk.Blocked,
                    "命中危险命令黑名单：" + BlockedRuleNames[blockedIndex] + "。",
                    BlockedRuleNames[blockedIndex]);
            }

            ToolRisk worst = ToolRisk.Low;
            string worstReason = "只读命令。";
            string worstRule = "read_only";

            foreach (string segment in SplitSegments(commandLine))
            {
                string trimmed = segment.Trim();
                if (trimmed.Length == 0)
                {
                    continue;
                }

                ToolRisk risk;
                string reason;
                string rule;
                AssessSegment(trimmed, 0, out risk, out reason, out rule);

                if (risk > worst)
                {
                    worst = risk;
                    worstReason = reason;
                    worstRule = rule;
                }
            }

            return new CommandRiskAssessment(worst, worstReason, worstRule);
        }

        private static void AssessSegment(
            string segment,
            int depth,
            out ToolRisk risk,
            out string reason,
            out string rule)
        {
            string normalized = segment.ToLowerInvariant();
            int blockedIndex = FindBlockedRule(normalized);
            if (blockedIndex >= 0)
            {
                risk = ToolRisk.Blocked;
                reason = "命中危险命令黑名单：" + BlockedRuleNames[blockedIndex] + "。";
                rule = BlockedRuleNames[blockedIndex];
                return;
            }

            string innerCommand;
            if (normalized.Contains("-encodedcommand") || normalized.Contains("-enc "))
            {
                risk = ToolRisk.High;
                reason = "使用编码命令，无法审阅实际内容。";
                rule = "encoded_command";
                return;
            }

            if (depth < 2 && TryUnwrapShell(segment, out innerCommand))
            {
                AssessSegment(innerCommand, depth + 1, out risk, out reason, out rule);
                return;
            }

            string first = GetFirstToken(segment);
            List<string> tokens = Tokenize(normalized);

            if (tokens.Contains("install") || tokens.Contains("uninstall"))
            {
                if (PackageTokens.Contains(first) || first == "dotnet" || first == "apt" || first == "apt-get")
                {
                    risk = ToolRisk.High;
                    reason = "安装或卸载软件会改变系统状态。";
                    rule = "package_install";
                    return;
                }
            }

            if (NetworkTokens.Contains(first))
            {
                risk = ToolRisk.High;
                reason = "网络类命令可能把工作区内容发到外部。";
                rule = "network_command";
                return;
            }

            if (ElevatedTokens.Contains(first))
            {
                risk = ToolRisk.High;
                reason = "提权执行会突破当前权限边界。";
                rule = "elevation";
                return;
            }

            if (DynamicExecutionTokens.Contains(first))
            {
                risk = ToolRisk.High;
                reason = "动态执行会把字符串当代码运行，内容不可审阅。";
                rule = "dynamic_execution";
                return;
            }

            if (ServiceTokens.Contains(first))
            {
                risk = ToolRisk.High;
                reason = "系统服务、注册表或进程操作影响面超出工作区。";
                rule = "system_mutation";
                return;
            }

            if (DestructiveTokens.Contains(first))
            {
                risk = ToolRisk.High;
                reason = "删除或移动文件后难以自动撤销。";
                rule = "destructive_file_operation";
                return;
            }

            if (first == "git")
            {
                string subcommand = GetNthToken(segment, 1);
                if (ReadOnlyGitSubcommands.Contains(subcommand))
                {
                    risk = ToolRisk.Low;
                    reason = "git 只读子命令。";
                    rule = "git_read_only";
                    return;
                }

                if (subcommand == "reset" || subcommand == "clean" || subcommand == "push"
                    || subcommand == "checkout" || subcommand == "restore")
                {
                    risk = ToolRisk.High;
                    reason = "git 命令可能丢弃本地改动或改写远端历史。";
                    rule = "git_history_mutation";
                    return;
                }

                risk = ToolRisk.Medium;
                reason = "git 命令可能修改仓库状态。";
                rule = "git_unknown";
                return;
            }

            if (first == "powershell" || first == "pwsh" || first == "cmd" || first == "call")
            {
                risk = ToolRisk.Medium;
                reason = "嵌套 shell 命令按中风险处理。";
                rule = "shell_wrapper";
                return;
            }

            // 重定向可以覆盖任意文件，即使首 token 是只读命令也提升为中风险。
            if (normalized.IndexOf('>') >= 0 || normalized.IndexOf('<') >= 0)
            {
                risk = ToolRisk.Medium;
                reason = "命令包含输入/输出重定向，可能覆盖文件。";
                rule = "redirection";
                return;
            }

            if (ReadOnlyTokens.Contains(first))
            {
                risk = ToolRisk.Low;
                reason = "只读命令。";
                rule = "read_only";
                return;
            }

            risk = ToolRisk.Medium;
            reason = "未知命令按中风险处理。";
            rule = "unknown_command";
        }

        private static bool TryUnwrapShell(string segment, out string inner)
        {
            inner = null;
            string first = GetFirstToken(segment);
            if (first != "cmd" && first != "powershell" && first != "pwsh")
            {
                return false;
            }

            string normalized = segment.Trim();
            string lower = normalized.ToLowerInvariant();

            int commandIndex = lower.IndexOf(" -command ", StringComparison.Ordinal);
            if (commandIndex >= 0)
            {
                inner = normalized.Substring(commandIndex + " -command ".Length);
                return inner.Trim().Length > 0;
            }

            int shortCommandIndex = lower.IndexOf(" -c ", StringComparison.Ordinal);
            if (shortCommandIndex >= 0)
            {
                inner = normalized.Substring(shortCommandIndex + " -c ".Length);
                return inner.Trim().Length > 0;
            }

            int slashCIndex = lower.IndexOf(" /c ", StringComparison.Ordinal);
            if (slashCIndex >= 0)
            {
                inner = normalized.Substring(slashCIndex + " /c ".Length);
                return inner.Trim().Length > 0;
            }

            return false;
        }

        private static int FindBlockedRule(string normalized)
        {
            for (int i = 0; i < BlockedRules.Length; i++)
            {
                if (System.Text.RegularExpressions.Regex.IsMatch(
                    normalized,
                    BlockedRules[i],
                    System.Text.RegularExpressions.RegexOptions.CultureInvariant))
                {
                    return i;
                }
            }

            return -1;
        }

        private static IEnumerable<string> SplitSegments(string commandLine)
        {
            var segments = new List<string>();
            int start = 0;
            for (int i = 0; i < commandLine.Length; i++)
            {
                char current = commandLine[i];
                bool split = current == ';' || current == '\n' || current == '|'
                    || (current == '&' && i + 1 < commandLine.Length && commandLine[i + 1] == '&');

                if (split)
                {
                    segments.Add(commandLine.Substring(start, i - start));
                    if (current == '&')
                    {
                        i++;
                    }

                    start = i + 1;
                }
            }

            segments.Add(commandLine.Substring(start));
            return segments;
        }

        private static List<string> Tokenize(string normalized)
        {
            var tokens = new List<string>(8);
            int index = 0;
            while (index < normalized.Length)
            {
                while (index < normalized.Length && char.IsWhiteSpace(normalized[index]))
                {
                    index++;
                }

                int start = index;
                while (index < normalized.Length
                       && !char.IsWhiteSpace(normalized[index])
                       && normalized[index] != '&'
                       && normalized[index] != '|'
                       && normalized[index] != ';'
                       && normalized[index] != '('
                       && normalized[index] != ')')
                {
                    index++;
                }

                if (index > start)
                {
                    tokens.Add(normalized.Substring(start, index - start).Trim('"', '\''));
                }
                else
                {
                    index++;
                }
            }

            return tokens;
        }

        private static string GetFirstToken(string segment)
        {
            string token = GetNthToken(segment, 0);
            if (token.Length == 0)
            {
                return string.Empty;
            }

            int lastSeparator = token.LastIndexOfAny(new[] { '\\', '/' });
            if (lastSeparator >= 0 && lastSeparator + 1 < token.Length)
            {
                token = token.Substring(lastSeparator + 1);
            }

            int dot = token.LastIndexOf('.');
            if (dot > 0)
            {
                string extension = token.Substring(dot + 1);
                if (extension == "exe" || extension == "com" || extension == "cmd"
                    || extension == "bat" || extension == "ps1")
                {
                    token = token.Substring(0, dot);
                }
            }

            return token.ToLowerInvariant();
        }

        private static string GetNthToken(string segment, int index)
        {
            List<string> tokens = Tokenize(segment.ToLowerInvariant());
            return index >= 0 && index < tokens.Count ? tokens[index] : string.Empty;
        }
    }
}
