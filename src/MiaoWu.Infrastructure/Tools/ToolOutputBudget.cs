using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MiaoWu.Core.Abstractions;
using MiaoWu.Core.Agents.Tools;

namespace MiaoWu.Infrastructure.Tools
{
    /// <summary>
    /// 统一工具输出预算：2000 行 / 50 KB；命令取尾部，其余取头部。
    /// 溢出正文写到 &lt;workspace&gt;\.agent\cache\tool-output。
    /// </summary>
    public static class ToolOutputBudget
    {
        public const int MaxLines = 2000;
        public const int MaxBytes = 50 * 1024;
        private const int MaxSpillBytes = 16 * 1024 * 1024;

        public static ToolOutputTruncation Apply(string text, bool tail, string fullOutputPath)
        {
            var result = new ToolOutputTruncation
            {
                Content = text ?? string.Empty,
                TotalLines = CountLines(text),
                TotalBytes = Encoding.UTF8.GetByteCount(text ?? string.Empty),
                FullOutputPath = fullOutputPath
            };

            if (string.IsNullOrEmpty(text))
            {
                return result;
            }

            string[] lines = SplitLines(text);
            var builder = new StringBuilder();
            int outputLines = 0;

            if (tail)
            {
                int start = Math.Max(0, lines.Length - MaxLines);
                for (int i = start; i < lines.Length; i++)
                {
                    if (Encoding.UTF8.GetByteCount(builder.ToString()) + Encoding.UTF8.GetByteCount(lines[i]) > MaxBytes)
                    {
                        break;
                    }

                    builder.Append(lines[i]);
                    if (i < lines.Length - 1)
                    {
                        builder.Append('\n');
                    }

                    outputLines++;
                }
            }
            else
            {
                for (int i = 0; i < lines.Length; i++)
                {
                    if (outputLines >= MaxLines
                        || Encoding.UTF8.GetByteCount(builder.ToString()) + Encoding.UTF8.GetByteCount(lines[i]) > MaxBytes)
                    {
                        break;
                    }

                    builder.Append(lines[i]);
                    if (i < lines.Length - 1)
                    {
                        builder.Append('\n');
                    }

                    outputLines++;
                }
            }

            result.Content = builder.ToString();
            result.OutputLines = outputLines;
            result.OutputBytes = Encoding.UTF8.GetByteCount(result.Content);
            result.Truncated = outputLines < lines.Length;
            if (result.Truncated)
            {
                result.TruncatedBy = result.OutputBytes >= MaxBytes ? "bytes" : "lines";
            }

            return result;
        }

        /// <summary>把完整输出写到工作区缓存，返回绝对路径；失败返回 null。</summary>
        public static string Save(
            string workspaceRoot,
            string sessionId,
            string callId,
            string content)
        {
            if (string.IsNullOrWhiteSpace(workspaceRoot)
                || string.IsNullOrWhiteSpace(sessionId)
                || string.IsNullOrWhiteSpace(callId)
                || string.IsNullOrEmpty(content))
            {
                return null;
            }

            try
            {
                string safeSession = Sanitize(sessionId);
                string safeCall = Sanitize(callId);
                string directory = Path.Combine(
                    workspaceRoot,
                    ".agent",
                    "cache",
                    "tool-output",
                    safeSession);

                Directory.CreateDirectory(directory);
                string path = Path.Combine(directory, safeCall + ".txt");
                string text = content;
                if (Encoding.UTF8.GetByteCount(text) > MaxSpillBytes)
                {
                    text = KeepHeadAndTail(text, MaxSpillBytes);
                }

                File.WriteAllText(path, text, new UTF8Encoding(false));
                return path;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static string KeepHeadAndTail(string text, int maxBytes)
        {
            int half = Math.Max(1024, maxBytes / 2);
            int headChars = Math.Min(text.Length, half / 2);
            int tailChars = Math.Min(text.Length - headChars, half / 2);
            if (headChars + tailChars >= text.Length)
            {
                return text;
            }

            return text.Substring(0, headChars)
                + "\n\n…（中间内容过长，已省略）…\n\n"
                + text.Substring(text.Length - tailChars);
        }

        private static string[] SplitLines(string text)
        {
            return (text ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        }

        private static int CountLines(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return 0;
            }

            int count = 1;
            foreach (char c in text)
            {
                if (c == '\n')
                {
                    count++;
                }
            }

            return count;
        }

        private static string Sanitize(string value)
        {
            var builder = new StringBuilder();
            foreach (char c in value)
            {
                builder.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_');
                if (builder.Length >= 64)
                {
                    break;
                }
            }

            return builder.Length == 0 ? "x" : builder.ToString();
        }
    }

    /// <summary>把 AgentToolDispatcher 适配成 Core 的 IToolExecutor，并套统一输出预算。</summary>
    public sealed class AgentToolDispatcherAdapter : IToolExecutor
    {
        private readonly AgentToolDispatcher _dispatcher;

        public AgentToolDispatcherAdapter(AgentToolDispatcher dispatcher)
        {
            _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        }

        public async Task<ToolExecution> ExecuteAsync(
            ToolCall call,
            ToolContext context,
            CancellationToken cancellationToken)
        {
            ToolExecution execution = await _dispatcher
                .ExecuteAsync(call, context, cancellationToken)
                .ConfigureAwait(false);

            if (execution == null || execution.Result == null)
            {
                return execution;
            }

            ToolResult result = execution.Result;
            string content = result.Content ?? string.Empty;
            if (content.Length == 0)
            {
                return execution;
            }

            bool tail = call != null && string.Equals(call.Name, ToolNames.RunCommand, StringComparison.OrdinalIgnoreCase);
            ToolOutputTruncation truncation = ToolOutputBudget.Apply(content, tail, null);

            if (truncation.Truncated && result.IsSuccess && context != null)
            {
                string path = ToolOutputBudget.Save(
                    context.WorkspaceRoot,
                    context.SessionId,
                    context.CallId,
                    content);

                if (!string.IsNullOrEmpty(path))
                {
                    truncation.FullOutputPath = path;
                    result.FullOutputPath = path;
                }
            }

            if (truncation.Truncated)
            {
                result.Content = truncation.Content
                    + "\n\n[输出已截断"
                    + (string.IsNullOrEmpty(truncation.FullOutputPath)
                        ? "；完整内容无法写入缓存]"
                        : "；完整输出：" + truncation.FullOutputPath + "]");
                result.Truncated = true;
                result.Truncation = truncation;
            }

            return execution;
        }
    }
}
