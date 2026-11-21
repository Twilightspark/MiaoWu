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

    /// <summary>计划模式切换（UI 用它同步开关状态）。</summary>
    public sealed class PlanModeChangedEvent : AgentEvent
    {
        public bool Active { get; set; }
    }

    public sealed class SnapshotRequiredEvent : AgentEvent
    {
        public string Reason { get; set; }
    }

    // ---- 步骤事件（对齐 DSH 的 step/*）----
    //
    // 术语：一次 run 是一条用户输入开启的「轮次」（turn），轮次内每次模型请求
    // 是一个「步骤」（step）。DSH 的 turn/step 与本模型一一对应，只是 MiaoWu
    // 继续用 Run 作为轮次的对外名字，避免改动既有 UI 与持久化。

    /// <summary>一步结束的原因。与 DSH 的 turn-end 变体对齐，只保留桌面用得到的几种。</summary>
    public enum StepEndReason
    {
        /// <summary>模型没有再请求工具，本轮次收敛。</summary>
        Completed = 0,

        /// <summary>模型请求了工具，执行后进入下一步。</summary>
        ToolCalls = 1,

        /// <summary>本步触顶 max tokens；轮次以粘性 max-tokens 结束。</summary>
        MaxTokens = 2,

        /// <summary>用户取消。</summary>
        Aborted = 3,

        /// <summary>结构化失败。</summary>
        Error = 4
    }

    public sealed class StepStartedEvent : AgentEvent
    {
        public string RunId { get; set; }

        /// <summary>轮次内的步骤序号，从 1 开始。</summary>
        public int StepIndex { get; set; }

        public string AssistantMessageId { get; set; }

        /// <summary>是否本轮次第一步（标题请求、过程组计时起点都依赖它）。</summary>
        public bool IsFirstStep { get; set; }
    }

    public sealed class StepCompletedEvent : AgentEvent
    {
        public string RunId { get; set; }

        public int StepIndex { get; set; }

        public string AssistantMessageId { get; set; }

        public StepEndReason Reason { get; set; }

        public StopReason StopReason { get; set; }

        public TokenUsage Usage { get; set; }

        /// <summary>本步交给调度器的工具调用数（含被上限截断后补结果的调用）。</summary>
        public int ToolCallCount { get; set; }

        /// <summary>是否还会进入下一步。</summary>
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
