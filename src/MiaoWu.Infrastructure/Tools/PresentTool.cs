using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MiaoWu.Core.Agents.Tools;
using MiaoWu.Infrastructure.Json;

namespace MiaoWu.Infrastructure.Tools
{
    /// <summary>
    /// present：声明本次交付的文件。只做校验与汇总（路径必须存在于工作区内），
    /// 供工具卡与后续导出使用；不写文件、不改状态。
    /// </summary>
    public sealed class PresentTool : IAgentTool
    {
        private const int MaxItems = 32;
        private const int MaxPathLength = 400;
        private const int MaxDescriptionLength = 200;

        public string Name
        {
            get { return ToolNames.Present; }
        }

        public string Description
        {
            get { return "声明本次交付的文件（path + 可选 description），必须都在工作区内且真实存在。"; }
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
            if (context == null || context.Sandbox == null)
            {
                return Task.FromResult(ToolResult.Failure(
                    call,
                    ToolResultStatus.Failed,
                    "present_unavailable",
                    "当前会话没有工作区上下文。"));
            }

            try
            {
                ToolArguments arguments = ToolArguments.Parse(call == null ? null : call.ArgumentsJson);
                IReadOnlyList<JsonValue> files = arguments.GetOptionalArray("files");
                if (files == null || files.Count == 0)
                {
                    throw new ToolArgumentException("invalid_arguments", "files 不能为空。");
                }

                if (files.Count > MaxItems)
                {
                    throw new ToolArgumentException(
                        "invalid_arguments",
                        "一次最多声明 " + MaxItems + " 个交付文件。");
                }

                var lines = new List<string>();
                var summaries = new List<string>();
                foreach (JsonValue item in files)
                {
                    string path = ReadString(item, "path");
                    if (string.IsNullOrWhiteSpace(path))
                    {
                        throw new ToolArgumentException("invalid_arguments", "每个交付项都要有 path。");
                    }

                    PathSandboxResult resolved = context.Sandbox.Resolve(
                        path,
                        PathAccessKind.File,
                        false);
                    if (!resolved.IsAllowed || !File.Exists(resolved.NormalizedPath))
                    {
                        return Task.FromResult(ToolResult.Failure(
                            call,
                            ToolResultStatus.InvalidArguments,
                            "path_invalid",
                            "交付文件不存在或不在工作区内：" + path + "。"));
                    }

                    string description = ReadString(item, "description");
                    string relative = resolved.RelativePath.Replace('\\', '/');
                    string line = "- `" + relative + "`";
                    if (!string.IsNullOrWhiteSpace(description))
                    {
                        string trimmed = description.Trim();
                        if (trimmed.Length > MaxDescriptionLength)
                        {
                            trimmed = trimmed.Substring(0, MaxDescriptionLength) + "…";
                        }

                        line += " — " + trimmed;
                    }

                    lines.Add(line);
                    summaries.Add(relative);
                }

                var builder = new StringBuilder();
                builder.AppendLine("已声明 " + lines.Count + " 个交付文件：");
                foreach (string line in lines)
                {
                    builder.AppendLine(line);
                }

                return Task.FromResult(ToolResult.Success(
                    call,
                    builder.ToString().TrimEnd(),
                    "交付 " + string.Join("、", summaries),
                    false,
                    false));
            }
            catch (ToolArgumentException exception)
            {
                return Task.FromResult(ToolResult.Failure(
                    call,
                    ToolResultStatus.InvalidArguments,
                    exception.Code,
                    exception.Message));
            }
        }

        private static string ReadString(JsonValue item, string name)
        {
            if (item == null)
            {
                return null;
            }

            JsonValue value = item.Get(name);
            return value == null ? null : value.AsString(null);
        }
    }
}
