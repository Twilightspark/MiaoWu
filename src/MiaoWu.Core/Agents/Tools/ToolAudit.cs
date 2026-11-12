using System;
using System.Text.RegularExpressions;

namespace MiaoWu.Core.Agents.Tools
{
    /// <summary>一条工具审计记录。只存摘要，不存消息全文、文件内容或命令输出。</summary>
    public sealed class ToolAuditEntry
    {
        public DateTimeOffset Timestamp { get; set; }

        public string SessionId { get; set; }

        public string WorkspaceId { get; set; }

        public string ToolName { get; set; }

        /// <summary>规范化路径或命令行；写入前必须经过 <see cref="ToolAuditRedaction"/>。</summary>
        public string Target { get; set; }

        /// <summary>参数摘要（已脱敏、已截断）。</summary>
        public string ArgumentsSummary { get; set; }

        public ToolRisk Risk { get; set; }

        /// <summary>auto / approved_once / approved_session / denied / blocked / cancelled / invalid。</summary>
        public string Decision { get; set; }

        public ToolResultStatus Status { get; set; }

        public long DurationMilliseconds { get; set; }

        public string ErrorCode { get; set; }

        public bool Truncated { get; set; }
    }

    /// <summary>审计日志写入接缝。实现必须保证追加写与行完整性，不阻塞调用方线程过久。</summary>
    public interface IToolAuditLog
    {
        void Write(ToolAuditEntry entry);
    }

    /// <summary>不落盘的审计实现，用于测试与未配置数据目录的场景。</summary>
    public sealed class NullToolAuditLog : IToolAuditLog
    {
        public static readonly NullToolAuditLog Instance = new NullToolAuditLog();

        private NullToolAuditLog()
        {
        }

        public void Write(ToolAuditEntry entry)
        {
        }
    }

    /// <summary>
    /// 审计文本脱敏：API Key、Bearer、私钥、URL 凭据与 password= 形式的赋值。
    /// 只做“可识别模式”的遮蔽，不能替代“不要记录敏感内容”的调用约定。
    /// </summary>
    public static class ToolAuditRedaction
    {
        /// <summary>单字段默认长度上限。</summary>
        public const int DefaultMaxLength = 512;

        private static readonly Regex[] Patterns =
        {
            new Regex(
                @"(?i)\b(api[_-]?key|access[_-]?token|auth[_-]?token|token|secret|password|passwd|pwd)\b\s*[=:]\s*[^\s,;'""]+",
                RegexOptions.CultureInvariant),
            new Regex(
                @"(?i)\bbearer\s+[a-z0-9\-._~+/]+=*",
                RegexOptions.CultureInvariant),
            new Regex(
                @"\bsk-[A-Za-z0-9_\-]{8,}",
                RegexOptions.CultureInvariant),
            new Regex(
                @"\bAKIA[0-9A-Z]{16}\b",
                RegexOptions.CultureInvariant),
            new Regex(
                @"://[^/\s:@]+:[^/\s@]+@",
                RegexOptions.CultureInvariant),
            new Regex(
                @"-----BEGIN [A-Z ]*PRIVATE KEY-----[\s\S]*?-----END [A-Z ]*PRIVATE KEY-----",
                RegexOptions.CultureInvariant)
        };

        private static readonly string[] Replacements =
        {
            "$1=***",
            "Bearer ***",
            "sk-***",
            "AKIA***",
            "://***@",
            "[private key]"
        };

        /// <summary>脱敏并压成一行；null 返回空串。</summary>
        public static string RedactOneLine(string text, int maxLength)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            string value = text.Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ');
            for (int i = 0; i < Patterns.Length; i++)
            {
                value = Patterns[i].Replace(value, Replacements[i]);
            }

            int limit = maxLength <= 0 ? DefaultMaxLength : maxLength;
            if (value.Length > limit)
            {
                value = value.Substring(0, limit) + "…";
            }

            return value;
        }

        public static string RedactOneLine(string text)
        {
            return RedactOneLine(text, DefaultMaxLength);
        }

        /// <summary>多行文本（例如 diff）只脱敏，不压行，且限制总长度。</summary>
        public static string RedactBlock(string text, int maxLength)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            string value = text;
            for (int i = 0; i < Patterns.Length; i++)
            {
                value = Patterns[i].Replace(value, Replacements[i]);
            }

            int limit = maxLength <= 0 ? DefaultMaxLength * 8 : maxLength;
            if (value.Length > limit)
            {
                value = value.Substring(0, limit) + "\n…（已截断）";
            }

            return value;
        }
    }
}
