using System;
using MiaoWu.Core.Agents.Tools;
using MiaoWu.Infrastructure.Tools;
using Xunit;

namespace MiaoWu.Tests.Tools
{
    /// <summary>工具行投影：标题、调用摘要与结果摘要。</summary>
    public sealed class ToolPresentationBuilderTests
    {
        [Theory]
        [InlineData(ToolNames.Read, "读取文件")]
        [InlineData(ToolNames.Pwsh, "执行命令")]
        [InlineData(ToolNames.WebSearch, "搜索网页")]
        [InlineData(ToolNames.ExitPlanMode, "退出计划模式")]
        [InlineData(ToolNames.ScheduleCreate, "新建定时任务")]
        public void TitlesMatchToolNames(string toolName, string expected)
        {
            Assert.Equal(expected, ToolPresentationBuilder.ProjectCall(toolName, null).Title);
        }

        [Fact]
        public void CallSummaryPrefersTheActionObject()
        {
            Assert.Equal("README.md",
                ToolPresentationBuilder.ProjectCall(ToolNames.Read, "{\"path\":\"README.md\"}").Summary);
            Assert.Equal("miao wu",
                ToolPresentationBuilder.ProjectCall(ToolNames.WebSearch, "{\"query\":\"miao wu\",\"max_results\":3}").Summary);
            Assert.Equal("pattern · src",
                ToolPresentationBuilder.ProjectCall(ToolNames.Grep, "{\"pattern\":\"pattern\",\"path\":\"src\"}").Summary);
            Assert.Equal("日报",
                ToolPresentationBuilder.ProjectCall(ToolNames.ScheduleCreate, "{\"title\":\"日报\",\"prompt\":\"x\"}").Summary);
            Assert.Equal("2 个文件",
                ToolPresentationBuilder.ProjectCall(ToolNames.Present, "{\"files\":[{\"path\":\"a\"},{\"path\":\"b\"}]}").Summary);

            // 计划模式只显示计划首行，不铺整段计划。
            Assert.Equal("先读再改", ToolPresentationBuilder.ProjectCall(
                ToolNames.ExitPlanMode,
                "{\"plan\":\"## 先读再改\\n细节\"}").Summary);

            // 参数不是合法 JSON 时仍要有标题，摘要留空。
            ToolPresentation broken = ToolPresentationBuilder.ProjectCall(ToolNames.Read, "{ not json");
            Assert.Equal("读取文件", broken.Title);
            Assert.Equal(string.Empty, broken.Summary);
        }

        [Fact]
        public void ResultSummariesDescribeWhatHappened()
        {
            Assert.Equal("已读取 3 行", ToolPresentationBuilder.ProjectResult(
                ToolNames.Read,
                ToolResult.Success(new ToolCall { Id = "c", Name = ToolNames.Read }, "a\nb\nc", null, true, false)));

            Assert.Equal("退出码 2", ToolPresentationBuilder.ProjectResult(
                ToolNames.Pwsh,
                ToolResult.Success(new ToolCall { Id = "c", Name = ToolNames.Pwsh }, "boom\n[exit code: 2]", null, true, false)));

            Assert.Equal("已退出计划模式", ToolPresentationBuilder.ProjectResult(
                ToolNames.ExitPlanMode,
                ToolResult.Success(new ToolCall { Id = "c", Name = ToolNames.ExitPlanMode }, "计划已获批准，已退出计划模式", null, true, false)));

            Assert.Equal("已创建", ToolPresentationBuilder.ProjectResult(
                ToolNames.ScheduleCreate,
                ToolResult.Success(new ToolCall { Id = "c", Name = ToolNames.ScheduleCreate }, "已创建定时任务", null, true, false)));

            var call = new ToolCall { Id = "c", Name = ToolNames.Read };
            Assert.Equal("超时", ToolPresentationBuilder.ProjectResult(
                ToolNames.Read,
                ToolResult.Failure(call, ToolResultStatus.Failed, "tool_timeout", "超过 30 秒")));
            Assert.Equal("已取消", ToolPresentationBuilder.ProjectResult(
                ToolNames.Read,
                ToolResult.Failure(call, ToolResultStatus.Cancelled, "cancelled", "取消")));
            Assert.Equal("已拒绝", ToolPresentationBuilder.ProjectResult(
                ToolNames.Read,
                ToolResult.Failure(call, ToolResultStatus.Denied, null, "用户拒绝")));
        }
    }
}
