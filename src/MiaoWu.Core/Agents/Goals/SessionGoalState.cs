using System;

namespace MiaoWu.Core.Agents.Goals
{
    /// <summary>
    /// 默认的会话目标状态机：内存状态 + 可选的持久化接缝，所有校验都在锁内完成，
    /// <see cref="Changed"/> 在锁外触发，避免事件处理器回调造成死锁。
    /// </summary>
    /// <remarks>
    /// 对齐 DSH 的 goal 工具语义：
    /// <list type="bullet">
    /// <item><description>同一时刻只有一个目标；进行中 / 暂停时重复 create 会被拒绝。</description></item>
    /// <item><description>update 必须带 id + revision，防止模型基于过期快照覆盖用户改动。</description></item>
    /// <item><description>blocked 要求同一阻塞条件至少持续 <see cref="MinRoundsBeforeBlocked"/> 轮。</description></item>
    /// </list>
    /// </remarks>
    public sealed class SessionGoalState : ISessionGoalState
    {
        /// <summary>未指定上限时的默认轮次预算；对齐 DSH 的 <c>maxGoalRounds</c> 默认值 256。</summary>
        public const int DefaultMaxGoalRounds = 256;

        /// <summary>轮次预算上限；防止在桌面进程里出现无界续跑。</summary>
        public const int MaxGoalRoundsLimit = 1000;

        /// <summary>目标正文长度上限。</summary>
        public const int MaxObjectiveLength = 2000;

        /// <summary>允许标记 blocked 之前必须已经开始的轮次数。</summary>
        public const int MinRoundsBeforeBlocked = 3;

        private readonly object _gate = new object();
        private readonly IGoalPersistence _persistence;
        private readonly Func<DateTimeOffset> _clock;
        private AgentGoal _current;
        private string _lastPersistenceError;

        /// <summary>纯内存状态，不持久化（测试与无盘场景）。</summary>
        public SessionGoalState()
            : this(null, null)
        {
        }

        /// <summary>从 <paramref name="persistence"/> 载入目标；为 <c>null</c> 时只保存在内存里。</summary>
        public SessionGoalState(IGoalPersistence persistence)
            : this(persistence, null)
        {
        }

        /// <summary>
        /// <paramref name="clock"/> 用于测试注入固定时间；为 <c>null</c> 时使用本地当前时间。
        /// </summary>
        public SessionGoalState(IGoalPersistence persistence, Func<DateTimeOffset> clock)
        {
            _persistence = persistence;
            _clock = clock ?? (() => DateTimeOffset.Now);
            _current = LoadPersisted();
        }

        public event EventHandler<AgentGoal> Changed;

        public AgentGoal Current
        {
            get
            {
                lock (_gate)
                {
                    return _current == null ? null : _current.Clone();
                }
            }
        }

        public string LastPersistenceError
        {
            get
            {
                lock (_gate)
                {
                    return _lastPersistenceError;
                }
            }
        }

        public GoalMutationResult Create(string objective, int? maxGoalRounds)
        {
            string normalized = NormalizeObjective(objective);
            if (normalized.Length == 0)
            {
                return GoalMutationResult.Fail("goal_objective_required", "objective 不能为空。");
            }

            if (normalized.Length > MaxObjectiveLength)
            {
                return GoalMutationResult.Fail(
                    "goal_objective_too_long",
                    "objective 超过 " + MaxObjectiveLength + " 字符。");
            }

            int rounds = maxGoalRounds ?? DefaultMaxGoalRounds;
            if (rounds < 1 || rounds > MaxGoalRoundsLimit)
            {
                return GoalMutationResult.Fail(
                    "goal_rounds_out_of_range",
                    "max_goal_rounds 必须在 1 到 " + MaxGoalRoundsLimit + " 之间。");
            }

            AgentGoal created;
            lock (_gate)
            {
                if (_current != null
                    && (_current.Phase == GoalPhase.Active || _current.Phase == GoalPhase.Paused))
                {
                    return GoalMutationResult.Fail(
                        "goal_exists",
                        "已有目标 " + _current.Id + "（" + GoalPhaseText.ToDisplay(_current.Phase)
                        + "）。先用 update_goal 暂停或完成它，再建新目标。");
                }

                DateTimeOffset now = _clock();
                created = new AgentGoal
                {
                    Id = NewGoalId(),
                    Objective = normalized,
                    Phase = GoalPhase.Active,
                    Revision = 1,
                    RoundsStarted = 0,
                    MaxGoalRounds = rounds,
                    CreatedAt = now,
                    UpdatedAt = now
                };

                _current = created;
                Persist(created);
            }

            RaiseChanged(created);
            return GoalMutationResult.Ok(created);
        }

        public GoalMutationResult Update(
            string goalId,
            long revision,
            string action,
            string objective,
            int? maxGoalRounds,
            string blockedReason)
        {
            string normalizedAction = (action ?? string.Empty).Trim().ToLowerInvariant();
            AgentGoal changed = null;
            GoalMutationResult result;

            lock (_gate)
            {
                if (_current == null)
                {
                    return GoalMutationResult.Fail("goal_not_found", "当前会话没有目标；先用 create_goal 创建。");
                }

                if (!string.Equals((goalId ?? string.Empty).Trim(), _current.Id, StringComparison.Ordinal))
                {
                    return GoalMutationResult.Fail(
                        "goal_not_found",
                        "没有这个目标；当前目标是 " + _current.Id + "。");
                }

                if (revision != _current.Revision)
                {
                    return GoalMutationResult.Fail(
                        "goal_revision_conflict",
                        "修订已变化：当前 revision 是 " + _current.Revision
                        + "。先 get_goal 读取最新状态再重试。");
                }

                AgentGoal next = _current.Clone();
                switch (normalizedAction)
                {
                    case "edit":
                        result = ApplyEdit(next, objective, maxGoalRounds);
                        break;

                    case "pause":
                        if (next.Phase != GoalPhase.Active)
                        {
                            result = InvalidTransition(next, "只有进行中的目标可以暂停。");
                        }
                        else
                        {
                            next.Phase = GoalPhase.Paused;
                            result = null;
                        }

                        break;

                    case "resume":
                        if (next.Phase != GoalPhase.Paused && next.Phase != GoalPhase.Blocked)
                        {
                            result = InvalidTransition(next, "只有已暂停或已阻塞的目标可以恢复。");
                        }
                        else
                        {
                            next.Phase = GoalPhase.Active;
                            next.BlockedReason = null;
                            result = null;
                        }

                        break;

                    case "complete":
                        if (next.Phase == GoalPhase.Completed)
                        {
                            result = InvalidTransition(next, "目标已经完成；用 create_goal 开始新目标。");
                        }
                        else
                        {
                            next.Phase = GoalPhase.Completed;
                            next.BlockedReason = null;
                            result = null;
                        }

                        break;

                    case "blocked":
                        result = ApplyBlocked(next, blockedReason);
                        break;

                    case "":
                        result = GoalMutationResult.Fail(
                            "goal_invalid_action",
                            "action 不能为空，取值为 edit / pause / resume / complete / blocked。");
                        break;

                    default:
                        result = GoalMutationResult.Fail(
                            "goal_invalid_action",
                            "未知 action：" + normalizedAction + "；取值为 edit / pause / resume / complete / blocked。");
                        break;
                }

                if (result != null)
                {
                    return result;
                }

                next.Revision = _current.Revision + 1;
                next.UpdatedAt = _clock();
                _current = next;
                Persist(next);
                changed = next.Clone();
            }

            RaiseChanged(changed);
            return GoalMutationResult.Ok(changed);
        }

        public void RecordRoundStarted()
        {
            AgentGoal changed = null;
            lock (_gate)
            {
                if (_current == null || _current.Phase != GoalPhase.Active)
                {
                    return;
                }

                // 轮次计数是宿主侧事实，不参与 revision 比较，避免每轮都把模型的快照作废。
                _current.RoundsStarted++;
                _current.UpdatedAt = _clock();
                Persist(_current);
                changed = _current.Clone();
            }

            RaiseChanged(changed);
        }

        private static GoalMutationResult ApplyEdit(
            AgentGoal next,
            string objective,
            int? maxGoalRounds)
        {
            if (objective == null && !maxGoalRounds.HasValue)
            {
                return GoalMutationResult.Fail(
                    "goal_nothing_to_change",
                    "edit 至少要改 objective 或 max_goal_rounds 之一。");
            }

            if (next.Phase == GoalPhase.Completed)
            {
                return InvalidTransition(next, "已完成的目标不能再编辑；用 create_goal 开始新目标。");
            }

            if (objective != null)
            {
                string normalized = NormalizeObjective(objective);
                if (normalized.Length == 0)
                {
                    return GoalMutationResult.Fail("goal_objective_required", "objective 不能改成空。");
                }

                if (normalized.Length > MaxObjectiveLength)
                {
                    return GoalMutationResult.Fail(
                        "goal_objective_too_long",
                        "objective 超过 " + MaxObjectiveLength + " 字符。");
                }

                next.Objective = normalized;
            }

            if (maxGoalRounds.HasValue)
            {
                int rounds = maxGoalRounds.Value;
                if (rounds < 1 || rounds > MaxGoalRoundsLimit)
                {
                    return GoalMutationResult.Fail(
                        "goal_rounds_out_of_range",
                        "max_goal_rounds 必须在 1 到 " + MaxGoalRoundsLimit + " 之间。");
                }

                next.MaxGoalRounds = rounds;
            }

            return null;
        }

        private static GoalMutationResult ApplyBlocked(AgentGoal next, string blockedReason)
        {
            if (next.Phase != GoalPhase.Active)
            {
                return InvalidTransition(next, "只有进行中的目标可以标记 blocked。");
            }

            string reason = (blockedReason ?? string.Empty).Trim();
            if (reason.Length == 0)
            {
                return GoalMutationResult.Fail(
                    "goal_blocked_reason_required",
                    "标记 blocked 必须给 blocked_reason，写清具体阻塞条件。");
            }

            if (next.RoundsStarted < MinRoundsBeforeBlocked)
            {
                return GoalMutationResult.Fail(
                    "goal_blocked_too_early",
                    "同一阻塞条件至少持续 " + MinRoundsBeforeBlocked
                    + " 轮后才允许标记 blocked（当前 " + next.RoundsStarted + " 轮）。");
            }

            next.Phase = GoalPhase.Blocked;
            next.BlockedReason = reason;
            return null;
        }

        private static GoalMutationResult InvalidTransition(AgentGoal goal, string message)
        {
            return GoalMutationResult.Fail(
                "goal_invalid_transition",
                message + "当前状态：" + GoalPhaseText.ToDisplay(goal.Phase) + "。");
        }

        private static string NormalizeObjective(string objective)
        {
            return (objective ?? string.Empty).Trim();
        }

        private static string NewGoalId()
        {
            return "goal_" + Guid.NewGuid().ToString("N").Substring(0, 12);
        }

        private AgentGoal LoadPersisted()
        {
            if (_persistence == null)
            {
                return null;
            }

            try
            {
                AgentGoal goal = _persistence.Load();
                if (goal == null
                    || string.IsNullOrWhiteSpace(goal.Id)
                    || string.IsNullOrWhiteSpace(goal.Objective))
                {
                    return null;
                }

                if (goal.Revision < 1)
                {
                    goal.Revision = 1;
                }

                if (goal.MaxGoalRounds < 1)
                {
                    goal.MaxGoalRounds = DefaultMaxGoalRounds;
                }

                if (goal.RoundsStarted < 0)
                {
                    goal.RoundsStarted = 0;
                }

                return goal;
            }
            catch (Exception exception)
            {
                _lastPersistenceError = exception.Message;
                return null;
            }
        }

        private void Persist(AgentGoal goal)
        {
            if (_persistence == null)
            {
                return;
            }

            try
            {
                _persistence.Save(goal.Clone());
                _lastPersistenceError = null;
            }
            catch (Exception exception)
            {
                // 写盘失败不回滚内存状态：目标在本进程内仍然有效，原因交给工具结果展示。
                _lastPersistenceError = exception.Message;
            }
        }

        private void RaiseChanged(AgentGoal goal)
        {
            EventHandler<AgentGoal> handler = Changed;
            if (handler != null && goal != null)
            {
                handler(this, goal.Clone());
            }
        }
    }
}
