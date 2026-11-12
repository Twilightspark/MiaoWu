using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MiaoWu.Core.Agents.Tools;
using Xunit;

namespace MiaoWu.Tests.Tools
{
    public sealed class ToolApprovalServiceTests
    {
        [Fact]
        public void ApproveOnceCompletesPendingRequest()
        {
            var service = new ToolApprovalService();
            Task<ToolApprovalResult> task = service.RequestAsync(
                Request("r1", "write_file", @"src\a.cs", true),
                CancellationToken.None);

            Assert.Equal(1, service.GetPendingCount());
            Assert.True(service.TryResolve("r1", ToolApprovalOutcome.ApproveOnce, null));

            ToolApprovalResult result = task.GetAwaiter().GetResult();
            Assert.True(result.IsApproved);
            Assert.Equal(ToolApprovalStatus.Approved, result.Status);
            Assert.Equal(ToolApprovalOutcome.ApproveOnce, result.Outcome);
            Assert.False(result.ApprovedBySessionGrant);
            Assert.Equal(0, service.GetPendingCount());
        }

        [Fact]
        public void DenyCarriesReasonAndDoesNotCreateGrant()
        {
            var service = new ToolApprovalService();
            Task<ToolApprovalResult> task = service.RequestAsync(
                Request("r1", "run_command", null, false),
                CancellationToken.None);

            service.TryResolve("r1", ToolApprovalOutcome.Deny, "会影响其他项目");

            ToolApprovalResult result = task.GetAwaiter().GetResult();
            Assert.Equal(ToolApprovalStatus.Denied, result.Status);
            Assert.Equal(ToolApprovalOutcome.Deny, result.Outcome);
            Assert.Equal("会影响其他项目", result.Ticket.DenyReason);
            Assert.False(service.HasSessionGrant("s1", "run_command", string.Empty));
        }

        [Fact]
        public void SessionGrantSkipsSecondRequest()
        {
            var service = new ToolApprovalService();
            Task<ToolApprovalResult> first = service.RequestAsync(
                Request("r1", "write_file", @"src\a.cs", true),
                CancellationToken.None);

            service.TryResolve("r1", ToolApprovalOutcome.ApproveForSession, null);
            first.GetAwaiter().GetResult();

            Assert.True(service.HasSessionGrant("s1", "write_file", @"src\a.cs"));

            Task<ToolApprovalResult> second = service.RequestAsync(
                Request("r2", "write_file", @"src\a.cs", true),
                CancellationToken.None);

            Assert.True(second.IsCompleted);
            ToolApprovalResult result = second.GetAwaiter().GetResult();
            Assert.True(result.ApprovedBySessionGrant);
            Assert.Equal(ToolApprovalOutcome.ApproveForSession, result.Outcome);
            Assert.Equal(0, service.GetPendingCount());
        }

        [Fact]
        public void GrantIsScopedToToolAndPath()
        {
            var service = new ToolApprovalService();
            Task<ToolApprovalResult> first = service.RequestAsync(
                Request("r1", "write_file", @"src\a.cs", true),
                CancellationToken.None);
            service.TryResolve("r1", ToolApprovalOutcome.ApproveForSession, null);
            first.GetAwaiter().GetResult();

            Assert.False(service.HasSessionGrant("s1", "write_file", @"src\b.cs"));
            Assert.False(service.HasSessionGrant("s1", "write_file", @"SRC\A.CS2"));
            Assert.False(service.HasSessionGrant("s2", "write_file", @"src\a.cs"));
            Assert.True(service.HasSessionGrant("s1", "write_file", @"SRC\A.CS"));
        }

        [Fact]
        public void SessionApprovalIsDowngradedWhenToolDoesNotAllowIt()
        {
            var service = new ToolApprovalService();
            Task<ToolApprovalResult> task = service.RequestAsync(
                Request("r1", "run_command", null, false),
                CancellationToken.None);

            service.TryResolve("r1", ToolApprovalOutcome.ApproveForSession, null);

            ToolApprovalResult result = task.GetAwaiter().GetResult();
            Assert.Equal(ToolApprovalOutcome.ApproveOnce, result.Outcome);
            Assert.False(service.HasSessionGrant("s1", "run_command", string.Empty));
        }

        [Fact]
        public void CancellingTokenCancelsPendingRequest()
        {
            var service = new ToolApprovalService();
            var cts = new CancellationTokenSource();

            Task<ToolApprovalResult> task = service.RequestAsync(
                Request("r1", "write_file", @"src\a.cs", true),
                cts.Token);

            Assert.Equal(1, service.GetPendingCount());
            cts.Cancel();

            ToolApprovalResult result = task.GetAwaiter().GetResult();
            Assert.Equal(ToolApprovalStatus.Cancelled, result.Status);
            Assert.Null(result.Outcome);
            Assert.Equal(0, service.GetPendingCount());
        }

        [Fact]
        public void AlreadyCancelledTokenReturnsCancelledWithoutPending()
        {
            var service = new ToolApprovalService();
            var cts = new CancellationTokenSource();
            cts.Cancel();

            ToolApprovalResult result = service
                .RequestAsync(Request("r1", "write_file", @"src\a.cs", true), cts.Token)
                .GetAwaiter()
                .GetResult();

            Assert.Equal(ToolApprovalStatus.Cancelled, result.Status);
            Assert.Equal(0, service.GetPendingCount());
        }

        [Fact]
        public void ClearSessionCancelsPendingAndGrants()
        {
            var service = new ToolApprovalService();
            Task<ToolApprovalResult> first = service.RequestAsync(
                Request("r1", "write_file", @"src\a.cs", true),
                CancellationToken.None);
            service.TryResolve("r1", ToolApprovalOutcome.ApproveForSession, null);
            first.GetAwaiter().GetResult();

            Task<ToolApprovalResult> pending = service.RequestAsync(
                Request("r2", "write_file", @"src\b.cs", true),
                CancellationToken.None);
            Assert.Equal(1, service.GetPendingCount());

            service.ClearSession("s1");

            ToolApprovalResult pendingResult = pending.GetAwaiter().GetResult();
            Assert.Equal(ToolApprovalStatus.Cancelled, pendingResult.Status);
            Assert.Equal(0, service.GetPendingCount());
            Assert.False(service.HasSessionGrant("s1", "write_file", @"src\a.cs"));

            Task<ToolApprovalResult> afterClear = service.RequestAsync(
                Request("r3", "write_file", @"src\a.cs", true),
                CancellationToken.None);
            Assert.False(afterClear.IsCompleted);
            service.TryResolve("r3", ToolApprovalOutcome.Deny, null);
            Assert.Equal(ToolApprovalStatus.Denied, afterClear.GetAwaiter().GetResult().Status);
        }

        [Fact]
        public void PendingRequestsAreFilteredBySession()
        {
            var service = new ToolApprovalService();
            service.RequestAsync(Request("r1", "write_file", @"a.cs", true), CancellationToken.None);
            service.RequestAsync(Request("r2", "write_file", @"b.cs", true, "s2"), CancellationToken.None);

            Assert.Equal(2, service.GetPendingCount());
            Assert.Single(service.GetPending("s1"));
            Assert.Single(service.GetPending("s2"));
            Assert.Empty(service.GetPending("s3"));
        }

        [Fact]
        public void DuplicateResolveReturnsFalse()
        {
            var service = new ToolApprovalService();
            Task<ToolApprovalResult> task = service.RequestAsync(
                Request("r1", "write_file", @"a.cs", true),
                CancellationToken.None);

            Assert.True(service.TryResolve("r1", ToolApprovalOutcome.ApproveOnce, null));
            Assert.False(service.TryResolve("r1", ToolApprovalOutcome.Deny, null));
            Assert.False(service.TryResolve("missing", ToolApprovalOutcome.ApproveOnce, null));

            Assert.Equal(ToolApprovalStatus.Approved, task.GetAwaiter().GetResult().Status);
        }

        [Fact]
        public void SubscriberExceptionDoesNotBreakStateMachine()
        {
            var service = new ToolApprovalService();
            service.Changed += (sender, args) => throw new InvalidOperationException("boom");

            Task<ToolApprovalResult> task = service.RequestAsync(
                Request("r1", "write_file", @"a.cs", true),
                CancellationToken.None);
            Assert.True(service.TryResolve("r1", ToolApprovalOutcome.ApproveOnce, null));

            Assert.True(task.GetAwaiter().GetResult().IsApproved);
        }

        [Fact]
        public void ChangedEventReportsRequestedAndResolved()
        {
            var service = new ToolApprovalService();
            var kinds = new List<ToolApprovalChangeKind>();
            service.Changed += (sender, args) => kinds.Add(args.Kind);

            Task<ToolApprovalResult> task = service.RequestAsync(
                Request("r1", "write_file", @"a.cs", true),
                CancellationToken.None);
            service.TryResolve("r1", ToolApprovalOutcome.ApproveOnce, null);
            task.GetAwaiter().GetResult();

            Assert.Equal(
                new[] { ToolApprovalChangeKind.Requested, ToolApprovalChangeKind.Resolved },
                kinds);
        }

        private static ToolApprovalRequest Request(
            string id,
            string toolName,
            string scopeKey,
            bool canApproveForSession,
            string sessionId = "s1")
        {
            return new ToolApprovalRequest
            {
                RequestId = id,
                SessionId = sessionId,
                ToolName = toolName,
                Risk = ToolRisk.High,
                Title = "需要审批",
                Target = scopeKey ?? "command",
                Summary = "影响摘要",
                CanApproveForSession = canApproveForSession,
                ScopeKey = scopeKey,
                CreatedAt = DateTimeOffset.UtcNow
            };
        }
    }
}
