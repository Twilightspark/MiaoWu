using System;
using System.Collections.Generic;
using System.IO;
using MiaoWu.Core.Agents.Goals;
using Xunit;

namespace MiaoWu.Tests.Agents.Goals
{
    public sealed class SessionGoalStateTests
    {
        private static readonly DateTimeOffset Start = new DateTimeOffset(2026, 11, 21, 9, 0, 0, TimeSpan.Zero);

        [Fact]
        public void CreateStartsActiveGoalAtRevisionOne()
        {
            FakePersistence store;
            SessionGoalState state = NewState(out store);

            GoalMutationResult result = state.Create("  完成 agent loop 对齐  ", null);

            Assert.True(result.IsSuccess, result.ErrorCode);
            Assert.StartsWith("goal_", result.Goal.Id);
            Assert.Equal("完成 agent loop 对齐", result.Goal.Objective);
            Assert.Equal(GoalPhase.Active, result.Goal.Phase);
            Assert.Equal(1, result.Goal.Revision);
            Assert.Equal(0, result.Goal.RoundsStarted);
            Assert.Equal(SessionGoalState.DefaultMaxGoalRounds, result.Goal.MaxGoalRounds);
            Assert.Equal(result.Goal.Id, store.Saved.Id);
            Assert.Equal(GoalPhase.Active, store.Saved.Phase);
        }

        [Fact]
        public void CreateRejectsSecondActiveGoal()
        {
            FakePersistence store;
            SessionGoalState state = NewState(out store);
            state.Create("第一个", null);

            GoalMutationResult second = state.Create("第二个", null);

            Assert.False(second.IsSuccess);
            Assert.Equal("goal_exists", second.ErrorCode);
            Assert.Equal("第一个", state.Current.Objective);
        }

        [Fact]
        public void CreateAllowsNewGoalAfterCompletion()
        {
            FakePersistence store;
            SessionGoalState state = NewState(out store);
            GoalMutationResult first = state.Create("第一个", null);
            state.Update(first.Goal.Id, 1, "complete", null, null, null);

            GoalMutationResult second = state.Create("第二个", 5);

            Assert.True(second.IsSuccess, second.ErrorCode);
            Assert.NotEqual(first.Goal.Id, second.Goal.Id);
            Assert.Equal(1, second.Goal.Revision);
            Assert.Equal(5, second.Goal.MaxGoalRounds);
            Assert.Equal(GoalPhase.Active, second.Goal.Phase);
        }

        [Fact]
        public void CreateValidatesObjectiveAndRoundBudget()
        {
            FakePersistence store;
            SessionGoalState state = NewState(out store);

            Assert.Equal("goal_objective_required", state.Create("   ", null).ErrorCode);
            Assert.Equal("goal_objective_too_long", state.Create(new string('x', 2001), null).ErrorCode);
            Assert.Equal("goal_rounds_out_of_range", state.Create("目标", 0).ErrorCode);
            Assert.Equal("goal_rounds_out_of_range", state.Create("目标", 1001).ErrorCode);
            Assert.Null(state.Current);
        }

        [Fact]
        public void UpdateRequiresMatchingGoalAndRevision()
        {
            FakePersistence store;
            SessionGoalState state = NewState(out store);
            GoalMutationResult created = state.Create("目标", null);

            GoalMutationResult wrongId = state.Update("goal_other", 1, "pause", null, null, null);
            Assert.Equal("goal_not_found", wrongId.ErrorCode);

            GoalMutationResult wrongRevision = state.Update(created.Goal.Id, 2, "pause", null, null, null);
            Assert.Equal("goal_revision_conflict", wrongRevision.ErrorCode);
            Assert.Contains("1", wrongRevision.ErrorMessage);

            GoalMutationResult paused = state.Update(created.Goal.Id, 1, "pause", null, null, null);
            Assert.True(paused.IsSuccess, paused.ErrorCode);
            Assert.Equal(GoalPhase.Paused, paused.Goal.Phase);
            Assert.Equal(2, paused.Goal.Revision);
        }

        [Fact]
        public void PauseResumeAndCompleteFollowTheStateMachine()
        {
            FakePersistence store;
            SessionGoalState state = NewState(out store);
            GoalMutationResult created = state.Create("目标", null);
            string id = created.Goal.Id;

            GoalMutationResult paused = state.Update(id, 1, "pause", null, null, null);
            Assert.Equal(GoalPhase.Paused, paused.Goal.Phase);

            GoalMutationResult resumed = state.Update(id, 2, "resume", null, null, null);
            Assert.Equal(GoalPhase.Active, resumed.Goal.Phase);

            GoalMutationResult completed = state.Update(id, 3, "complete", null, null, null);
            Assert.Equal(GoalPhase.Completed, completed.Goal.Phase);
            Assert.Equal(4, completed.Goal.Revision);

            Assert.Equal(
                "goal_invalid_transition",
                state.Update(id, 4, "complete", null, null, null).ErrorCode);
            Assert.Equal(
                "goal_invalid_transition",
                state.Update(id, 4, "resume", null, null, null).ErrorCode);
            Assert.Equal(
                "goal_invalid_transition",
                state.Update(id, 4, "pause", null, null, null).ErrorCode);
        }

        [Fact]
        public void BlockedRequiresThreeRoundsAndAReason()
        {
            FakePersistence store;
            SessionGoalState state = NewState(out store);
            GoalMutationResult created = state.Create("目标", null);
            string id = created.Goal.Id;

            GoalMutationResult tooEarly = state.Update(id, 1, "blocked", null, null, "等待外部依赖");
            Assert.Equal("goal_blocked_too_early", tooEarly.ErrorCode);

            state.RecordRoundStarted();
            state.RecordRoundStarted();
            state.RecordRoundStarted();

            GoalMutationResult noReason = state.Update(id, 1, "blocked", null, null, "   ");
            Assert.Equal("goal_blocked_reason_required", noReason.ErrorCode);

            GoalMutationResult blocked = state.Update(id, 1, "blocked", null, null, "等待外部依赖");
            Assert.True(blocked.IsSuccess, blocked.ErrorCode);
            Assert.Equal(GoalPhase.Blocked, blocked.Goal.Phase);
            Assert.Equal("等待外部依赖", blocked.Goal.BlockedReason);

            GoalMutationResult resumed = state.Update(id, 2, "resume", null, null, null);
            Assert.Equal(GoalPhase.Active, resumed.Goal.Phase);
            Assert.Null(resumed.Goal.BlockedReason);
        }

        [Fact]
        public void RecordRoundStartedOnlyCountsActiveGoalsAndKeepsRevision()
        {
            FakePersistence store;
            SessionGoalState state = NewState(out store);
            GoalMutationResult created = state.Create("目标", null);
            string id = created.Goal.Id;

            state.RecordRoundStarted();
            state.RecordRoundStarted();
            Assert.Equal(2, state.Current.RoundsStarted);
            Assert.Equal(1, state.Current.Revision);

            state.Update(id, 1, "pause", null, null, null);
            state.RecordRoundStarted();
            Assert.Equal(2, state.Current.RoundsStarted);

            state.Update(id, 2, "resume", null, null, null);
            state.RecordRoundStarted();
            Assert.Equal(3, state.Current.RoundsStarted);
            Assert.Equal(3, state.Current.Revision);
        }

        [Fact]
        public void EditChangesObjectiveAndBudgetOnly()
        {
            FakePersistence store;
            SessionGoalState state = NewState(out store);
            GoalMutationResult created = state.Create("旧目标", null);
            string id = created.Goal.Id;
            state.Update(id, 1, "pause", null, null, null);

            GoalMutationResult edited = state.Update(id, 2, "edit", "新目标", 5, null);

            Assert.True(edited.IsSuccess, edited.ErrorCode);
            Assert.Equal("新目标", edited.Goal.Objective);
            Assert.Equal(5, edited.Goal.MaxGoalRounds);
            Assert.Equal(GoalPhase.Paused, edited.Goal.Phase);
            Assert.Equal(3, edited.Goal.Revision);

            Assert.Equal(
                "goal_nothing_to_change",
                state.Update(id, 3, "edit", null, null, null).ErrorCode);
            Assert.Equal(
                "goal_objective_required",
                state.Update(id, 3, "edit", "  ", null, null).ErrorCode);
            Assert.Equal(
                "goal_rounds_out_of_range",
                state.Update(id, 3, "edit", null, 0, null).ErrorCode);
            Assert.Equal(
                "goal_invalid_action",
                state.Update(id, 3, "explode", null, null, null).ErrorCode);
        }

        [Fact]
        public void EditIsRejectedOnCompletedGoal()
        {
            FakePersistence store;
            SessionGoalState state = NewState(out store);
            GoalMutationResult created = state.Create("目标", null);
            state.Update(created.Goal.Id, 1, "complete", null, null, null);

            GoalMutationResult edited = state.Update(created.Goal.Id, 2, "edit", "新目标", null, null);

            Assert.Equal("goal_invalid_transition", edited.ErrorCode);
            Assert.Equal("目标", state.Current.Objective);
        }

        [Fact]
        public void PersistedGoalIsLoadedOnConstruction()
        {
            var store = new FakePersistence
            {
                LoadValue = new AgentGoal
                {
                    Id = "goal_seeded0001",
                    Objective = "来自磁盘的目标",
                    Phase = GoalPhase.Paused,
                    Revision = 3,
                    RoundsStarted = 2,
                    MaxGoalRounds = 10,
                    CreatedAt = Start,
                    UpdatedAt = Start
                }
            };

            var state = new SessionGoalState(store, () => Start);

            Assert.NotNull(state.Current);
            Assert.Equal("goal_seeded0001", state.Current.Id);
            Assert.Equal(GoalPhase.Paused, state.Current.Phase);
            Assert.Equal(3, state.Current.Revision);
        }

        [Fact]
        public void PersistenceFailureKeepsMemoryStateAndReportsReason()
        {
            var store = new FakePersistence { FailSave = true };
            var state = new SessionGoalState(store, () => Start);

            GoalMutationResult created = state.Create("目标", null);

            Assert.True(created.IsSuccess, created.ErrorCode);
            Assert.NotNull(state.Current);
            Assert.Contains("disk full", state.LastPersistenceError);
        }

        [Fact]
        public void ChangedEventCarriesIndependentSnapshots()
        {
            FakePersistence store;
            SessionGoalState state = NewState(out store);
            var seen = new List<AgentGoal>();
            state.Changed += (sender, goal) => seen.Add(goal);

            GoalMutationResult created = state.Create("目标", null);
            state.RecordRoundStarted();

            Assert.Equal(2, seen.Count);
            Assert.Equal(1, seen[0].Revision);
            Assert.Equal(1, seen[1].RoundsStarted);

            // 事件负载是副本：调用方改它不会影响状态机。
            seen[0].Objective = "被改掉了";
            Assert.Equal("目标", state.Current.Objective);
        }

        [Fact]
        public void PhaseWireNamesRoundTrip()
        {
            GoalPhase phase;
            Assert.True(GoalPhaseText.TryParse("blocked", out phase));
            Assert.Equal(GoalPhase.Blocked, phase);
            Assert.True(GoalPhaseText.TryParse(" Paused ", out phase));
            Assert.Equal(GoalPhase.Paused, phase);
            Assert.False(GoalPhaseText.TryParse("unknown", out phase));

            Assert.Equal("active", GoalPhaseText.ToWire(GoalPhase.Active));
            Assert.Equal("completed", GoalPhaseText.ToWire(GoalPhase.Completed));
            Assert.Equal("已暂停", GoalPhaseText.ToDisplay(GoalPhase.Paused));
        }

        private static SessionGoalState NewState(out FakePersistence store)
        {
            store = new FakePersistence();
            return new SessionGoalState(store, () => Start);
        }

        private sealed class FakePersistence : IGoalPersistence
        {
            public AgentGoal LoadValue { get; set; }

            public AgentGoal Saved { get; private set; }

            public bool FailSave { get; set; }

            public AgentGoal Load()
            {
                return LoadValue == null ? null : LoadValue.Clone();
            }

            public void Save(AgentGoal goal)
            {
                if (FailSave)
                {
                    throw new IOException("disk full");
                }

                Saved = goal.Clone();
            }
        }
    }
}
