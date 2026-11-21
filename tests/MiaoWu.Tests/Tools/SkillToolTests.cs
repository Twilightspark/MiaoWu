using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MiaoWu.Core.Agents.Tools;
using MiaoWu.Infrastructure.Tools;
using Xunit;

namespace MiaoWu.Tests.Tools
{
    public sealed class SkillToolTests : IDisposable
    {
        private readonly string _workspace;
        private readonly string _userSkills;
        private readonly SkillTool _tool;

        public SkillToolTests()
        {
            string root = Path.Combine(Path.GetTempPath(), "miaowu-skill-" + Guid.NewGuid().ToString("N"));
            _workspace = Path.Combine(root, "ws");
            _userSkills = Path.Combine(root, "user-skills");
            Directory.CreateDirectory(Path.Combine(_workspace, ".agent", "skills"));
            Directory.CreateDirectory(_userSkills);
            _tool = new SkillTool(_userSkills);
        }

        public void Dispose()
        {
            try
            {
                string root = Path.GetDirectoryName(_workspace);
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, true);
                }
            }
            catch (Exception)
            {
            }
        }

        [Fact]
        public async Task ListsWorkspaceAndUserSkillsWithWorkspaceWinning()
        {
            Directory.CreateDirectory(_userSkills);
            File.WriteAllText(Path.Combine(_userSkills, "review.md"), "# Review\n用户级审查技能。");
            File.WriteAllText(Path.Combine(_userSkills, "shared.md"), "# Shared\n用户级版本。");
            File.WriteAllText(
                Path.Combine(_workspace, ".agent", "skills", "shared.md"),
                "# Shared\n工作区版本。");

            ToolResult result = await ExecuteAsync(null);

            Assert.True(result.IsSuccess);
            Assert.Contains("review", result.Content);
            Assert.Single(result.Content.Split('\n'), line => line.Contains("shared"));
            Assert.Contains("review：Review", result.Content);
        }

        [Fact]
        public async Task LoadsSkillBodyByName()
        {
            File.WriteAllText(
                Path.Combine(_workspace, ".agent", "skills", "deploy.md"),
                "# Deploy\n先跑测试，再打包。");

            ToolResult result = await ExecuteAsync("{\"name\":\"deploy\"}");

            Assert.True(result.IsSuccess);
            Assert.Contains("先跑测试，再打包。", result.Content);
            Assert.Equal("加载技能 deploy", result.Summary);
        }

        [Fact]
        public async Task UnknownSkillFailsWithAvailableNames()
        {
            ToolResult result = await ExecuteAsync("{\"name\":\"missing\"}");

            Assert.Equal(ToolResultStatus.Failed, result.Status);
            Assert.Equal("skill_not_found", result.ErrorCode);
            Assert.Contains("missing", result.ErrorMessage);
        }

        [Fact]
        public async Task EmptyCatalogSaysSo()
        {
            ToolResult result = await ExecuteAsync(null);

            Assert.True(result.IsSuccess);
            Assert.Contains("没有可用技能", result.Content);
        }

        private Task<ToolResult> ExecuteAsync(string argumentsJson)
        {
            var context = new ToolContext
            {
                SessionId = "s1",
                WorkspaceId = "w1",
                WorkspaceRoot = _workspace,
                Now = DateTimeOffset.UtcNow
            };

            var call = new ToolCall
            {
                Id = "call-1",
                Name = ToolNames.Skill,
                ArgumentsJson = argumentsJson
            };

            return _tool.ExecuteAsync(call, context, CancellationToken.None);
        }
    }
}
