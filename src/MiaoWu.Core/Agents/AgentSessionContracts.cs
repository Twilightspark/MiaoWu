using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MiaoWu.Core.Agents.Tools;
using MiaoWu.Core.Errors;
using MiaoWu.Core.Models;

namespace MiaoWu.Core.Agents
{
    // Faulted 只用于 loop 任务不可恢复；单次请求失败回 Idle。
    public enum AgentSessionState
    {
        Idle,
        Running,
        Cancelling,
        Faulted,
        Disposed
    }

    public enum AgentSendMode
    {
        /// <summary>当前 run 结束后依次执行（pending 上限 4）。</summary>
        Queue,

        /// <summary>P3 不使用；保留给后续插话。</summary>
        Interrupt
    }

    public sealed class AgentSessionOptions
    {
        public string SessionId { get; set; }

        public string WorkspaceId { get; set; }

        /// <summary>工作区根（规范化绝对路径）；工具沙箱从它解析。</summary>
        public string WorkspaceRoot { get; set; }

        /// <summary>该工作区的会话存储；为空时用运行时级默认存储。</summary>
        public MiaoWu.Core.Conversations.IConversationStore Store { get; set; }

        public string SystemPrompt { get; set; }

        public string ProviderId { get; set; }

        public string ModelId { get; set; }

        public double? Temperature { get; set; }

        public int? MaxOutputTokens { get; set; }

        public int HistoryLimit { get; set; }

        public int MaxTurnsPerRun { get; set; }

        public int MaxToolCallsPerTurn { get; set; }
    }

    public sealed class AgentSessionSnapshot
    {
        public string SessionId { get; set; }

        public string WorkspaceId { get; set; }

        public AgentSessionState State { get; set; }

        public long Revision { get; set; }

        public List<AgentMessage> Messages { get; set; }

        public string ActiveRunId { get; set; }

        public string ActiveAssistantMessageId { get; set; }

        public int ActiveTextLength { get; set; }

        public TokenUsage LastUsage { get; set; }

        public AgentError LastError { get; set; }

        public int PendingCount { get; set; }

        public string Title { get; set; }

        public int TurnIndex { get; set; }

        public IReadOnlyList<ToolExecution> ToolExecutions { get; set; }

        public string ProviderId { get; set; }

        public string ModelId { get; set; }

        public int EstimatedContextTokens { get; set; }
    }

    public sealed class AgentRun
    {
        public string RunId { get; set; }

        public string UserMessageId { get; set; }

        public string AssistantMessageId { get; set; }

        /// <summary>run 结束（完成/失败/取消）时完成；不因失败而 fault。</summary>
        public Task Completion { get; set; }
    }

    public interface IAgentSession
    {
        string Id { get; }

        string WorkspaceId { get; }

        AgentSessionState State { get; }

        AgentSessionSnapshot Snapshot();

        Task<AgentRun> SendAsync(AgentInput input, AgentSendMode mode, CancellationToken cancellationToken);

        Task CancelAsync(bool clearPending, CancellationToken cancellationToken);

        /// <summary>移除指定 assistant 消息并从同一条用户消息重跑。</summary>
        Task RetryAsync(string assistantMessageId, CancellationToken cancellationToken);

        Task SetModelAsync(string providerId, string modelId, CancellationToken cancellationToken);

        /// <summary>订阅后必须先 Snapshot() 再消费实时事件。</summary>
        IDisposable Subscribe(IAgentEventSink sink);

        Task WaitForIdleAsync(CancellationToken cancellationToken);

        Task RenameAsync(string title, CancellationToken cancellationToken);
    }

    public interface IAgentRuntime : IDisposable
    {
        void Start();

        Task<IAgentSession> GetOrCreateAsync(AgentSessionOptions options, CancellationToken cancellationToken);

        IAgentSession Find(string sessionId);

        Task CloseAsync(string sessionId, CancellationToken cancellationToken);

        Task ShutdownAsync(TimeSpan timeout);

        IReadOnlyList<AgentSessionSnapshot> GetActiveSnapshots();

        /// <summary>轻量汇总事件：标题/状态/完成时间变化。</summary>
        event EventHandler<AgentSessionSummaryEventArgs> SessionChanged;
    }

    public sealed class AgentSessionSummaryEventArgs : EventArgs
    {
        public string SessionId { get; set; }

        public string WorkspaceId { get; set; }

        public string Title { get; set; }

        public AgentSessionState State { get; set; }

        public DateTimeOffset UpdatedAt { get; set; }

        public StopReason? LastStopReason { get; set; }
    }
}
