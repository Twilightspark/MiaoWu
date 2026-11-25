using System.Collections.Generic;
using MiaoWu.Core.Agents.Tools;
using MiaoWu.Core.Prompt;
using Xunit;

namespace MiaoWu.Tests.Prompt
{
    /// <summary>
    /// 系统提示里的运行时状态：模式与工具数量要明确写给模型，
    /// 避免它把“计划模式裁剪掉写工具”误判成工具不存在或已经获批。
    /// </summary>
    public sealed class SystemPromptBuilderTests
    {
        [Fact]
        public void BuildWithoutStateKeepsLegacyShape()
        {
            string prompt = SystemPromptBuilder.Build(
                "你是测试助手。",
                @"D:\ws",
                Definitions("read"));

            Assert.Contains("你是测试助手。", prompt);
            Assert.Contains("<cwd>D:\\ws</cwd>", prompt);
            Assert.Contains("- read:", prompt);
            Assert.DoesNotContain("<state>", prompt);
            Assert.DoesNotContain("<plan-mode>", prompt);
        }

        [Fact]
        public void NormalModeStatesModeAndToolCount()
        {
            string prompt = SystemPromptBuilder.Build(
                "你是测试助手。",
                @"D:\ws",
                Definitions("read", "write"),
                new SystemPromptState { PlanMode = false, ToolCount = 2 });

            Assert.Contains("<state>", prompt);
            Assert.Contains("当前模式：普通模式（可读写）", prompt);
            Assert.Contains("可用工具：2 个", prompt);
            Assert.DoesNotContain("<plan-mode>", prompt);
        }

        [Fact]
        public void PlanModeExplainsTheReadOnlyToolPolicy()
        {
            string prompt = SystemPromptBuilder.Build(
                "你是测试助手。",
                @"D:\ws",
                Definitions("read"),
                new SystemPromptState { PlanMode = true, ToolCount = 1 });

            Assert.Contains("当前模式：计划模式（只读）", prompt);
            Assert.Contains("<plan-mode>", prompt);
            Assert.Contains("这是计划模式的限制，不是工具缺失", prompt);
            Assert.Contains("exit_plan_mode", prompt);
            Assert.Contains("不要声称已经写入", prompt);
            Assert.Contains("/work", prompt);
            Assert.True(
                prompt.IndexOf("<plan-mode>", System.StringComparison.Ordinal)
                    > prompt.IndexOf("<cwd>", System.StringComparison.Ordinal),
                "状态块应排在 cwd 之后，保持前面的提示前缀稳定。");
        }

        private static IReadOnlyList<ToolDefinition> Definitions(params string[] names)
        {
            var list = new List<ToolDefinition>();
            foreach (string name in names)
            {
                list.Add(new ToolDefinition
                {
                    Name = name,
                    Description = name + " 工具",
                    PromptSnippet = name + " 工具",
                    ParametersJson = "{\"type\":\"object\"}",
                    Risk = ToolRisk.Low
                });
            }

            return list;
        }
    }
}
