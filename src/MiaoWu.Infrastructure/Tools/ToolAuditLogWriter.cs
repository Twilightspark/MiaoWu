using System;
using System.Globalization;
using System.IO;
using System.Text;
using MiaoWu.Core.Abstractions;
using MiaoWu.Core.Agents.Tools;
using MiaoWu.Infrastructure.Json;

namespace MiaoWu.Infrastructure.Tools
{
    /// <summary>
    /// 按天滚动的工具审计日志：<c>logs/audit-yyyyMMdd.jsonl</c>，每行一个 JSON 对象。
    /// </summary>
    /// <remarks>
    /// <para>只追加、不重写；每次写入后 <c>Flush(true)</c>，异常退出最多丢最后一条。</para>
    /// <para>写盘失败只会被吞掉，不能反过来打断工具执行；磁盘满时由用户从日志缺失发现。</para>
    /// </remarks>
    public sealed class ToolAuditLogWriter : IToolAuditLog
    {
        private const string FilePrefix = "audit-";
        private const string FileExtension = ".jsonl";

        private readonly object _gate = new object();
        private readonly string _logsDirectory;

        public ToolAuditLogWriter(IAppPaths appPaths)
            : this(appPaths == null ? null : appPaths.LogsDirectory)
        {
        }

        public ToolAuditLogWriter(string logsDirectory)
        {
            if (string.IsNullOrWhiteSpace(logsDirectory))
            {
                throw new ArgumentException("审计日志目录不能为空。", nameof(logsDirectory));
            }

            _logsDirectory = Path.GetFullPath(logsDirectory);
        }

        public string LogsDirectory
        {
            get { return _logsDirectory; }
        }

        public void Write(ToolAuditEntry entry)
        {
            if (entry == null)
            {
                return;
            }

            string path = GetFilePath(entry.Timestamp);
            string line = JsonWriter.Write(ToJson(entry));

            lock (_gate)
            {
                try
                {
                    Directory.CreateDirectory(_logsDirectory);
                    var bytes = new UTF8Encoding(false).GetBytes(line + "\n");
                    using (var stream = new FileStream(
                        path,
                        FileMode.Append,
                        FileAccess.Write,
                        FileShare.Read))
                    {
                        stream.Write(bytes, 0, bytes.Length);
                        stream.Flush(true);
                    }
                }
                catch (Exception exception) when (
                    exception is IOException
                    || exception is UnauthorizedAccessException
                    || exception is NotSupportedException)
                {
                    // 审计失败不阻断工具执行；缺失会在导出/查看时暴露。
                }
            }
        }

        /// <summary>某天（按本地时间）的日志文件路径。</summary>
        public string GetFilePath(DateTimeOffset timestamp)
        {
            return Path.Combine(
                _logsDirectory,
                FilePrefix
                    + timestamp.ToLocalTime().ToString("yyyyMMdd", CultureInfo.InvariantCulture)
                    + FileExtension);
        }

        private static JsonValue ToJson(ToolAuditEntry entry)
        {
            var root = JsonValue.NewObject();
            root.Add("ts", JsonValue.From(entry.Timestamp.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture)));
            root.Add("sessionId", JsonValue.From(entry.SessionId));
            root.Add("workspaceId", JsonValue.From(entry.WorkspaceId));
            root.Add("tool", JsonValue.From(entry.ToolName));
            root.Add("risk", JsonValue.From(RiskName(entry.Risk)));
            root.Add("decision", JsonValue.From(entry.Decision));
            root.Add("status", JsonValue.From(StatusName(entry.Status)));
            root.Add("durationMs", JsonValue.From((double)entry.DurationMilliseconds));

            if (!string.IsNullOrEmpty(entry.Target))
            {
                root.Add("target", JsonValue.From(entry.Target));
            }

            if (!string.IsNullOrEmpty(entry.ArgumentsSummary))
            {
                root.Add("args", JsonValue.From(entry.ArgumentsSummary));
            }

            if (!string.IsNullOrEmpty(entry.ErrorCode))
            {
                root.Add("errorCode", JsonValue.From(entry.ErrorCode));
            }

            if (entry.Truncated)
            {
                root.Add("truncated", JsonValue.From(true));
            }

            return root;
        }

        public static string RiskName(ToolRisk risk)
        {
            switch (risk)
            {
                case ToolRisk.Low:
                    return "low";
                case ToolRisk.Medium:
                    return "medium";
                case ToolRisk.High:
                    return "high";
                case ToolRisk.Blocked:
                    return "blocked";
                default:
                    return "unknown";
            }
        }

        public static string StatusName(ToolResultStatus status)
        {
            switch (status)
            {
                case ToolResultStatus.Succeeded:
                    return "succeeded";
                case ToolResultStatus.Failed:
                    return "failed";
                case ToolResultStatus.Denied:
                    return "denied";
                case ToolResultStatus.Blocked:
                    return "blocked";
                case ToolResultStatus.Cancelled:
                    return "cancelled";
                case ToolResultStatus.InvalidArguments:
                    return "invalid_arguments";
                default:
                    return "unknown";
            }
        }
    }
}
