using System;
using System.IO;
using System.Linq;
using System.Text;
using MiaoWu.Core.Skills;
using MiaoWu.Infrastructure.Skills;
using Xunit;

namespace MiaoWu.Tests.Skills
{
    /// <summary>
    /// 程序预设技能：资源是否真的嵌进程序集、释放 / 幂等 / 删除后补回 / 同名用户技能不覆盖 / 认领旧导入。
    /// 用真实资源跑，构建时漏掉文件会在这里直接失败。
    /// </summary>
    public sealed class PresetSkillInstallerTests : IDisposable
    {
        private const string PresetId = "office-wps";

        private readonly string _root;
        private readonly string _workspace;
        private readonly string _userSkills;

        public PresetSkillInstallerTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "miaowu-presets-" + Guid.NewGuid().ToString("N"));
            _workspace = Path.Combine(_root, "workspace");
            _userSkills = Path.Combine(_root, "user-skills");
            Directory.CreateDirectory(_workspace);
            Directory.CreateDirectory(_userSkills);
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
        public void EveryPresetFileIsEmbedded()
        {
            var assembly = typeof(PresetSkillInstaller).Assembly;
            foreach (PresetSkillPackage package in PresetSkills.All)
            {
                Assert.NotEmpty(package.Files);
                foreach (PresetSkillFile file in package.Files)
                {
                    using (Stream stream = assembly.GetManifestResourceStream(file.ResourceName))
                    {
                        Assert.True(stream != null, "缺少资源：" + file.ResourceName);
                        Assert.True(stream.Length > 0, "资源为空：" + file.ResourceName);
                    }
                }
            }
        }

        [Fact]
        public void InstallDropsSkillIntoUserDirectory()
        {
            PresetSkillInstallReport report = new PresetSkillInstaller(_userSkills).Install();

            PresetSkillEntry entry = report.Entries.Single(item => item.Id == PresetId);
            Assert.Equal(PresetSkillAction.Installed, entry.Action);
            Assert.True(report.Changed);

            string target = Path.Combine(_userSkills, PresetId);
            Assert.True(File.Exists(Path.Combine(target, "SKILL.md")));
            Assert.True(File.Exists(Path.Combine(target, SkillDescriptor.PresetMarkerFileName)));
            Assert.True(File.Exists(Path.Combine(target, "examples", "word-table.json")));

            var exe = new FileInfo(Path.Combine(target, "bin", "miaowu_office.exe"));
            Assert.True(exe.Exists);
            Assert.True(exe.Length > 50 * 1024, "WPS 桥 exe 太小：" + exe.Length);
        }

        [Fact]
        public void SecondInstallKeepsUserEdits()
        {
            var installer = new PresetSkillInstaller(_userSkills);
            installer.Install();

            string body = Path.Combine(_userSkills, PresetId, "SKILL.md");
            File.AppendAllText(body, "\n\n<!-- 用户补充 -->\n", new UTF8Encoding(false));

            PresetSkillEntry entry = installer.Install().Entries.Single(item => item.Id == PresetId);

            Assert.Equal(PresetSkillAction.Skipped, entry.Action);
            Assert.Contains("用户补充", File.ReadAllText(body));
        }

        [Fact]
        public void DeletingTheSkillFolderIsRestoredOnNextStart()
        {
            var installer = new PresetSkillInstaller(_userSkills);
            installer.Install();
            Directory.Delete(Path.Combine(_userSkills, PresetId), true);

            PresetSkillEntry entry = installer.Install().Entries.Single(item => item.Id == PresetId);

            Assert.Equal(PresetSkillAction.Installed, entry.Action);
            Assert.True(File.Exists(Path.Combine(_userSkills, PresetId, "SKILL.md")));
        }

        [Fact]
        public void ForeignSkillWithSameNameIsLeftAlone()
        {
            string target = Path.Combine(_userSkills, PresetId);
            Directory.CreateDirectory(target);
            File.WriteAllText(
                Path.Combine(target, "SKILL.md"),
                "---\nname: office-wps\ndescription: 我自己的版本。\n---\n",
                new UTF8Encoding(false));

            PresetSkillEntry entry = new PresetSkillInstaller(_userSkills).Install()
                .Entries.Single(item => item.Id == PresetId);

            Assert.Equal(PresetSkillAction.Conflict, entry.Action);
            Assert.Contains("我自己的版本", File.ReadAllText(Path.Combine(target, "SKILL.md")));
            Assert.False(File.Exists(Path.Combine(target, SkillDescriptor.PresetMarkerFileName)));
            Assert.False(Directory.Exists(Path.Combine(target, "bin")));
        }

        [Fact]
        public void IdenticalImportIsAdoptedAndCompleted()
        {
            string target = Path.Combine(_userSkills, PresetId);
            Directory.CreateDirectory(target);
            byte[] body = ReadResource("MiaoWu.Presets.office-wps.SKILL.md");
            File.WriteAllBytes(Path.Combine(target, "SKILL.md"), body);

            PresetSkillEntry entry = new PresetSkillInstaller(_userSkills).Install()
                .Entries.Single(item => item.Id == PresetId);

            Assert.Equal(PresetSkillAction.Adopted, entry.Action);
            Assert.True(File.Exists(Path.Combine(target, SkillDescriptor.PresetMarkerFileName)));
            Assert.True(File.Exists(Path.Combine(target, "bin", "miaowu_office.exe")));
        }

        [Fact]
        public void CatalogLabelsSeededSkillAsPreset()
        {
            new PresetSkillInstaller(_userSkills).Install();
            string plain = Path.Combine(_userSkills, "release-notes");
            Directory.CreateDirectory(plain);
            File.WriteAllText(
                Path.Combine(plain, "SKILL.md"),
                "---\nname: release-notes\ndescription: 生成发布说明。\n---\n",
                new UTF8Encoding(false));

            SkillCatalog catalog = new SkillCatalogService(_userSkills).Load(_workspace);

            SkillDescriptor preset = catalog.Skills.Single(skill => skill.Name == PresetId);
            Assert.True(preset.IsPreset);
            Assert.True(preset.IsValid);
            Assert.Equal(SkillSource.User, preset.Source);
            Assert.False(catalog.Skills.Single(skill => skill.Name == "release-notes").IsPreset);
        }

        private static byte[] ReadResource(string name)
        {
            using (Stream stream = typeof(PresetSkillInstaller).Assembly.GetManifestResourceStream(name))
            {
                Assert.True(stream != null, "缺少资源：" + name);
                using (var buffer = new MemoryStream())
                {
                    stream.CopyTo(buffer);
                    return buffer.ToArray();
                }
            }
        }
    }
}
