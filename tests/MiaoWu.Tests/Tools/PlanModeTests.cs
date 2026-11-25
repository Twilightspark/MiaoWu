using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MiaoWu.Core.Agents.Tools;
using MiaoWu.Infrastructure.Tools;
using Xunit;

namespace MiaoWu.Tests.Tools
{
    /// <summary>计划模式：工具白名单、exit_plan_mode 的批准路径与参数校验。</summary>
    public sealed class PlanModeTests
    {
        [Fact]
        public void FilterKeepsReadOnlyToolsAndExitPlanMode()
        {
            IReadOnlyList<ToolDefinition> definitions = new[]
            {
                Definition(ToolNames.Read),
                Definition(ToolNames.Grep),
                Definition(ToolNames.Write),
                Definition(ToolNames.Pwsh),
                Definition(ToolNames.WebFetch),
                Definition(ToolNames.ExitPlanMode)
            };

            IReadOnlyList<ToolDefinition> filtered = PlanModeToolFilter.Filter(definitions, true);

            var names = new List<string>();
            for (int i = 0; i < filtered.Count; i++)
            {
                names.Add(filtered[i].Name);
            }

            Assert.Equal(4, names.Count);
            Assert.Contains(ToolNames.Read, names);
            Assert.Contains(ToolNames.Grep, names);
            Assert.Contains(ToolNames.WebFetch, names);
            Assert.Contains(ToolNames.ExitPlanMode, names);
            Assert.DoesNotContain(ToolNames.Write, names);
            Assert.DoesNotContain(ToolNames.Pwsh, names);
        }

        [Fact]
        public void FilterIsPassThroughWhenPlanModeIsOff()
        {
            IReadOnlyList<ToolDefinition> definitions = new[]
            {
                Definition(ToolNames.Write),
                Definition(ToolNames.Pwsh)
            };

            Assert.Same(definitions, PlanModeToolFilter.Filter(definitions, false));
        }

        [Fact]
        public async Task ApprovedCallExitsPlanMode()
        {
            var mode = new FakePlanMode(true);
            var tool = new ExitPlanModeTool();

            ToolResult result = await tool.ExecuteAsync(
                Call("{\"plan\":\"# 计划\\n先读文件，再改 README。\"}"),
                Context(mode),
                CancellationToken.None);

            Assert.Equal(ToolResultStatus.Succeeded, result.Status);
            Assert.True(mode.Exited);
            Assert.Contains("已退出计划模式", result.Content);
            Assert.Contains("先读文件", result.Content);
        }

        [Fact]
        public async Task InactivePlanModeOnlyRecordsThePlan()
        {
            var mode = new FakePlanMode(false);
            ToolResult result = await new ExitPlanModeTool().ExecuteAsync(
                Call("{\"plan\":\"计划正文\"}"),
                Context(mode),
                CancellationToken.None);

            Assert.Equal(ToolResultStatus.Succeeded, result.Status);
            Assert.False(mode.Exited);
            Assert.Contains("当前不在计划模式", result.Content);
            Assert.Contains("没有提交审批", result.Content);
            Assert.DoesNotContain("已获批准", result.Content);
        }

        [Fact]
        public async Task MissingPlanHostIsReported()
        {
            ToolResult result = await new ExitPlanModeTool().ExecuteAsync(
                Call("{\"plan\":\"计划正文\"}"),
                new ToolContext(),
                CancellationToken.None);

            Assert.Equal(ToolResultStatus.Failed, result.Status);
            Assert.Equal("plan_unavailable", result.ErrorCode);
        }

        [Fact]
        public async Task BlankPlanIsRejected()
        {
            var mode = new FakePlanMode(true);
            ToolResult result = await new ExitPlanModeTool().ExecuteAsync(
                Call("{\"plan\":\"   \"}"),
                Context(mode),
                CancellationToken.None);

            Assert.Equal(ToolResultStatus.InvalidArguments, result.Status);
            Assert.False(mode.Exited);
        }

        [Fact]
        public void PreviewCarriesSummaryAndFirstLine()
        {
            ToolApprovalPreview preview = new ExitPlanModeTool().Describe(
                Call("{\"plan\":\"## 两步\\n细节\"}"),
                new ToolContext());

            Assert.Equal("退出计划模式", preview.Title);
            Assert.Equal("两步", preview.Target);
            Assert.Contains("计划", preview.Summary);
        }

        private static ToolDefinition Definition(string name)
        {
            return new ToolDefinition
            {
                Name = name,
                Description = name,
                ParametersJson = "{\"type\":\"object\"}",
                Risk = ToolRisk.Low
            };
        }

        private static ToolCall Call(string argumentsJson)
        {
            return new ToolCall
            {
                Id = "call_exit_plan",
                Name = ToolNames.ExitPlanMode,
                ArgumentsJson = argumentsJson
            };
        }

        private static ToolContext Context(ISessionPlanMode mode)
        {
            return new ToolContext { Plan = mode };
        }

        private sealed class FakePlanMode : ISessionPlanMode
        {
            public FakePlanMode(bool active)
            {
                IsActive = active;
            }

            public bool IsActive { get; private set; }

            public bool Exited { get; private set; }

            public void ExitPlanMode()
            {
                Exited = true;
                IsActive = false;
            }
        }
    }
}
