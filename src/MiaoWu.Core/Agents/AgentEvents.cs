using System;
using MiaoWu.Core.Agents.Tools;
using MiaoWu.Core.Errors;
using MiaoWu.Core.Models;

namespace MiaoWu.Core.Agents
{
    /// <summary>所有会话事件的基类。事件只描述“发生了什么”，不携带 UI 指令。</summary>
    public abstract class AgentEvent
    {
        public string SessionId { get; set; }

        public long Sequence { get; set; }

        public DateTimeOffset Timestamp { get; set; }
    }

    public sealed class StateChangedEvent : AgentEvent
    {
        public AgentSessionState State { get; set; }
    }

    public sealed class RunStartedEvent : AgentEvent
    {
        public string RunId { get; set; }

        public string UserMessageId { get; set; }

        public string AssistantMessageId { get; set; }

        public string ProviderId { get; set; }

        public string ModelId { get; set; }
    }

    public sealed class MessageStartedEvent : AgentEvent
    {
        public AgentMessage Message { get; set; }
    }

    public sealed class MessageDeltaEvent : AgentEvent
    {
        public string MessageId { get; set; }

        public int BlockIndex { get; set; }

        public string Delta { get; set; }

        public int TextLength { get; set; }
    }

    public sealed class ThinkingDeltaEvent : AgentEvent
    {
        public string MessageId { get; set; }

        public int BlockIndex { get; set; }

        public string Delta { get; set; }
    }

    public sealed class MessageCompletedEvent : AgentEvent
    {
        public AgentMessage Message { get; set; }

        public TokenUsage Usage { get; set; }

        public StopReason StopReason { get; set; }
    }

    public sealed class RunCompletedEvent : AgentEvent
    {
        public string RunId { get; set; }

        public StopReason StopReason { get; set; }

        public TokenUsage Usage { get; set; }
    }

    public sealed class RunCancelledEvent : AgentEvent
    {
        public string RunId { get; set; }

        public string AssistantMessageId { get; set; }
    }

    public sealed class RunFailedEvent : AgentEvent
    {
        public string RunId { get; set; }

        public string AssistantMessageId { get; set; }

        public AgentError Error { get; set; }

        public bool HasPartialContent { get; set; }
    }

    public sealed class SessionTitleChangedEvent : AgentEvent
    {
        public string Title { get; set; }
    }

    public sealed class UsageChangedEvent : AgentEvent
    {
        public TokenUsage Usage { get; set; }

        public int EstimatedContextTokens { get; set; }
    }

    public sealed class SnapshotRequiredEvent : AgentEvent
    {
        public string Reason { get; set; }
    }

    // ---- P3.5 / P4：工具轮次事件 ----

    public sealed class TurnStartedEvent : AgentEvent
    {
        public string RunId { get; set; }

        public int TurnIndex { get; set; }
    }

    public sealed class TurnCompletedEvent : AgentEvent
    {
        public string RunId { get; set; }

        public int TurnIndex { get; set; }

        public StopReason StopReason { get; set; }

        public TokenUsage Usage { get; set; }

        public bool WillContinue { get; set; }
    }

    public sealed class ToolExecutionStartedEvent : AgentEvent
    {
        public ToolExecution Execution { get; set; }
    }

    public sealed class ToolExecutionCompletedEvent : AgentEvent
    {
        public ToolExecution Execution { get; set; }
    }

    public sealed class ToolApprovalRequestedEvent : AgentEvent
    {
        public ToolApprovalRequest Request { get; set; }
    }

    public sealed class ToolApprovalResolvedEvent : AgentEvent
    {
        public string RequestId { get; set; }

        public ToolApprovalStatus Status { get; set; }

        public ToolApprovalOutcome? Outcome { get; set; }
    }

    /// <summary>会话事件订阅者。发布线程同步调用，实现方不得阻塞。</summary>
    public interface IAgentEventSink
    {
        void OnEvent(AgentEvent evt);

        /// <summary>队列溢出，订阅者应调用 Snapshot() 重同步。</summary>
        void OnOverflow();
    }
}
