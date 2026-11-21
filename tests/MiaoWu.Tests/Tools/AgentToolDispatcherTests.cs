using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MiaoWu.Core.Abstractions;
using MiaoWu.Core.Agents.Tools;
using MiaoWu.Infrastructure.Tools;
using Xunit;

namespace MiaoWu.Tests.Tools
{
    public sealed class AgentToolDispatcherTests
    {
        private const string Root = @"C:\Work\Project";

        private readonly ToolRegistry _registry = new ToolRegistry();
        private readonly ToolApprovalService _approvals = new ToolApprovalService();
        private readonly CapturingAuditLog _audit = new CapturingAuditLog();
        private readonly FakeClock _clock = new FakeClock(new DateTimeOffset(2026, 11, 12, 6, 30, 0, TimeSpan.Zero));

        [Fact]
        public void ReadOnlyToolRunsWithoutApproval()
        {
            var tool = new FakeTool("read", ToolRisk.Low, false);
            _registry.Register(tool);
            AgentToolDispatcher dispatcher = CreateDispatcher();

            ToolExecution execution = Execute(dispatcher, "read");

            Assert.Equal(ToolExecutionDecision.Auto, execution.Decision);
            Assert.True(execution.Result.IsSuccess);
            Assert.Equal(1, tool.ExecuteCount);
            Assert.Equal(0, _approvals.GetPendingCount());
            Assert.Single(_audit.Entries);
            Assert.Equal("auto", _audit.Entries[0].Decision);
            Assert.Equal(ToolResultStatus.Succeeded, _audit.Entries[0].Status);
        }

        [Fact]
        public void HighRiskToolRequiresApprovalEvenIfToolOptsOut()
        {
            var tool = new FakeTool("write", ToolRisk.High, false);
            _registry.Register(tool);
            AgentToolDispatcher dispatcher = CreateDispatcher();

            Task<ToolExecution> task = dispatcher.ExecuteAsync(
                Call("write", "{\"path\":\"a.txt\"}"),
                Context("s1"),
                CancellationToken.None);

            Assert.Equal(1, _approvals.GetPendingCount());
            Assert.True(_approvals.TryResolve("call-1", ToolApprovalOutcome.ApproveOnce, null));

            ToolExecution execution = task.GetAwaiter().GetResult();
            Assert.Equal(ToolExecutionDecision.ApprovedOnce, execution.Decision);
            Assert.True(execution.Result.IsSuccess);
            Assert.Equal(1, tool.ExecuteCount);
        }

        [Fact]
        public void NeverPolicyRunsApprovalWithoutAsking()
        {
            var tool = new FakeTool("write", ToolRisk.High, true);
            _registry.Register(tool);
            AgentToolDispatcher dispatcher = CreateDispatcher();

            ToolContext context = Context("s1");
            context.ApprovalPolicy = ToolApprovalPolicy.Never;

            ToolExecution execution = dispatcher.ExecuteAsync(
                Call("write", "{\"path\":\"a.txt\"}"),
                context,
                CancellationToken.None).GetAwaiter().GetResult();

            Assert.Equal(ToolExecutionDecision.Auto, execution.Decision);
            Assert.True(execution.Result.IsSuccess);
            Assert.Equal(1, tool.ExecuteCount);
            Assert.Equal(0, _approvals.GetPendingCount());
            Assert.Contains("never", execution.RiskReason);
        }

        [Fact]
        public void DeniedApprovalDoesNotExecuteTool()
        {
            var tool = new FakeTool("write", ToolRisk.High, true);
            _registry.Register(tool);
            AgentToolDispatcher dispatcher = CreateDispatcher();

            Task<ToolExecution> task = dispatcher.ExecuteAsync(
                Call("write", "{\"path\":\"a.txt\"}"),
                Context("s1"),
                CancellationToken.None);

            Assert.True(_approvals.TryResolve("call-1", ToolApprovalOutcome.Deny, "不要改这个文件"));

            ToolExecution execution = task.GetAwaiter().GetResult();
            Assert.Equal(ToolExecutionDecision.Denied, execution.Decision);
            Assert.Equal(ToolResultStatus.Denied, execution.Result.Status);
            Assert.Contains("不要改这个文件", execution.Result.Content);
            Assert.Equal(0, tool.ExecuteCount);
            Assert.Equal("denied", _audit.Entries[0].Decision);
            Assert.Equal(ToolResultStatus.Denied, _audit.Entries[0].Status);
        }

        [Fact]
        public void SessionGrantSkipsSecondApproval()
        {
            var tool = new FakeTool("write", ToolRisk.High, true)
            {
                Preview = new ToolApprovalPreview
                {
                    Title = "写入文件",
                    Target = @"a.txt",
                    Summary = "修改 1 行",
                    CanApproveForSession = true,
                    ScopeKey = @"a.txt"
                }
            };
            _registry.Register(tool);
            AgentToolDispatcher dispatcher = CreateDispatcher();

            Task<ToolExecution> first = dispatcher.ExecuteAsync(
                Call("write", "{\"path\":\"a.txt\"}"),
                Context("s1"),
                CancellationToken.None);
            _approvals.TryResolve("call-1", ToolApprovalOutcome.ApproveForSession, null);

            Assert.Equal(ToolExecutionDecision.ApprovedForSession, first.GetAwaiter().GetResult().Decision);

            ToolExecution second = Execute(dispatcher, "write", "call-2");

            Assert.Equal(ToolExecutionDecision.ApprovedForSession, second.Decision);
            Assert.Equal(2, tool.ExecuteCount);
            Assert.Equal(0, _approvals.GetPendingCount());
        }

        [Fact]
        public void BlockedRiskEvaluatorPreventsExecutionAndApproval()
        {
            var tool = new BlockingTool("pwsh");
            _registry.Register(tool);
            AgentToolDispatcher dispatcher = CreateDispatcher();

            ToolExecution execution = Execute(dispatcher, "pwsh", "call-1", "{\"command\":\"format C:\"}");

            Assert.Equal(ToolExecutionDecision.Blocked, execution.Decision);
            Assert.Equal(ToolResultStatus.Blocked, execution.Result.Status);
            Assert.Equal("risk_blocked", execution.Result.ErrorCode);
            Assert.Equal(0, tool.ExecuteCount);
            Assert.Equal(0, _approvals.GetPendingCount());
            Assert.Equal("blocked", _audit.Entries[0].Decision);
        }

        [Fact]
        public void UnknownToolReturnsStructuredError()
        {
            AgentToolDispatcher dispatcher = CreateDispatcher();

            ToolExecution execution = Execute(dispatcher, "does_not_exist");

            Assert.Equal(ToolExecutionDecision.Invalid, execution.Decision);
            Assert.Equal("unknown_tool", execution.Result.ErrorCode);
            Assert.Contains("未知工具", execution.Result.Content);
            Assert.Single(_audit.Entries);
        }

        [Fact]
        public void ToolExceptionBecomesStructuredFailure()
        {
            var tool = new FakeTool("read", ToolRisk.Low, false) { Throw = true };
            _registry.Register(tool);
            AgentToolDispatcher dispatcher = CreateDispatcher();

            ToolExecution execution = Execute(dispatcher, "read");

            Assert.Equal(ToolResultStatus.Failed, execution.Result.Status);
            Assert.Equal("tool_exception", execution.Result.ErrorCode);
            Assert.DoesNotContain("at ", execution.Result.Content);
        }

        [Fact]
        public void CancellingWhilePendingCancelsExecution()
        {
            var tool = new FakeTool("write", ToolRisk.High, true);
            _registry.Register(tool);
            AgentToolDispatcher dispatcher = CreateDispatcher();
            var cts = new CancellationTokenSource();

            Task<ToolExecution> task = dispatcher.ExecuteAsync(
                Call("write", "{}"),
                Context("s1"),
                cts.Token);

            cts.Cancel();

            ToolExecution execution = task.GetAwaiter().GetResult();
            Assert.Equal(ToolExecutionDecision.Cancelled, execution.Decision);
            Assert.Equal(ToolResultStatus.Cancelled, execution.Result.Status);
            Assert.Equal(0, tool.ExecuteCount);
        }

        [Fact]
        public void MissingSessionFailsApprovalTools()
        {
            var tool = new FakeTool("write", ToolRisk.High, true);
            _registry.Register(tool);
            AgentToolDispatcher dispatcher = CreateDispatcher();

            ToolExecution execution = Execute(
                dispatcher,
                "write",
                "call-1",
                "{}",
                Context(string.Empty));

            Assert.Equal(ToolResultStatus.Failed, execution.Result.Status);
            Assert.Equal("missing_session", execution.Result.ErrorCode);
            Assert.Equal(0, tool.ExecuteCount);
        }

        [Fact]
        public void AuditEntryRedactsSecrets()
        {
            var tool = new FakeTool("read", ToolRisk.Low, false);
            _registry.Register(tool);
            AgentToolDispatcher dispatcher = CreateDispatcher();

            Execute(
                dispatcher,
                "read",
                "call-9",
                "{\"api_key\":\"sk-abcdefghijklmnop\",\"path\":\"a.txt\"}");

            ToolAuditEntry entry = _audit.Entries[0];
            Assert.DoesNotContain("sk-abcdefghijklmnop", entry.ArgumentsSummary);
            Assert.Equal("s1", entry.SessionId);
            Assert.Equal("w1", entry.WorkspaceId);
            Assert.Equal("read", entry.ToolName);
            Assert.True(entry.DurationMilliseconds >= 0);
            Assert.Equal(_clock.UtcNow, entry.Timestamp);
        }

        [Fact]
        public void MissingToolNameReturnsInvalidArguments()
        {
            AgentToolDispatcher dispatcher = CreateDispatcher();
            var execution = dispatcher.ExecuteAsync(
                new ToolCall { Id = "c1", Name = "   " },
                Context("s1"),
                CancellationToken.None).GetAwaiter().GetResult();

            Assert.Equal(ToolResultStatus.InvalidArguments, execution.Result.Status);
            Assert.Equal("missing_tool_name", execution.Result.ErrorCode);
        }

        private AgentToolDispatcher CreateDispatcher()
        {
            return new AgentToolDispatcher(_registry, _approvals, _audit, _clock);
        }

        private ToolExecution Execute(
            AgentToolDispatcher dispatcher,
            string toolName,
            string callId = "call-1",
            string arguments = "{}",
            ToolContext context = null)
        {
            return dispatcher.ExecuteAsync(
                Call(toolName, arguments, callId),
                context ?? Context("s1"),
                CancellationToken.None).GetAwaiter().GetResult();
        }

        private static ToolCall Call(string name, string arguments, string id = "call-1")
        {
            return new ToolCall
            {
                Id = id,
                Name = name,
                ArgumentsJson = arguments
            };
        }

        private static ToolContext Context(string sessionId)
        {
            return new ToolContext
            {
                SessionId = sessionId,
                WorkspaceId = "w1",
                WorkspaceRoot = Root,
                Sandbox = new WorkspacePathSandbox(Root, null),
                CallId = "call-1",
                Now = DateTimeOffset.UtcNow
            };
        }

        private sealed class FakeTool : IAgentTool
        {
            public FakeTool(string name, ToolRisk risk, bool requiresApproval)
            {
                Name = name;
                Risk = risk;
                RequiresApproval = requiresApproval;
            }

            public string Name { get; }

            public string Description
            {
                get { return "fake"; }
            }

            public ToolRisk Risk { get; set; }

            public bool RequiresApproval { get; set; }

            public ToolApprovalPreview Preview { get; set; }

            public bool Throw { get; set; }

            public int ExecuteCount { get; private set; }

            public ToolApprovalPreview Describe(ToolCall call, ToolContext context)
            {
                return Preview;
            }

            public Task<ToolResult> ExecuteAsync(
                ToolCall call,
                ToolContext context,
                CancellationToken cancellationToken)
            {
                ExecuteCount++;
                if (Throw)
                {
                    throw new InvalidOperationException("boom");
                }

                return Task.FromResult(ToolResult.Success(call, "ok", "fake result", false, false));
            }
        }

        private sealed class BlockingTool : IAgentTool, IToolRiskEvaluator
        {
            public BlockingTool(string name)
            {
                Name = name;
            }

            public string Name { get; }

            public string Description
            {
                get { return "blocking"; }
            }

            public ToolRisk Risk
            {
                get { return ToolRisk.Low; }
            }

            public bool RequiresApproval
            {
                get { return false; }
            }

            public int ExecuteCount { get; private set; }

            public ToolApprovalPreview Describe(ToolCall call, ToolContext context)
            {
                return null;
            }

            public ToolRiskAssessment AssessRisk(ToolCall call, ToolContext context)
            {
                return new ToolRiskAssessment(ToolRisk.Blocked, "命中危险命令黑名单：format");
            }

            public Task<ToolResult> ExecuteAsync(
                ToolCall call,
                ToolContext context,
                CancellationToken cancellationToken)
            {
                ExecuteCount++;
                return Task.FromResult(ToolResult.Success(call, "should not run", "x", false, false));
            }
        }

        private sealed class CapturingAuditLog : IToolAuditLog
        {
            public List<ToolAuditEntry> Entries { get; } = new List<ToolAuditEntry>();

            public void Write(ToolAuditEntry entry)
            {
                Entries.Add(entry);
            }
        }

        private sealed class FakeClock : IClock
        {
            public FakeClock(DateTimeOffset now)
            {
                UtcNow = now;
            }

            public DateTimeOffset UtcNow { get; }
        }
    }
}
