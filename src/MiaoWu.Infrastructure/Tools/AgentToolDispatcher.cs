using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using MiaoWu.Core.Abstractions;
using MiaoWu.Core.Agents.Tools;

namespace MiaoWu.Infrastructure.Tools
{
    /// <summary>
    /// 工具调度管线：查表 → 风险分级 → 审批 → 执行 → 审计。
    /// 所有失败都转成结构化结果，不向会话循环抛业务异常。
    /// </summary>
    public sealed class AgentToolDispatcher
    {
        private const int MaxArgumentsAuditLength = 400;
        private const int MaxTargetAuditLength = 300;

        private readonly IToolRegistry _registry;
        private readonly IToolApprovalService _approvals;
        private readonly IToolAuditLog _auditLog;
        private readonly IClock _clock;

        public AgentToolDispatcher(
            IToolRegistry registry,
            IToolApprovalService approvals,
            IToolAuditLog auditLog,
            IClock clock)
        {
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
            _approvals = approvals ?? throw new ArgumentNullException(nameof(approvals));
            _auditLog = auditLog ?? NullToolAuditLog.Instance;
            _clock = clock ?? SystemClock.Instance;
        }

        public async Task<ToolExecution> ExecuteAsync(
            ToolCall call,
            ToolContext context,
            CancellationToken cancellationToken)
        {
            var execution = new ToolExecution
            {
                Call = call ?? new ToolCall { Name = string.Empty },
                Risk = ToolRisk.Medium,
                Decision = ToolExecutionDecision.Invalid
            };

            if (call == null || string.IsNullOrWhiteSpace(call.Name))
            {
                execution.Result = ToolResult.Failure(
                    call,
                    ToolResultStatus.InvalidArguments,
                    "missing_tool_name",
                    "模型没有给出工具名。");
                RecordAudit(execution, context, null);
                return execution;
            }

            if (context == null)
            {
                execution.Result = ToolResult.Failure(
                    call,
                    ToolResultStatus.InvalidArguments,
                    "missing_context",
                    "工具执行缺少工作区上下文。");
                return execution;
            }

            IAgentTool tool;
            if (!_registry.TryGet(call.Name, out tool) || tool == null)
            {
                execution.Risk = ToolRisk.Medium;
                execution.Result = ToolResult.Failure(
                    call,
                    ToolResultStatus.Failed,
                    "unknown_tool",
                    "未知工具：" + call.Name + "。可用工具：" + DescribeRegisteredTools() + "。");
                RecordAudit(execution, context, null);
                return execution;
            }

            ToolApprovalPreview preview = null;
            try
            {
                execution.Risk = EvaluateRisk(tool, call, context, out string riskReason);
                execution.RiskReason = riskReason;

                if (execution.Risk == ToolRisk.Blocked)
                {
                    execution.Decision = ToolExecutionDecision.Blocked;
                    execution.Result = ToolResult.Failure(
                        call,
                        ToolResultStatus.Blocked,
                        "risk_blocked",
                        string.IsNullOrWhiteSpace(riskReason) ? "该操作被安全策略拦截。" : riskReason);
                    return execution;
                }

                // DSH 的 never 策略：从不询问，需要审批的动作直接放行；
                // 风险分级为 Blocked 的情况在上一段已经返回，不受策略影响。
                bool policyAllows = context.ApprovalPolicy != ToolApprovalPolicy.Never;
                bool needsApproval = policyAllows
                    && (tool.RequiresApproval || execution.Risk >= ToolRisk.High);
                if (!policyAllows && (tool.RequiresApproval || execution.Risk >= ToolRisk.High))
                {
                    string suffix = "会话审批策略为 never，自动放行。";
                    execution.RiskReason = string.IsNullOrEmpty(execution.RiskReason)
                        ? suffix
                        : execution.RiskReason + " " + suffix;
                }

                if (needsApproval)
                {
                    try
                    {
                        preview = tool.Describe(call, context);
                    }
                    catch (ToolArgumentException exception)
                    {
                        execution.Decision = ToolExecutionDecision.Invalid;
                        execution.Result = ToolResult.Failure(
                            call,
                            ToolResultStatus.InvalidArguments,
                            exception.Code,
                            exception.Message);
                        return execution;
                    }
                    catch (ToolPreparationException exception)
                    {
                        execution.Decision = ToolExecutionDecision.Invalid;
                        execution.Result = ToolResult.Failure(
                            call,
                            exception.Status,
                            exception.Code,
                            exception.Message);
                        return execution;
                    }
                    catch (Exception)
                    {
                        // 描述失败不阻断主流程；审批仍会显示工具名与参数摘要。
                    }

                    ApprovalOutcome approval = await RequestApprovalAsync(
                        call,
                        context,
                        tool,
                        execution.Risk,
                        preview,
                        cancellationToken).ConfigureAwait(false);

                    execution.Decision = approval.Decision;

                    if (approval.Result != null)
                    {
                        // 审批被拒绝或取消：不再执行工具。
                        execution.Result = approval.Result;
                        return execution;
                    }
                }
                else
                {
                    execution.Decision = ToolExecutionDecision.Auto;
                }

                // 工具在 Describe 阶段可能记录了读后写校验状态（文件哈希/mtime）。
                context.ApprovalPreview = preview;

                execution.Result = await RunToolAsync(tool, call, context, cancellationToken)
                    .ConfigureAwait(false);
                return execution;
            }
            finally
            {
                if (execution.Result == null)
                {
                    execution.Result = ToolResult.Failure(
                        call,
                        ToolResultStatus.Failed,
                        "tool_crashed",
                        "工具执行中断。");
                }

                RecordAudit(execution, context, preview);
            }
        }

        private static ToolRisk EvaluateRisk(
            IAgentTool tool,
            ToolCall call,
            ToolContext context,
            out string reason)
        {
            if (tool is IToolRiskEvaluator evaluator)
            {
                ToolRiskAssessment assessment = evaluator.AssessRisk(call, context);
                reason = assessment == null ? string.Empty : assessment.Reason;
                return assessment == null ? tool.Risk : assessment.Risk;
            }

            reason = tool.Risk == ToolRisk.Low ? "只读工具，自动放行。" : string.Empty;
            return tool.Risk;
        }

        private async Task<ApprovalOutcome> RequestApprovalAsync(
            ToolCall call,
            ToolContext context,
            IAgentTool tool,
            ToolRisk risk,
            ToolApprovalPreview preview,
            CancellationToken cancellationToken)
        {
            var outcome = new ApprovalOutcome { Decision = ToolExecutionDecision.Cancelled };

            if (string.IsNullOrWhiteSpace(context.SessionId))
            {
                outcome.Result = ToolResult.Failure(
                    call,
                    ToolResultStatus.Failed,
                    "missing_session",
                    "需要审批的工具必须在会话内执行。");
                return outcome;
            }

            var request = new ToolApprovalRequest
            {
                RequestId = string.IsNullOrWhiteSpace(call.Id)
                    ? Guid.NewGuid().ToString("N")
                    : call.Id,
                SessionId = context.SessionId,
                ToolName = tool.Name,
                Risk = risk,
                Title = preview != null && !string.IsNullOrWhiteSpace(preview.Title)
                    ? preview.Title
                    : "需要审批",
                Target = preview == null ? string.Empty : preview.Target,
                Summary = preview == null ? string.Empty : preview.Summary,
                CanApproveForSession = preview != null && preview.CanApproveForSession,
                CanShowDiff = preview != null && preview.CanShowDiff,
                Diff = preview == null ? null : preview.Diff,
                ScopeKey = preview == null ? null : preview.ScopeKey,
                CreatedAt = _clock.UtcNow
            };

            ToolApprovalResult approval = await _approvals
                .RequestAsync(request, cancellationToken)
                .ConfigureAwait(false);

            if (approval.Status == ToolApprovalStatus.Approved)
            {
                outcome.Decision = approval.ApprovedBySessionGrant
                    || approval.Outcome == ToolApprovalOutcome.ApproveForSession
                    ? ToolExecutionDecision.ApprovedForSession
                    : ToolExecutionDecision.ApprovedOnce;

                return outcome;
            }

            if (approval.Status == ToolApprovalStatus.Denied)
            {
                outcome.Decision = ToolExecutionDecision.Denied;
                string reason = approval.Ticket == null ? null : approval.Ticket.DenyReason;
                string message = string.IsNullOrWhiteSpace(reason)
                    ? "用户拒绝了这次操作。"
                    : "用户拒绝了这次操作： " + reason;

                outcome.Result = ToolResult.Failure(
                    call,
                    ToolResultStatus.Denied,
                    "approval_denied",
                    message);
                return outcome;
            }

            outcome.Decision = ToolExecutionDecision.Cancelled;
            outcome.Result = ToolResult.Failure(
                call,
                ToolResultStatus.Cancelled,
                "approval_cancelled",
                "审批已取消。");
            return outcome;
        }

        private sealed class ApprovalOutcome
        {
            public ToolExecutionDecision Decision { get; set; }

            /// <summary>null 表示审批通过，可以继续执行工具。</summary>
            public ToolResult Result { get; set; }
        }

        private static async Task<ToolResult> RunToolAsync(
            IAgentTool tool,
            ToolCall call,
            ToolContext context,
            CancellationToken cancellationToken)
        {
            var stopwatch = Stopwatch.StartNew();
            ToolResult result;

            try
            {
                result = await tool.ExecuteAsync(call, context, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                result = ToolResult.Failure(
                    call,
                    ToolResultStatus.Cancelled,
                    "cancelled",
                    "操作已取消。");
            }
            catch (Exception exception)
            {
                // 工具内部的 bug 不能把堆栈带到 UI；保留一句可读信息。
                result = ToolResult.Failure(
                    call,
                    ToolResultStatus.Failed,
                    "tool_exception",
                    "工具执行失败：" + Shorten(exception.Message, 200));
            }
            finally
            {
                stopwatch.Stop();
            }

            if (result == null)
            {
                result = ToolResult.Failure(
                    call,
                    ToolResultStatus.Failed,
                    "empty_result",
                    "工具没有返回结果。");
            }

            result.DurationMilliseconds = stopwatch.ElapsedMilliseconds;
            if (string.IsNullOrEmpty(result.ToolName))
            {
                result.ToolName = tool.Name;
            }

            if (string.IsNullOrEmpty(result.CallId))
            {
                result.CallId = call.Id;
            }

            if (string.IsNullOrEmpty(result.Summary))
            {
                result.Summary = Shorten(call.ArgumentsJson, 120);
            }

            return result;
        }

        private void RecordAudit(
            ToolExecution execution,
            ToolContext context,
            ToolApprovalPreview preview)
        {
            if (execution == null || execution.Result == null || context == null)
            {
                return;
            }

            string target = preview == null ? null : preview.Target;
            if (string.IsNullOrWhiteSpace(target))
            {
                target = execution.Result.Summary;
            }

            string argumentsSummary = null;
            var redactor = execution.Call == null ? null : FindToolRedactor(execution.Call.Name);
            if (redactor != null)
            {
                try
                {
                    argumentsSummary = redactor.GetAuditArguments(execution.Call, context);
                }
                catch (Exception)
                {
                    argumentsSummary = null;
                }
            }

            if (string.IsNullOrEmpty(argumentsSummary) && execution.Call != null)
            {
                argumentsSummary = execution.Call.ArgumentsJson;
            }

            var entry = new ToolAuditEntry
            {
                Timestamp = _clock.UtcNow,
                SessionId = context.SessionId,
                WorkspaceId = context.WorkspaceId,
                ToolName = execution.Call == null ? null : execution.Call.Name,
                Target = ToolAuditRedaction.RedactOneLine(target, MaxTargetAuditLength),
                ArgumentsSummary = ToolAuditRedaction.RedactOneLine(
                    argumentsSummary,
                    MaxArgumentsAuditLength),
                Risk = execution.Risk,
                Decision = execution.GetAuditDecision(),
                Status = execution.Result.Status,
                DurationMilliseconds = execution.Result.DurationMilliseconds,
                ErrorCode = execution.Result.ErrorCode,
                Truncated = execution.Result.Truncated
            };

            try
            {
                _auditLog.Write(entry);
            }
            catch (Exception)
            {
                // 审计写入失败不能反过来破坏工具执行或会话循环。
            }
        }

        private IToolAuditRedactor FindToolRedactor(string toolName)
        {
            IAgentTool tool;
            if (_registry.TryGet(toolName, out tool))
            {
                return tool as IToolAuditRedactor;
            }

            return null;
        }

        private string DescribeRegisteredTools()
        {
            IReadOnlyList<IAgentTool> tools = _registry.GetAll();
            if (tools == null || tools.Count == 0)
            {
                return "（无）";
            }

            var names = new List<string>(tools.Count);
            for (int i = 0; i < tools.Count; i++)
            {
                names.Add(tools[i].Name);
            }

            return string.Join("、", names);
        }

        private static string Shorten(string text, int maxLength)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            return text.Length <= maxLength ? text : text.Substring(0, maxLength) + "…";
        }
    }
}
