using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MiaoWu.Core.Agents.Tools;
using MiaoWu.Core.Conversations;
using MiaoWu.Core.Skills;
using MiaoWu.Infrastructure.Skills;
using MiaoWu.Infrastructure.Tools;
using Xunit;

namespace MiaoWu.Tests.Skills
{
    /// <summary>技能目录：扫描 / 优先级 / 启用名单 / 导入 / 旧式根层文件 / UTF-8。</summary>
    public sealed class SkillCatalogServiceTests : IDisposable
    {
        private readonly string _root;
        private readonly string _workspace;
        private readonly string _userSkills;
        private readonly SkillCatalogService _catalog;

        public SkillCatalogServiceTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "miaowu-skills-" + Guid.NewGuid().ToString("N"));
            _workspace = Path.Combine(_root, "workspace");
            _userSkills = Path.Combine(_root, "user-skills");
            Directory.CreateDirectory(_workspace);
            Directory.CreateDirectory(_userSkills);
            _catalog = new SkillCatalogService(_userSkills);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_root, true);
            }
            catch (IOException)
            {
            }
        }

        [Fact]
        public void LoadsWorkspaceAndUserSkillsAndLetsWorkspaceWin()
        {
            WriteSkill(Path.Combine(AgentWorkspaceLayout.GetSkillsDirectory(_workspace), "pdf-tools"), "pdf-tools", "读取 PDF。");
            WriteSkill(Path.Combine(_userSkills, "pdf-tools"), "pdf-tools", "用户级 PDF。");
            WriteSkill(Path.Combine(_userSkills, "release-notes"), "release-notes", "生成发布说明。");

            SkillCatalog catalog = _catalog.Load(_workspace);

            Assert.Equal(2, catalog.Skills.Count);
            SkillDescriptor pdf = catalog.Skills.Single(skill => skill.Name == "pdf-tools");
            Assert.Equal(SkillSource.Workspace, pdf.Source);
            Assert.Contains(catalog.Diagnostics, d => d.Code == "skill.duplicate");
        }

        [Fact]
        public void InvalidSkillStaysVisibleWithDiagnostic()
        {
            string folder = Path.Combine(AgentWorkspaceLayout.GetSkillsDirectory(_workspace), "broken-skill");
            Directory.CreateDirectory(folder);
            File.WriteAllText(
                Path.Combine(folder, "SKILL.md"),
                "---\nname: broken-skill\n---\n",
                new UTF8Encoding(false));

            SkillCatalog catalog = _catalog.Load(_workspace);

            SkillDescriptor broken = Assert.Single(catalog.Skills);
            Assert.False(broken.IsValid);
            Assert.Contains("description", broken.InvalidReason);
            Assert.Contains(catalog.Diagnostics, d => d.Level == SkillDiagnosticLevel.Error);
        }

        [Fact]
        public void SetEnabledWritesWorkspaceStateAndRoundTrips()
        {
            WriteSkill(Path.Combine(AgentWorkspaceLayout.GetSkillsDirectory(_workspace), "pdf-tools"), "pdf-tools", "读取 PDF。");

            Assert.True(_catalog.SetEnabled(_workspace, "pdf-tools", false));
            string statePath = Path.Combine(AgentWorkspaceLayout.GetAgentDirectory(_workspace), "skills.json");
            Assert.True(File.Exists(statePath));
            Assert.Contains("pdf-tools", File.ReadAllText(statePath));

            SkillDescriptor disabled = _catalog.Load(_workspace).Skills.Single();
            Assert.False(disabled.IsEnabled);

            Assert.True(_catalog.SetEnabled(_workspace, "pdf-tools", true));
            Assert.True(_catalog.Load(_workspace).Skills.Single().IsEnabled);
        }

        [Fact]
        public async Task SkillToolHidesDisabledSkills()
        {
            WriteSkill(Path.Combine(AgentWorkspaceLayout.GetSkillsDirectory(_workspace), "pdf-tools"), "pdf-tools", "读取 PDF。");
            WriteSkill(Path.Combine(AgentWorkspaceLayout.GetSkillsDirectory(_workspace), "release-notes"), "release-notes", "生成发布说明。");
            Assert.True(_catalog.SetEnabled(_workspace, "pdf-tools", false));

            var tool = new SkillTool(_userSkills);
            ToolResult result = await tool.ExecuteAsync(
                new ToolCall { Id = "c1", Name = "skill", ArgumentsJson = "{}" },
                new ToolContext { WorkspaceRoot = _workspace },
                CancellationToken.None);

            Assert.Equal(ToolResultStatus.Succeeded, result.Status);
            Assert.DoesNotContain("pdf-tools", result.Content);
            Assert.Contains("release-notes", result.Content);
        }

        [Fact]
        public void ImportCopiesFolderAndRejectsDuplicate()
        {
            string source = Path.Combine(_root, "import-source", "pdf-tools");
            WriteSkill(source, "pdf-tools", "读取 PDF。");
            File.WriteAllText(Path.Combine(source, "format.md"), "格式说明\n");

            SkillImportResult result = _catalog.Import(_workspace, source);

            Assert.Equal(SkillImportStatus.Imported, result.Status);
            string target = Path.Combine(AgentWorkspaceLayout.GetSkillsDirectory(_workspace), "pdf-tools");
            Assert.True(File.Exists(Path.Combine(target, "SKILL.md")));
            Assert.True(File.Exists(Path.Combine(target, "format.md")));

            SkillImportResult duplicate = _catalog.Import(_workspace, source);
            Assert.Equal(SkillImportStatus.Conflict, duplicate.Status);
        }

        [Fact]
        public void LegacyRootMarkdownIsAccepted()
        {
            string directory = AgentWorkspaceLayout.GetSkillsDirectory(_workspace);
            Directory.CreateDirectory(directory);
            File.WriteAllText(
                Path.Combine(directory, "old-skill.md"),
                "# 旧式技能\n\n兼容根层 Markdown。\n");

            SkillDescriptor skill = Assert.Single(_catalog.Load(_workspace).Skills);

            Assert.True(skill.IsLegacy);
            Assert.Equal("old-skill", skill.Name);
            Assert.True(skill.IsValid);
        }

        [Fact]
        public void NonUtf8BodyIsInvalidAndReadBodyThrows()
        {
            string folder = Path.Combine(AgentWorkspaceLayout.GetSkillsDirectory(_workspace), "gbk-skill");
            Directory.CreateDirectory(folder);
            File.WriteAllBytes(
                Path.Combine(folder, "SKILL.md"),
                new byte[] { 0xB2, 0xE2, 0xCA, 0xD4 });

            SkillDescriptor skill = Assert.Single(_catalog.Load(_workspace).Skills);

            Assert.False(skill.IsValid);
            Assert.Contains("UTF-8", skill.InvalidReason);
            Assert.Throws<InvalidDataException>(() => _catalog.ReadBody(skill));
        }

        [Fact]
        public void ImportRequiresSkillFileAndWorkspace()
        {
            Assert.Equal(
                SkillImportStatus.NoWorkspace,
                _catalog.Import(null, _userSkills).Status);

            string plain = Path.Combine(_root, "plain");
            Directory.CreateDirectory(plain);
            Assert.Equal(
                SkillImportStatus.InvalidSource,
                _catalog.Import(_workspace, plain).Status);
        }

        private static void WriteSkill(string directory, string name, string description)
        {
            Directory.CreateDirectory(directory);
            var builder = new StringBuilder();
            builder.AppendLine("---");
            builder.AppendLine("name: " + name);
            builder.AppendLine("description: " + description);
            builder.AppendLine("---");
            builder.AppendLine();
            builder.AppendLine("# " + name);
            builder.AppendLine();
            builder.AppendLine("## 步骤");
            builder.AppendLine();
            builder.AppendLine("1. 做点什么。");
            File.WriteAllText(
                Path.Combine(directory, "SKILL.md"),
                builder.ToString(),
                new UTF8Encoding(false));
        }
    }
}
