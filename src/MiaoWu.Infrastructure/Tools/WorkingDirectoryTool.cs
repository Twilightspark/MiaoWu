using System;
using System.Threading;
using System.Threading.Tasks;
using MiaoWu.Core.Agents.Tools;

namespace MiaoWu.Infrastructure.Tools
{
    /// <summary>
    /// working_directory：读取或切换会话工作目录。命令默认在它下面执行；
    /// 文件类工具（read/write/edit/glob/grep）仍以工作区根为基准解析相对路径。
    /// 路径必须落在工作区内且真实存在。
    /// </summary>
    public sealed class WorkingDirectoryTool : IAgentTool
    {
        private const int MaxPathLength = 400;

        public string Name
        {
            get { return ToolNames.WorkingDirectory; }
        }

        public string Description
        {
            get { return "读取或切换会话工作目录（pwsh 默认在此执行）；省略 path 时返回当前目录。"; }
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
                if (context == null || context.Sandbox == null || context.WorkingDirectory == null)
                {
                    return Task.FromResult(ToolResult.Failure(
                        call,
                        ToolResultStatus.Failed,
                        "working_directory_unavailable",
                        "当前会话没有工作目录上下文。"));
                }

                ToolArguments arguments = ToolArguments.Parse(call == null ? null : call.ArgumentsJson);
                string path = arguments.GetOptionalString("path", null, MaxPathLength);

                if (string.IsNullOrWhiteSpace(path))
                {
                    return Task.FromResult(DescribeCurrent(call, context));
                }

                PathSandboxResult resolved = context.Sandbox.Resolve(
                    path,
                    PathAccessKind.Directory,
                    false);
                if (!resolved.IsAllowed)
                {
                    return Task.FromResult(ToolResult.Failure(
                        call,
                        ToolResultStatus.InvalidArguments,
                        "path_invalid",
                        resolved.Detail));
                }

                if (!context.WorkingDirectory.TrySet(resolved.NormalizedPath))
                {
                    return Task.FromResult(ToolResult.Failure(
                        call,
                        ToolResultStatus.Failed,
                        "working_directory_failed",
                        "目录不存在或不在工作区内：" + path + "。"));
                }

                ToolResult result = DescribeCurrent(call, context);
                result.Summary = "切换工作目录：" + path;
                return Task.FromResult(result);
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
                    "working_directory_failed",
                    "读取工作目录失败：" + exception.Message));
            }
        }

        private static ToolResult DescribeCurrent(ToolCall call, ToolContext context)
        {
            string current = context.WorkingDirectory.Current ?? string.Empty;
            string display = current;
            if (context.Sandbox != null && !string.IsNullOrWhiteSpace(current))
            {
                PathSandboxResult resolved = context.Sandbox.Resolve(
                    current,
                    PathAccessKind.Directory,
                    true);
                if (resolved.IsAllowed)
                {
                    display = resolved.RelativePath.Length == 0 ? "." : resolved.RelativePath;
                }
            }

            return ToolResult.Success(
                call,
                "当前工作目录：" + display + "（绝对路径 " + current + "）",
                "工作目录 " + display,
                false,
                false);
        }
    }
}
