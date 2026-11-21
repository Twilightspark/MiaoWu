using System;

namespace MiaoWu.Core.Agents.Goals
{
    /// <summary>
    /// 会话目标的阶段。对齐 DSH 的 goal 生命周期：进行中 → 暂停 / 完成 / 阻塞，
    /// 暂停与阻塞都可以再恢复。完成是终态，只能通过重新 create_goal 开始新目标。
    /// </summary>
    public enum GoalPhase
    {
        /// <summary>进行中：宿主可以继续推进（自动续跑轮次留待后续版本）。</summary>
        Active = 0,

        /// <summary>用户主动暂停：不再推进，但保留目标与轮次计数。</summary>
        Paused = 1,

        /// <summary>目标已达成，终态。</summary>
        Completed = 2,

        /// <summary>同一阻塞条件持续存在，需要用户介入；可 resume 恢复。</summary>
        Blocked = 3
    }

    /// <summary>
    /// 一个会话目标。字段是模型可见状态的一部分（`get_goal` 原样返回），
    /// 因此 <see cref="Revision"/> 承担乐观并发：每次模型侧改动都会加一，
    /// 宿主侧轮次计数不参与比较。
    /// </summary>
    public sealed class AgentGoal
    {
        /// <summary>目标 ID，形如 <c>goal_ab12cd34ef56</c>。</summary>
        public string Id { get; set; }

        /// <summary>目标正文，以模型给出的原文为准，只做首尾空白清理。</summary>
        public string Objective { get; set; }

        public GoalPhase Phase { get; set; }

        /// <summary>从 1 开始；`update_goal` 必须带当前值，否则按冲突处理。</summary>
        public long Revision { get; set; }

        /// <summary>目标生效之后开始的轮次数；由宿主在每条用户输入开启新一轮时累加。</summary>
        public int RoundsStarted { get; set; }

        /// <summary>允许的最大轮次；达到上限后宿主不再续跑（本版本只记录与展示）。</summary>
        public int MaxGoalRounds { get; set; }

        /// <summary>进入 <see cref="GoalPhase.Blocked"/> 时的阻塞原因；其他阶段为空。</summary>
        public string BlockedReason { get; set; }

        public DateTimeOffset CreatedAt { get; set; }

        public DateTimeOffset UpdatedAt { get; set; }

        /// <summary>浅拷贝；对外只暴露副本，避免调用方绕过状态机直接改字段。</summary>
        public AgentGoal Clone()
        {
            return new AgentGoal
            {
                Id = Id,
                Objective = Objective,
                Phase = Phase,
                Revision = Revision,
                RoundsStarted = RoundsStarted,
                MaxGoalRounds = MaxGoalRounds,
                BlockedReason = BlockedReason,
                CreatedAt = CreatedAt,
                UpdatedAt = UpdatedAt
            };
        }
    }

    /// <summary>
    /// 目标状态变更的结果。失败一律走结构化错误码，不抛异常给工具层。
    /// </summary>
    public sealed class GoalMutationResult
    {
        private GoalMutationResult()
        {
        }

        /// <summary>变更后的目标快照；失败时为 <c>null</c>。</summary>
        public AgentGoal Goal { get; private set; }

        /// <summary>机器可读错误码，例如 <c>goal_revision_conflict</c>；成功时为 <c>null</c>。</summary>
        public string ErrorCode { get; private set; }

        /// <summary>给模型看的一句话说明。</summary>
        public string ErrorMessage { get; private set; }

        public bool IsSuccess
        {
            get { return ErrorCode == null; }
        }

        public static GoalMutationResult Ok(AgentGoal goal)
        {
            return new GoalMutationResult { Goal = goal == null ? null : goal.Clone() };
        }

        public static GoalMutationResult Fail(string errorCode, string errorMessage)
        {
            return new GoalMutationResult
            {
                ErrorCode = errorCode,
                ErrorMessage = errorMessage
            };
        }
    }

    /// <summary>
    /// 会话级目标状态。由会话运行时实现并注入 <see cref="Tools.ToolContext"/>，
    /// 工具本身保持无状态；持久化通过 <see cref="IGoalPersistence"/> 注入。
    /// </summary>
    public interface ISessionGoalState
    {
        /// <summary>当前目标快照；没有目标时为 <c>null</c>。</summary>
        AgentGoal Current { get; }

        /// <summary>最近一次写盘失败的原因；成功或未配置持久化时为 <c>null</c>。</summary>
        string LastPersistenceError { get; }

        /// <summary>新建目标；已有进行中的目标时返回 <c>goal_exists</c>。</summary>
        GoalMutationResult Create(string objective, int? maxGoalRounds);

        /// <summary>
        /// 按 id + revision 修改目标。<paramref name="action"/> 取
        /// <c>edit</c> / <c>pause</c> / <c>resume</c> / <c>complete</c> / <c>blocked</c>。
        /// </summary>
        GoalMutationResult Update(
            string goalId,
            long revision,
            string action,
            string objective,
            int? maxGoalRounds,
            string blockedReason);

        /// <summary>每条用户输入开启新一轮时由宿主调用；只累加进行中目标的轮次。</summary>
        void RecordRoundStarted();

        /// <summary>目标状态变化（新建、修改、轮次累加）。在锁外触发。</summary>
        event EventHandler<AgentGoal> Changed;
    }

    /// <summary>
    /// 目标状态的持久化接缝。实现放在基础设施层；测试可以注入内存实现。
    /// 抛出异常时状态机保留内存副本，并把原因写进 <see cref="ISessionGoalState.LastPersistenceError"/>。
    /// </summary>
    public interface IGoalPersistence
    {
        /// <summary>读取已保存的目标；文件不存在或损坏时返回 <c>null</c>。</summary>
        AgentGoal Load();

        /// <summary>整体覆盖保存目标。</summary>
        void Save(AgentGoal goal);
    }

    /// <summary>目标阶段的线上名（JSON）与展示名（中文）。</summary>
    public static class GoalPhaseText
    {
        /// <summary>写入 JSON 的稳定小写名，与 DSH 的 phase 词表一致。</summary>
        public static string ToWire(GoalPhase phase)
        {
            switch (phase)
            {
                case GoalPhase.Paused:
                    return "paused";
                case GoalPhase.Completed:
                    return "completed";
                case GoalPhase.Blocked:
                    return "blocked";
                default:
                    return "active";
            }
        }

        /// <summary>工具结果与界面使用的中文名。</summary>
        public static string ToDisplay(GoalPhase phase)
        {
            switch (phase)
            {
                case GoalPhase.Paused:
                    return "已暂停";
                case GoalPhase.Completed:
                    return "已完成";
                case GoalPhase.Blocked:
                    return "已阻塞";
                default:
                    return "进行中";
            }
        }

        /// <summary>解析 JSON 里的阶段名；未知值返回 <c>false</c>。</summary>
        public static bool TryParse(string text, out GoalPhase phase)
        {
            phase = GoalPhase.Active;
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            switch (text.Trim().ToLowerInvariant())
            {
                case "active":
                    phase = GoalPhase.Active;
                    return true;
                case "paused":
                    phase = GoalPhase.Paused;
                    return true;
                case "completed":
                    phase = GoalPhase.Completed;
                    return true;
                case "blocked":
                    phase = GoalPhase.Blocked;
                    return true;
                default:
                    return false;
            }
        }
    }
}
