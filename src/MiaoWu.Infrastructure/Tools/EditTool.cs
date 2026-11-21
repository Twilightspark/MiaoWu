using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MiaoWu.Core.Agents.Tools;
using MiaoWu.Core.Text;
using MiaoWu.Infrastructure.Json;

namespace MiaoWu.Infrastructure.Tools
{
    /// <summary>
    /// edit：在文本文件里做字面量替换。old_string 必须唯一匹配（replace_all=true 除外），
    /// 编码检测、备份、审批预览（含统一 diff）、外部修改校验与原子写全部复用 <see cref="WriteTool"/>。
    /// </summary>
    public sealed class EditTool : IAgentTool, IToolAuditRedactor
    {
        private const int MaxPathLength = 400;
        private const int MaxOldStringChars = 200000;
        private const int MaxNewStringChars = 2 * 1024 * 1024;

        private readonly WriteTool _writer;

        public EditTool(WriteTool writer)
        {
            _writer = writer ?? throw new ArgumentNullException(nameof(writer));
        }

        public string Name
        {
            get { return ToolNames.Edit; }
        }

        public string Description
        {
            get
            {
                return "编辑工作区内的文本文件：把 old_string 替换为 new_string。参数：path（相对路径）、"
                    + "old_string（必须唯一匹配）、new_string（可为空）、replace_all（可选，默认 false）。"
                    + "编辑前自动备份，审批后执行；文件在审批后被外部修改会拒绝写入。";
            }
        }

        public ToolRisk Risk
        {
            get { return ToolRisk.High; }
        }

        public bool RequiresApproval
        {
            get { return true; }
        }

        public ToolApprovalPreview Describe(ToolCall call, ToolContext context)
        {
            EditPlan plan = Prepare(call, context);
            ToolApprovalPreview preview = _writer.Describe(plan.WriteCall, context);
            if (preview != null)
            {
                preview.Title = "编辑文件";
            }

            return preview;
        }

        public string GetAuditArguments(ToolCall call, ToolContext context)
        {
            try
            {
                ToolArguments arguments = ToolArguments.Parse(call.ArgumentsJson);
                string path = arguments.GetRequiredString(ToolArgumentNames.Path, MaxPathLength);
                string oldString = arguments.GetRequiredString("old_string", MaxOldStringChars);
                string newString = arguments.GetRequiredStringAllowEmpty("new_string", MaxNewStringChars);
                bool replaceAll = arguments.GetOptionalBool("replace_all", false);
                return "path=" + path
                    + "; old_string=" + oldString.Length.ToString(CultureInfo.InvariantCulture) + " 字符"
                    + "; new_string=" + newString.Length.ToString(CultureInfo.InvariantCulture) + " 字符"
                    + "; replace_all=" + (replaceAll ? "true" : "false");
            }
            catch (ToolArgumentException)
            {
                return "（参数无法解析）";
            }
        }

        public async Task<ToolResult> ExecuteAsync(
            ToolCall call,
            ToolContext context,
            CancellationToken cancellationToken)
        {
            try
            {
                return await Task.Run(
                    () => ExecuteCore(call, context, cancellationToken),
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return ToolResult.Failure(
                    call,
                    ToolResultStatus.Cancelled,
                    "cancelled",
                    "编辑已取消。");
            }
        }

        private ToolResult ExecuteCore(
            ToolCall call,
            ToolContext context,
            CancellationToken cancellationToken)
        {
            EditPlan plan;
            try
            {
                plan = Prepare(call, context);
            }
            catch (ToolArgumentException exception)
            {
                return ToolResultFactory.InvalidArguments(call, exception);
            }
            catch (ToolPreparationException exception)
            {
                return ToolResult.Failure(call, exception.Status, exception.Code, exception.Message);
            }

            cancellationToken.ThrowIfCancellationRequested();

            ToolResult result = _writer
                .ExecuteAsync(plan.WriteCall, context, cancellationToken)
                .GetAwaiter()
                .GetResult();

            if (result != null && result.Status == ToolResultStatus.Succeeded)
            {
                string content = result.Content ?? string.Empty;
                const string WrittenPrefix = "已写入 ";
                if (content.StartsWith(WrittenPrefix, StringComparison.Ordinal))
                {
                    content = "已编辑 " + content.Substring(WrittenPrefix.Length);
                }

                result.Content = content
                    + "\n替换：" + plan.Count.ToString(CultureInfo.InvariantCulture) + " 处";
            }

            return result;
        }

        private static EditPlan Prepare(ToolCall call, ToolContext context)
        {
            ToolArguments arguments;
            string requestedPath;
            string oldString;
            string newString;
            bool replaceAll;
            try
            {
                arguments = ToolArguments.Parse(call.ArgumentsJson);
                requestedPath = arguments.GetRequiredString(ToolArgumentNames.Path, MaxPathLength);
                oldString = arguments.GetRequiredString("old_string", MaxOldStringChars);
                newString = arguments.GetRequiredStringAllowEmpty("new_string", MaxNewStringChars);
                replaceAll = arguments.GetOptionalBool("replace_all", false);
            }
            catch (ToolArgumentException exception)
            {
                throw new ToolPreparationException(
                    exception.Code,
                    exception.Message,
                    ToolResultStatus.InvalidArguments);
            }

            PathSandboxResult resolved = context.Sandbox.Resolve(requestedPath, PathAccessKind.File);
            if (!resolved.IsAllowed)
            {
                throw new ToolPreparationException(
                    ToolResultFactory.ToErrorCode(resolved.Status),
                    resolved.Detail);
            }

            long length;
            try
            {
                length = new FileInfo(resolved.NormalizedPath).Length;
            }
            catch (Exception exception) when (
                exception is IOException || exception is UnauthorizedAccessException)
            {
                throw new ToolPreparationException("read_failed", "无法读取目标文件属性。");
            }

            if (length > ToolText.MaxFileBytes)
            {
                throw new ToolPreparationException(
                    "file_too_large",
                    "目标文件超过 2 MB，edit 不做文本替换。");
            }

            byte[] bytes;
            try
            {
                bytes = WriteTool.ReadAllBytesShared(resolved.NormalizedPath);
            }
            catch (Exception exception) when (
                exception is IOException || exception is UnauthorizedAccessException)
            {
                throw new ToolPreparationException("read_failed", "无法读取目标文件，可能被占用。");
            }

            if (ToolText.LooksBinary(bytes, bytes.Length))
            {
                throw new ToolPreparationException(
                    "binary_file",
                    "目标文件包含二进制内容，edit 只处理文本文件。");
            }

            TextEncodingDetection detection = TextEncodingDetector.Detect(bytes);
            string oldText = bytes.Length == 0
                ? string.Empty
                : detection.Encoding.GetString(
                    bytes,
                    detection.BomLength,
                    Math.Max(0, bytes.Length - detection.BomLength));

            string haystack = NormalizeNewLines(oldText);
            string needle = NormalizeNewLines(oldString);
            string replacement = NormalizeNewLines(newString);

            if (needle.Length == 0)
            {
                throw new ToolPreparationException(
                    "empty_old_string",
                    "old_string 不能为空。",
                    ToolResultStatus.InvalidArguments);
            }

            int count = CountOccurrences(haystack, needle);
            if (count == 0)
            {
                throw new ToolPreparationException(
                    "old_string_not_found",
                    "未在文件中找到 old_string；请先用 read 确认原文。");
            }

            if (count > 1 && !replaceAll)
            {
                throw new ToolPreparationException(
                    "old_string_not_unique",
                    "old_string 在文件中出现 "
                        + count.ToString(CultureInfo.InvariantCulture)
                        + " 次，无法唯一确定替换位置；请加长上下文，或设置 replace_all=true。");
            }

            string newText = replaceAll
                ? ReplaceAll(haystack, needle, replacement)
                : ReplaceFirst(haystack, needle, replacement);

            var writeArguments = JsonValue.NewObject();
            writeArguments.Add(ToolArgumentNames.Path, JsonValue.From(requestedPath));
            writeArguments.Add("content", JsonValue.From(newText));

            return new EditPlan
            {
                Count = count,
                WriteCall = new ToolCall
                {
                    Id = call == null ? null : call.Id,
                    Name = ToolNames.Write,
                    ArgumentsJson = JsonWriter.Write(writeArguments)
                }
            };
        }

        private static string NormalizeNewLines(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            return text.Replace("\r\n", "\n").Replace('\r', '\n');
        }

        private static int CountOccurrences(string haystack, string needle)
        {
            int count = 0;
            int index = 0;
            while (index <= haystack.Length - needle.Length)
            {
                int hit = haystack.IndexOf(needle, index, StringComparison.Ordinal);
                if (hit < 0)
                {
                    break;
                }

                count++;
                index = hit + needle.Length;
            }

            return count;
        }

        private static string ReplaceFirst(string haystack, string needle, string replacement)
        {
            int hit = haystack.IndexOf(needle, StringComparison.Ordinal);
            return hit < 0
                ? haystack
                : haystack.Substring(0, hit) + replacement + haystack.Substring(hit + needle.Length);
        }

        private static string ReplaceAll(string haystack, string needle, string replacement)
        {
            var builder = new StringBuilder(haystack.Length);
            int index = 0;
            while (true)
            {
                int hit = haystack.IndexOf(needle, index, StringComparison.Ordinal);
                if (hit < 0)
                {
                    builder.Append(haystack, index, haystack.Length - index);
                    break;
                }

                builder.Append(haystack, index, hit - index);
                builder.Append(replacement);
                index = hit + needle.Length;
            }

            return builder.ToString();
        }

        private sealed class EditPlan
        {
            public int Count;
            public ToolCall WriteCall;
        }
    }
}
