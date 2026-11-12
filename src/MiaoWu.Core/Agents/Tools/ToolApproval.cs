using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace MiaoWu.Core.Agents.Tools
{
    /// <summary>用户对一次审批请求的决定。</summary>
    public enum ToolApprovalOutcome
    {
        /// <summary>只允许这一次。</summary>
        ApproveOnce = 0,

        /// <summary>本会话内对同一工具与同一目标不再询问。</summary>
        ApproveForSession = 1,

        /// <summary>拒绝，并把原因回传给模型。</summary>
        Deny = 2
    }

    /// <summary>审批票据的生命周期。</summary>
    public enum ToolApprovalStatus
    {
        Pending = 0,
        Approved = 1,
        Denied = 2,

        /// <summary>会话结束、用户停止或运行时关闭导致审批作废。</summary>
        Cancelled = 3
    }

    /// <summary>
    /// 展示在通知里的审批请求。Diff 与 Target 都要经过截断与脱敏后才允许进入 UI/审计。
    /// </summary>
    public sealed class ToolApprovalRequest
    {
        public string RequestId { get; set; }

        public string SessionId { get; set; }

        public string ToolName { get; set; }

        public ToolRisk Risk { get; set; }

        /// <summary>通知标题，例如“写入文件”。</summary>
        public string Title { get; set; }

        /// <summary>等宽显示的目标：规范化路径或命令行。</summary>
        public string Target { get; set; }

        /// <summary>影响摘要：行数变化、是否备份、失效条件。</summary>
        public string Summary { get; set; }

        /// <summary>是否允许“本次会话都允许”。</summary>
        public bool CanApproveForSession { get; set; }

        public bool CanShowDiff { get; set; }

        /// <summary>统一 diff 文本；为空时 UI 不显示“查看 diff”。</summary>
        public string Diff { get; set; }

        /// <summary>
        /// 会话授权的匹配键：写文件为规范化路径，命令为空（命令不提供会话授权）。
        /// 不展示给用户。
        /// </summary>
        public string ScopeKey { get; set; }

        public DateTimeOffset CreatedAt { get; set; }
    }

    /// <summary>审批票据：请求 + 当前状态 + 决定与理由。</summary>
    public sealed class ToolApprovalTicket
    {
        public ToolApprovalTicket(ToolApprovalRequest request)
        {
            Request = request ?? throw new ArgumentNullException(nameof(request));
            Status = ToolApprovalStatus.Pending;
        }

        public ToolApprovalRequest Request { get; }

        public ToolApprovalStatus Status { get; internal set; }

        /// <summary>仅在 <see cref="ToolApprovalStatus.Approved"/> / <see cref="ToolApprovalStatus.Denied"/> 时有值。</summary>
        public ToolApprovalOutcome? Outcome { get; internal set; }

        public string DenyReason { get; internal set; }

        public DateTimeOffset? DecidedAt { get; internal set; }

        public bool IsPending
        {
            get { return Status == ToolApprovalStatus.Pending; }
        }
    }

    /// <summary>审批状态变化的类别。</summary>
    public enum ToolApprovalChangeKind
    {
        Requested = 0,
        Resolved = 1,
        Cancelled = 2
    }

    public sealed class ToolApprovalEventArgs : EventArgs
    {
        public ToolApprovalEventArgs(ToolApprovalChangeKind kind, ToolApprovalTicket ticket)
        {
            Kind = kind;
            Ticket = ticket;
        }

        public ToolApprovalChangeKind Kind { get; }

        public ToolApprovalTicket Ticket { get; }
    }

    /// <summary>一次审批等待的最终结果。等待被取消时 Status 为 Cancelled。</summary>
    public sealed class ToolApprovalResult
    {
        public ToolApprovalResult(
            ToolApprovalOutcome? outcome,
            ToolApprovalStatus status,
            bool approvedBySessionGrant,
            ToolApprovalTicket ticket)
        {
            Outcome = outcome;
            Status = status;
            ApprovedBySessionGrant = approvedBySessionGrant;
            Ticket = ticket;
        }

        public ToolApprovalOutcome? Outcome { get; }

        public ToolApprovalStatus Status { get; }

        /// <summary>true 表示命中本会话已有的“始终允许”，没有弹通知。</summary>
        public bool ApprovedBySessionGrant { get; }

        public ToolApprovalTicket Ticket { get; }

        public bool IsApproved
        {
            get
            {
                return Status == ToolApprovalStatus.Approved
                    && (Outcome == ToolApprovalOutcome.ApproveOnce
                        || Outcome == ToolApprovalOutcome.ApproveForSession);
            }
        }
    }

    /// <summary>本会话内的一次“始终允许”。只对同一工具 + 同一 ScopeKey 生效。</summary>
    public sealed class ToolApprovalGrant
    {
        public string SessionId { get; set; }

        public string ToolName { get; set; }

        public string ScopeKey { get; set; }

        public DateTimeOffset GrantedAt { get; set; }
    }

    /// <summary>
    /// 审批服务。负责挂起请求、等待用户决定、记录会话级授权；不负责展示，
    /// 也不负责执行工具。UI 通过 <see cref="Changed"/> 增删通知。
    /// </summary>
    public interface IToolApprovalService
    {
        event EventHandler<ToolApprovalEventArgs> Changed;

        /// <summary>
        /// 请求审批。命中会话授权时立即返回；否则挂起直到 <see cref="TryResolve"/>
        /// 或取消（取消返回 Cancelled，不抛异常）。
        /// </summary>
        Task<ToolApprovalResult> RequestAsync(
            ToolApprovalRequest request,
            CancellationToken cancellationToken);

        /// <summary>处理一条待审批请求；已处理或不存在时返回 false。</summary>
        bool TryResolve(string requestId, ToolApprovalOutcome outcome, string denyReason);

        IReadOnlyList<ToolApprovalTicket> GetPending();

        IReadOnlyList<ToolApprovalTicket> GetPending(string sessionId);

        int GetPendingCount();

        bool HasSessionGrant(string sessionId, string toolName, string scopeKey);

        /// <summary>会话结束：取消其全部待审批并清空授权。</summary>
        void ClearSession(string sessionId);
    }

    /// <summary>
    /// 默认实现：内存字典 + TaskCompletionSource。所有公开成员线程安全。
    /// 待审批请求不会自动超时——审批通知必须由用户显式处理，或随会话结束作废。
    /// </summary>
    public sealed class ToolApprovalService : IToolApprovalService
    {
        private readonly object _gate = new object();
        private readonly Dictionary<string, PendingApproval> _pending =
            new Dictionary<string, PendingApproval>(StringComparer.Ordinal);
        private readonly Dictionary<string, ToolApprovalGrant> _grants =
            new Dictionary<string, ToolApprovalGrant>(StringComparer.OrdinalIgnoreCase);

        public event EventHandler<ToolApprovalEventArgs> Changed;

        public Task<ToolApprovalResult> RequestAsync(
            ToolApprovalRequest request,
            CancellationToken cancellationToken)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            if (string.IsNullOrWhiteSpace(request.RequestId))
            {
                request.RequestId = Guid.NewGuid().ToString("N");
            }

            if (string.IsNullOrWhiteSpace(request.SessionId))
            {
                throw new ArgumentException("审批请求必须带会话 ID。", nameof(request));
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return Task.FromResult(CreateCancelledResult(request));
            }

            PendingApproval pending;
            ToolApprovalEventArgs requestedEvent = null;

            lock (_gate)
            {
                if (request.CanApproveForSession
                    && FindGrantLocked(request.SessionId, request.ToolName, request.ScopeKey) != null)
                {
                    var grantedTicket = new ToolApprovalTicket(request)
                    {
                        Status = ToolApprovalStatus.Approved,
                        Outcome = ToolApprovalOutcome.ApproveForSession,
                        DecidedAt = DateTimeOffset.UtcNow
                    };

                    return Task.FromResult(new ToolApprovalResult(
                        ToolApprovalOutcome.ApproveForSession,
                        ToolApprovalStatus.Approved,
                        true,
                        grantedTicket));
                }

                // 同一请求 ID 重复提交时复用旧票据，避免通知栈出现两条。
                if (_pending.TryGetValue(request.RequestId, out pending))
                {
                    return pending.Completion.Task;
                }

                pending = new PendingApproval(request);
                _pending.Add(request.RequestId, pending);

                if (cancellationToken.CanBeCanceled)
                {
                    pending.HasCancellation = true;
                    pending.CancellationRegistration = cancellationToken.Register(
                        () => CancelPending(request.RequestId));
                }

                requestedEvent = new ToolApprovalEventArgs(
                    ToolApprovalChangeKind.Requested,
                    pending.Ticket);
            }

            RaiseChanged(requestedEvent);
            return pending.Completion.Task;
        }

        public bool TryResolve(string requestId, ToolApprovalOutcome outcome, string denyReason)
        {
            if (string.IsNullOrWhiteSpace(requestId))
            {
                return false;
            }

            ToolApprovalTicket ticket;
            ToolApprovalGrant grant = null;
            PendingApproval pending = null;

            lock (_gate)
            {
                if (!_pending.TryGetValue(requestId, out pending) || !pending.Ticket.IsPending)
                {
                    return false;
                }

                ticket = pending.Ticket;

                if (outcome == ToolApprovalOutcome.Deny)
                {
                    ticket.Status = ToolApprovalStatus.Denied;
                    ticket.Outcome = ToolApprovalOutcome.Deny;
                    ticket.DenyReason = NormalizeReason(denyReason);
                }
                else
                {
                    ToolApprovalOutcome effective = outcome;
                    if (effective == ToolApprovalOutcome.ApproveForSession
                        && !ticket.Request.CanApproveForSession)
                    {
                        // 命令类工具不提供“始终允许”，静默降级为一次。
                        effective = ToolApprovalOutcome.ApproveOnce;
                    }

                    ticket.Status = ToolApprovalStatus.Approved;
                    ticket.Outcome = effective;

                    if (effective == ToolApprovalOutcome.ApproveForSession)
                    {
                        grant = new ToolApprovalGrant
                        {
                            SessionId = ticket.Request.SessionId,
                            ToolName = ticket.Request.ToolName,
                            ScopeKey = NormalizeScopeKey(ticket.Request.ScopeKey),
                            GrantedAt = DateTimeOffset.UtcNow
                        };

                        _grants[BuildGrantKey(grant)] = grant;
                    }
                }

                ticket.DecidedAt = DateTimeOffset.UtcNow;
                _pending.Remove(requestId);
            }

            pending.ReleaseCancellation();

            var result = new ToolApprovalResult(
                ticket.Outcome,
                ticket.Status,
                false,
                ticket);

            RaiseChanged(new ToolApprovalEventArgs(ToolApprovalChangeKind.Resolved, ticket));
            pending.Completion.TrySetResult(result);
            return true;
        }

        public IReadOnlyList<ToolApprovalTicket> GetPending()
        {
            lock (_gate)
            {
                return SnapshotLocked(null);
            }
        }

        public IReadOnlyList<ToolApprovalTicket> GetPending(string sessionId)
        {
            lock (_gate)
            {
                return SnapshotLocked(sessionId);
            }
        }

        public int GetPendingCount()
        {
            lock (_gate)
            {
                return _pending.Count;
            }
        }

        public bool HasSessionGrant(string sessionId, string toolName, string scopeKey)
        {
            lock (_gate)
            {
                return FindGrantLocked(sessionId, toolName, scopeKey) != null;
            }
        }

        public void ClearSession(string sessionId)
        {
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                return;
            }

            var cancelled = new List<PendingApproval>();
            lock (_gate)
            {
                var ids = new List<string>();
                foreach (KeyValuePair<string, PendingApproval> pair in _pending)
                {
                    if (string.Equals(
                        pair.Value.Ticket.Request.SessionId,
                        sessionId,
                        StringComparison.Ordinal))
                    {
                        ids.Add(pair.Key);
                    }
                }

                for (int i = 0; i < ids.Count; i++)
                {
                    PendingApproval pending = _pending[ids[i]];
                    pending.Ticket.Status = ToolApprovalStatus.Cancelled;
                    _pending.Remove(ids[i]);
                    cancelled.Add(pending);
                }

                var grantKeys = new List<string>();
                foreach (KeyValuePair<string, ToolApprovalGrant> pair in _grants)
                {
                    if (string.Equals(pair.Value.SessionId, sessionId, StringComparison.Ordinal))
                    {
                        grantKeys.Add(pair.Key);
                    }
                }

                for (int i = 0; i < grantKeys.Count; i++)
                {
                    _grants.Remove(grantKeys[i]);
                }
            }

            for (int i = 0; i < cancelled.Count; i++)
            {
                PendingApproval pending = cancelled[i];
                pending.ReleaseCancellation();
                RaiseChanged(new ToolApprovalEventArgs(ToolApprovalChangeKind.Cancelled, pending.Ticket));
                pending.Completion.TrySetResult(new ToolApprovalResult(
                    null,
                    ToolApprovalStatus.Cancelled,
                    false,
                    pending.Ticket));
            }
        }

        private void CancelPending(string requestId)
        {
            PendingApproval pending;
            lock (_gate)
            {
                if (!_pending.TryGetValue(requestId, out pending) || !pending.Ticket.IsPending)
                {
                    return;
                }

                pending.Ticket.Status = ToolApprovalStatus.Cancelled;
                _pending.Remove(requestId);
            }

            pending.ReleaseCancellation();

            RaiseChanged(new ToolApprovalEventArgs(ToolApprovalChangeKind.Cancelled, pending.Ticket));
            pending.Completion.TrySetResult(new ToolApprovalResult(
                null,
                ToolApprovalStatus.Cancelled,
                false,
                pending.Ticket));
        }

        private ToolApprovalResult CreateCancelledResult(ToolApprovalRequest request)
        {
            var ticket = new ToolApprovalTicket(request)
            {
                Status = ToolApprovalStatus.Cancelled
            };

            return new ToolApprovalResult(null, ToolApprovalStatus.Cancelled, false, ticket);
        }

        private IReadOnlyList<ToolApprovalTicket> SnapshotLocked(string sessionId)
        {
            var list = new List<ToolApprovalTicket>(_pending.Count);
            foreach (KeyValuePair<string, PendingApproval> pair in _pending)
            {
                if (sessionId == null
                    || string.Equals(
                        pair.Value.Ticket.Request.SessionId,
                        sessionId,
                        StringComparison.Ordinal))
                {
                    list.Add(pair.Value.Ticket);
                }
            }

            return list;
        }

        private ToolApprovalGrant FindGrantLocked(string sessionId, string toolName, string scopeKey)
        {
            string key = BuildGrantKey(sessionId, toolName, scopeKey);
            if (_grants.TryGetValue(key, out ToolApprovalGrant grant))
            {
                return grant;
            }

            return null;
        }

        private static string BuildGrantKey(ToolApprovalGrant grant)
        {
            return BuildGrantKey(grant.SessionId, grant.ToolName, grant.ScopeKey);
        }

        private static string BuildGrantKey(string sessionId, string toolName, string scopeKey)
        {
            return (sessionId ?? string.Empty)
                + "\u0001"
                + (toolName ?? string.Empty)
                + "\u0001"
                + NormalizeScopeKey(scopeKey);
        }

        private static string NormalizeScopeKey(string scopeKey)
        {
            // Windows 路径忽略大小写；命令授权不存在，ScopeKey 为空也不影响。
            return (scopeKey ?? string.Empty).Trim();
        }

        private static string NormalizeReason(string denyReason)
        {
            if (string.IsNullOrWhiteSpace(denyReason))
            {
                return string.Empty;
            }

            const int MaxLength = 500;
            string text = denyReason.Trim();
            return text.Length <= MaxLength ? text : text.Substring(0, MaxLength);
        }

        private void RaiseChanged(ToolApprovalEventArgs args)
        {
            EventHandler<ToolApprovalEventArgs> handler = Changed;
            if (handler == null)
            {
                return;
            }

            try
            {
                handler(this, args);
            }
            catch (Exception)
            {
                // 订阅者异常不能破坏审批状态机。
            }
        }

        private sealed class PendingApproval
        {
            public PendingApproval(ToolApprovalRequest request)
            {
                Ticket = new ToolApprovalTicket(request);
                Completion = new TaskCompletionSource<ToolApprovalResult>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            }

            public ToolApprovalTicket Ticket { get; }

            public TaskCompletionSource<ToolApprovalResult> Completion { get; }

            public CancellationTokenRegistration CancellationRegistration { get; set; }

            public bool HasCancellation { get; set; }

            public void ReleaseCancellation()
            {
                if (HasCancellation)
                {
                    CancellationRegistration.Dispose();
                    CancellationRegistration = default(CancellationTokenRegistration);
                    HasCancellation = false;
                }
            }
        }
    }
}
