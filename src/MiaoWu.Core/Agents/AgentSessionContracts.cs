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
        /// <summary>开一个新轮次；当前轮次结束后依次执行（pending 上限 4）。</summary>
        Queue,

        /// <summary>
        /// 投到当前轮次的下一步边界（DSH 的 steer）。空闲时退化为 Queue，
        /// 因为不可能插进一个不存在的活动轮次。
        /// </summary>
        Steer,

        /// <summary>与 <see cref="Steer"/> 相同，但语义上不负责唤醒（空闲时仍按 Queue 处理）。</summary>
        Inject
    }

    public sealed class AgentSessionOptions
    {
        public AgentSessionOptions()
        {
            AutoTitle = true;
        }

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

        /// <summary>一步内并行执行的只读工具上限；对齐 DSH 的 maxParallelToolCalls（默认 10）。</summary>
        public int MaxParallelToolCalls { get; set; }

        /// <summary>
        /// 目标自动续跑：一轮正常结束后，若会话目标仍是进行中且还有轮次预算，
        /// 自动接一轮 continuation。默认关闭，由 <c>agent.goalAutoContinue</c> 打开。
        /// </summary>
        public bool GoalContinuation { get; set; }

        /// <summary>
        /// 每次判断是否续跑时读取的策略（优先于 <see cref="GoalContinuation"/>）。
        /// 宿主用它把设置页开关接成实时生效，而不是等会话重建。
        /// </summary>
        public Func<bool> GoalContinuationProvider { get; set; }

        /// <summary>
        /// 每次构造工具上下文时读取的审批策略（优先于 <see cref="ApprovalPolicy"/>）。
        /// 宿主用它让“审批：每次询问 / 从不询问”在设置页改完立刻作用于下一次工具调用。
        /// </summary>
        public Func<ToolApprovalPolicy> ApprovalPolicyProvider { get; set; }

        /// <summary>
        /// 一条用户输入之后最多自动续跑几轮（0 表示不额外限制，只看目标的 maxGoalRounds）。
        /// 默认 8，防止桌面进程连续消耗额度。
        /// </summary>
        public int MaxGoalContinuationRounds { get; set; }

        /// <summary>会话级审批策略，默认 Ask。</summary>
        public ToolApprovalPolicy ApprovalPolicy { get; set; }

        /// <summary>
        /// 会话级目标状态（goal 三件套 + 轮次计数）。为 <c>null</c> 时目标工具返回不可用。
        /// 由宿主按会话创建并注入，运行时只透传到 <see cref="Tools.ToolContext"/>。
        /// </summary>
        public Goals.ISessionGoalState Goals { get; set; }

        /// <summary>
        /// 是否自动生成标题：首条消息先落一个截断回退标题，等首轮结束后再用
        /// 该轮记录的路由请求一个更短的模型标题。用户手动重命名后不再自动覆盖。
        /// </summary>
        public bool AutoTitle { get; set; }
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

        /// <summary>当前（或最近一轮）轮次内已经开始的步骤数。</summary>
        public int StepIndex { get; set; }

        public IReadOnlyList<ToolExecution> ToolExecutions { get; set; }

        public string ProviderId { get; set; }

        public string ModelId { get; set; }

        public int EstimatedContextTokens { get; set; }

        /// <summary>是否处于计划模式；开启时只暴露只读工具与 `exit_plan_mode`。</summary>
        public bool PlanMode { get; set; }
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

        /// <summary>是否处于计划模式。</summary>
        bool PlanMode { get; }

        /// <summary>
        /// 切换计划模式；下一步开始前生效（不会打断正在进行的步骤）。
        /// 进入时只暴露只读工具与 `exit_plan_mode`，退出后恢复完整工具集。
        /// </summary>
        void SetPlanMode(bool active);

        Task<AgentRun> SendAsync(AgentInput input, AgentSendMode mode, CancellationToken cancellationToken);

        Task CancelAsync(bool clearPending, CancellationToken cancellationToken);

        /// <summary>移除指定 assistant 消息并从同一条用户消息重跑。</summary>
        Task RetryAsync(string assistantMessageId, CancellationToken cancellationToken);

        /// <summary>
        /// 切换模型。宿主注入了共享 <see cref="MiaoWu.Core.Models.ModelSelection"/> 时，
        /// 改的是所有会话共用的默认模型（运行中的会话本轮保持原模型、下一条消息生效）；
        /// 没有共享选择时只改本会话，且生成中调用会抛错。
        /// </summary>
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
