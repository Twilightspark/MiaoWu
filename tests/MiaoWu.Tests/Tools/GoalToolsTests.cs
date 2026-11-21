using System;
using System.Threading;
using MiaoWu.Core.Agents.Goals;
using MiaoWu.Core.Agents.Tools;
using MiaoWu.Infrastructure.Json;
using MiaoWu.Infrastructure.Tools;
using Xunit;

namespace MiaoWu.Tests.Tools
{
    public sealed class GoalToolsTests
    {
        private static readonly DateTimeOffset Start = new DateTimeOffset(2026, 11, 21, 9, 0, 0, TimeSpan.Zero);

        [Fact]
        public void CreateThenGetThenCompleteRoundTripsThroughTools()
        {
            var state = new SessionGoalState(null, () => Start);
            ToolContext context = Context(state);

            ToolResult created = Execute(
                new CreateGoalTool(),
                context,
                Object(("objective", JsonValue.From("对齐 goal 工具"))));

            Assert.True(created.IsSuccess, created.Content);
            Assert.Contains("目标：goal_", created.Content);
            Assert.Contains("状态：进行中", created.Content);
            Assert.Contains("修订：1", created.Content);
            Assert.Equal(GoalPhase.Active, state.Current.Phase);

            ToolResult read = Execute(new GetGoalTool(), context, Object());
            Assert.True(read.IsSuccess, read.Content);
            Assert.Contains(state.Current.Id, read.Content);
            Assert.Contains("对齐 goal 工具", read.Content);

            ToolResult completed = Execute(
                new UpdateGoalTool(),
                context,
                Object(
                    ("goal_id", JsonValue.From(state.Current.Id)),
                    ("revision", JsonValue.From(1)),
                    ("action", JsonValue.From("complete"))));

            Assert.True(completed.IsSuccess, completed.Content);
            Assert.Contains("状态：已完成", completed.Content);
            Assert.Equal(GoalPhase.Completed, state.Current.Phase);
        }

        [Fact]
        public void GetGoalWithoutGoalIsNotAnError()
        {
            ToolContext context = Context(new SessionGoalState(null, () => Start));

            ToolResult result = Execute(new GetGoalTool(), context, Object());

            Assert.True(result.IsSuccess, result.Content);
            Assert.Contains("没有目标", result.Content);
        }

        [Fact]
        public void ToolsReportUnavailableWithoutGoalState()
        {
            ToolContext context = new ToolContext { SessionId = "s1", CallId = "c1" };

            ToolResult created = Execute(
                new CreateGoalTool(),
                context,
                Object(("objective", JsonValue.From("目标"))));
            ToolResult read = Execute(new GetGoalTool(), context, Object());
            ToolResult updated = Execute(
                new UpdateGoalTool(),
                context,
                Object(
                    ("goal_id", JsonValue.From("goal_x")),
                    ("revision", JsonValue.From(1)),
                    ("action", JsonValue.From("pause"))));

            Assert.Equal(ToolResultStatus.Failed, created.Status);
            Assert.Equal("goal_unavailable", created.ErrorCode);
            Assert.Equal("goal_unavailable", read.ErrorCode);
            Assert.Equal("goal_unavailable", updated.ErrorCode);
        }

        [Fact]
        public void CreateRejectsDuplicateActiveGoal()
        {
            var state = new SessionGoalState(null, () => Start);
            ToolContext context = Context(state);
            Execute(new CreateGoalTool(), context, Object(("objective", JsonValue.From("第一个"))));

            ToolResult second = Execute(
                new CreateGoalTool(),
                context,
                Object(("objective", JsonValue.From("第二个"))));

            Assert.False(second.IsSuccess);
            Assert.Equal("goal_exists", second.ErrorCode);
        }

        [Fact]
        public void UpdateReportsRevisionConflict()
        {
            var state = new SessionGoalState(null, () => Start);
            ToolContext context = Context(state);
            Execute(new CreateGoalTool(), context, Object(("objective", JsonValue.From("目标"))));

            ToolResult conflict = Execute(
                new UpdateGoalTool(),
                context,
                Object(
                    ("goal_id", JsonValue.From(state.Current.Id)),
                    ("revision", JsonValue.From(99)),
                    ("action", JsonValue.From("complete"))));

            Assert.False(conflict.IsSuccess);
            Assert.Equal("goal_revision_conflict", conflict.ErrorCode);
            Assert.Equal(GoalPhase.Active, state.Current.Phase);
        }

        [Fact]
        public void CreateRejectsMissingObjectiveAsInvalidArguments()
        {
            ToolContext context = Context(new SessionGoalState(null, () => Start));

            ToolResult result = Execute(new CreateGoalTool(), context, Object());

            Assert.Equal(ToolResultStatus.InvalidArguments, result.Status);
            Assert.Equal("missing_argument", result.ErrorCode);
        }

        [Fact]
        public void UpdateRejectsNonPositiveRevision()
        {
            var state = new SessionGoalState(null, () => Start);
            ToolContext context = Context(state);
            Execute(new CreateGoalTool(), context, Object(("objective", JsonValue.From("目标"))));

            ToolResult result = Execute(
                new UpdateGoalTool(),
                context,
                Object(
                    ("goal_id", JsonValue.From(state.Current.Id)),
                    ("revision", JsonValue.From(0)),
                    ("action", JsonValue.From("pause"))));

            Assert.Equal(ToolResultStatus.InvalidArguments, result.Status);
            Assert.Equal("invalid_arguments", result.ErrorCode);
        }

        [Fact]
        public void CreateAuditDoesNotCopyObjectiveText()
        {
            const string Objective = "机密目标正文";
            ToolContext context = Context(new SessionGoalState(null, () => Start));
            var tool = new CreateGoalTool();
            ToolCall call = Call("create_goal", Object(("objective", JsonValue.From(Objective))));

            string audit = ((IToolAuditRedactor)tool).GetAuditArguments(call, context);

            Assert.Contains("字符", audit);
            Assert.DoesNotContain(Objective, audit);
        }

        [Fact]
        public void UpdateAuditKeepsActionButNotBlockedReasonText()
        {
            const string Reason = "等待外部依赖的详细说明";
            ToolContext context = Context(new SessionGoalState(null, () => Start));
            var tool = new UpdateGoalTool();
            ToolCall call = Call(
                "update_goal",
                Object(
                    ("goal_id", JsonValue.From("goal_abc")),
                    ("revision", JsonValue.From(3)),
                    ("action", JsonValue.From("blocked")),
                    ("blocked_reason", JsonValue.From(Reason))));

            string audit = ((IToolAuditRedactor)tool).GetAuditArguments(call, context);

            Assert.Contains("goal_abc", audit);
            Assert.Contains("action=blocked", audit);
            Assert.Contains("revision=3", audit);
            Assert.DoesNotContain(Reason, audit);
        }

        private static ToolContext Context(ISessionGoalState goals)
        {
            return new ToolContext
            {
                SessionId = "s1",
                WorkspaceId = "w1",
                CallId = "c1",
                Goals = goals
            };
        }

        private static ToolResult Execute(IAgentTool tool, ToolContext context, JsonValue arguments)
        {
            return tool
                .ExecuteAsync(Call(tool.Name, arguments), context, CancellationToken.None)
                .GetAwaiter()
                .GetResult();
        }

        private static ToolCall Call(string name, JsonValue arguments)
        {
            return new ToolCall
            {
                Id = "c1",
                Name = name,
                ArgumentsJson = JsonWriter.Write(arguments)
            };
        }

        private static JsonValue Object(params (string Key, JsonValue Value)[] members)
        {
            JsonValue root = JsonValue.NewObject();
            for (int i = 0; i < members.Length; i++)
            {
                root[members[i].Key] = members[i].Value;
            }

            return root;
        }
    }
}
