using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using MiaoWu.Core.Conversations;
using MiaoWu.Core.Skills;
using MiaoWu.Core.Text;
using MiaoWu.Infrastructure.Json;

namespace MiaoWu.Infrastructure.Skills
{
    /// <summary>
    /// 技能目录的扫描与写路径。技能有两种布局：
    /// 文件夹 + <c>SKILL.md</c>（推荐），以及旧式根层 <c>name.md</c>。
    /// 工作区技能优先于用户级技能，同名时后者只记一条覆盖诊断。
    /// </summary>
    public sealed class SkillCatalogService : ISkillCatalogService
    {
        public const string SkillFileName = "SKILL.md";
        public const string StateFileName = "skills.json";
        public const string SchemaVersionKey = "schemaVersion";
        public const string DisabledKey = "disabled";

        private const int MaxBodyBytes = 512 * 1024;
        private const int MaxImportFiles = 200;
        private const long MaxImportBytes = 5 * 1024 * 1024;
        private const int MaxFilesListed = 50;

        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);

        private static readonly Regex SkillNamePattern = new Regex(
            "^[a-z0-9](?:[a-z0-9-]{0,62}[a-z0-9])?$",
            RegexOptions.CultureInvariant | RegexOptions.Compiled);

        private readonly string _userSkillsDirectory;

        public SkillCatalogService(string userSkillsDirectory)
        {
            _userSkillsDirectory = string.IsNullOrWhiteSpace(userSkillsDirectory)
                ? null
                : Path.GetFullPath(userSkillsDirectory);
        }

        /// <summary>用户级技能目录；没有配置时只扫描工作区技能。</summary>
        public string UserSkillsDirectory
        {
            get { return _userSkillsDirectory ?? string.Empty; }
        }

        public SkillCatalog Load(string workspaceRoot)
        {
            var catalog = new SkillCatalog(workspaceRoot);
            var diagnostics = new List<SkillDiagnostic>();
            var merged = new List<SkillDescriptor>();

            string workspaceSkills = null;
            if (!string.IsNullOrWhiteSpace(workspaceRoot))
            {
                try
                {
                    workspaceSkills = AgentWorkspaceLayout.GetSkillsDirectory(workspaceRoot);
                }
                catch (Exception exception)
                {
                    diagnostics.Add(new SkillDiagnostic(
                        SkillDiagnosticLevel.Error,
                        "skill.workspace",
                        "工作区路径不可用：" + exception.Message));
                }
            }

            List<string> disabled = LoadDisabled(workspaceRoot, diagnostics);
            var seen = new Dictionary<string, SkillDescriptor>(StringComparer.OrdinalIgnoreCase);

            AddDirectory(merged, seen, diagnostics, workspaceSkills, SkillSource.Workspace);
            AddDirectory(merged, seen, diagnostics, _userSkillsDirectory, SkillSource.User);

            var enabled = new HashSet<string>(disabled, StringComparer.OrdinalIgnoreCase);
            foreach (SkillDescriptor skill in merged)
            {
                skill.IsEnabled = !enabled.Contains(skill.Name);
            }

            merged.Sort(CompareSkills);

            catalog.Skills = merged;
            catalog.Diagnostics = diagnostics;
            catalog.Disabled = disabled;
            catalog.ScannedAt = DateTimeOffset.Now;
            return catalog;
        }

        public bool SetEnabled(string workspaceRoot, string name, bool enabled)
        {
            if (string.IsNullOrWhiteSpace(workspaceRoot) || string.IsNullOrWhiteSpace(name)
                || !SkillNamePattern.IsMatch(name.Trim().ToLowerInvariant()))
            {
                return false;
            }

            string normalized = name.Trim().ToLowerInvariant();
            List<string> disabled = LoadDisabled(workspaceRoot, null);
            var set = new HashSet<string>(disabled, StringComparer.OrdinalIgnoreCase);
            if (enabled)
            {
                set.Remove(normalized);
            }
            else
            {
                set.Add(normalized);
            }

            return WriteDisabled(workspaceRoot, set);
        }

        public SkillImportResult Import(string workspaceRoot, string sourceDirectory)
        {
            if (string.IsNullOrWhiteSpace(workspaceRoot))
            {
                return new SkillImportResult
                {
                    Status = SkillImportStatus.NoWorkspace,
                    Message = "先选择一个工作区，再导入技能。"
                };
            }

            if (string.IsNullOrWhiteSpace(sourceDirectory) || !Directory.Exists(sourceDirectory))
            {
                return new SkillImportResult
                {
                    Status = SkillImportStatus.InvalidSource,
                    Message = "选择的文件夹不存在。"
                };
            }

            string source;
            string skillsRoot;
            try
            {
                source = Path.GetFullPath(sourceDirectory)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                skillsRoot = Path.GetFullPath(AgentWorkspaceLayout.GetSkillsDirectory(workspaceRoot))
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            catch (Exception exception)
            {
                return new SkillImportResult
                {
                    Status = SkillImportStatus.Failed,
                    Message = "路径不可用：" + exception.Message
                };
            }

            string bodyPath = Path.Combine(source, SkillFileName);
            if (!File.Exists(bodyPath))
            {
                return new SkillImportResult
                {
                    Status = SkillImportStatus.InvalidSource,
                    Message = "这个文件夹里没有 SKILL.md。请选择技能文件夹本身。"
                };
            }

            string name = NormalizeImportName(ParseFrontMatter(ReadTextOrEmpty(bodyPath)).Name, Path.GetFileName(source));
            if (!SkillNamePattern.IsMatch(name))
            {
                return new SkillImportResult
                {
                    Status = SkillImportStatus.InvalidSource,
                    Message = "技能名称只能使用小写字母、数字与连字符（例如 pdf-tools）。"
                };
            }

            string target = Path.GetFullPath(Path.Combine(skillsRoot, name));
            if (!IsInside(skillsRoot, target))
            {
                return new SkillImportResult
                {
                    Status = SkillImportStatus.Failed,
                    Message = "技能目录不可用。"
                };
            }

            if (Directory.Exists(target) || File.Exists(target))
            {
                return new SkillImportResult
                {
                    Status = SkillImportStatus.Conflict,
                    Name = name,
                    Message = "已存在同名技能「" + name + "」。先删除原技能或换一个名称。"
                };
            }

            int fileCount = 0;
            long totalBytes = 0;
            List<string> files;
            try
            {
                files = Directory.GetFiles(source, "*", SearchOption.AllDirectories).ToList();
                foreach (string file in files)
                {
                    var info = new FileInfo(file);
                    fileCount++;
                    totalBytes += info.Length;
                }
            }
            catch (Exception exception)
            {
                return new SkillImportResult
                {
                    Status = SkillImportStatus.Failed,
                    Message = "读取技能文件夹失败：" + exception.Message
                };
            }

            if (fileCount > MaxImportFiles || totalBytes > MaxImportBytes)
            {
                return new SkillImportResult
                {
                    Status = SkillImportStatus.Failed,
                    Message = "技能文件夹太大（最多 " + MaxImportFiles + " 个文件 / 5 MB）。"
                };
            }

            try
            {
                Directory.CreateDirectory(skillsRoot);
                Directory.CreateDirectory(target);
                foreach (string file in files)
                {
                    string relative = file.Substring(source.Length)
                        .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    string destination = Path.GetFullPath(Path.Combine(target, relative));
                    if (!IsInside(target, destination))
                    {
                        continue;
                    }

                    string parent = Path.GetDirectoryName(destination);
                    if (!string.IsNullOrEmpty(parent))
                    {
                        Directory.CreateDirectory(parent);
                    }

                    File.Copy(file, destination, false);
                }
            }
            catch (Exception exception)
            {
                TryDeleteDirectory(target);
                return new SkillImportResult
                {
                    Status = SkillImportStatus.Failed,
                    Name = name,
                    Message = "导入失败：" + exception.Message
                };
            }

            SkillDescriptor imported = Load(workspaceRoot).Skills
                .FirstOrDefault(skill => string.Equals(skill.Name, name, StringComparison.OrdinalIgnoreCase)
                    && skill.Source == SkillSource.Workspace);

            return new SkillImportResult
            {
                Status = SkillImportStatus.Imported,
                Name = name,
                Skill = imported,
                Message = "已导入「" + name + "」。"
            };
        }

        public string ReadBody(SkillDescriptor skill)
        {
            if (skill == null || string.IsNullOrWhiteSpace(skill.BodyPath) || !File.Exists(skill.BodyPath))
            {
                throw new InvalidDataException("技能正文不存在。");
            }

            var info = new FileInfo(skill.BodyPath);
            if (info.Length > MaxBodyBytes)
            {
                throw new InvalidDataException("技能正文超过 512 KB，请在编辑器中打开。");
            }

            byte[] bytes = File.ReadAllBytes(skill.BodyPath);
            Utf8TextInfo text = Utf8Text.Read(bytes);
            if (!text.IsValid)
            {
                throw new InvalidDataException("不是 UTF-8 文本。");
            }

            return text.Text.TrimEnd();
        }

        private void AddDirectory(
            List<SkillDescriptor> skills,
            Dictionary<string, SkillDescriptor> seen,
            List<SkillDiagnostic> diagnostics,
            string directory,
            SkillSource source)
        {
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            {
                return;
            }

            try
            {
                string[] folders = Directory.GetDirectories(directory);
                Array.Sort(folders, StringComparer.OrdinalIgnoreCase);
                foreach (string folder in folders)
                {
                    string body = Path.Combine(folder, SkillFileName);
                    if (!File.Exists(body))
                    {
                        continue;
                    }

                    AddSkill(skills, seen, diagnostics, folder, body, source, false);
                }

                string[] files = Directory.GetFiles(directory, "*.md", SearchOption.TopDirectoryOnly);
                Array.Sort(files, StringComparer.OrdinalIgnoreCase);
                foreach (string file in files)
                {
                    if (string.Equals(Path.GetFileName(file), SkillFileName, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    AddSkill(skills, seen, diagnostics, null, file, source, true);
                }
            }
            catch (Exception exception)
            {
                diagnostics.Add(new SkillDiagnostic(
                    SkillDiagnosticLevel.Error,
                    "skill.scan",
                    "读取技能目录失败：" + exception.Message,
                    directory));
            }
        }

        private static void AddSkill(
            List<SkillDescriptor> skills,
            Dictionary<string, SkillDescriptor> seen,
            List<SkillDiagnostic> diagnostics,
            string directory,
            string bodyPath,
            SkillSource source,
            bool legacy)
        {
            SkillDescriptor skill = ReadSkill(directory, bodyPath, source, legacy);
            SkillDescriptor existing;
            if (seen.TryGetValue(skill.Name, out existing))
            {
                string message = source == SkillSource.User
                    ? "同名用户级技能被工作区技能覆盖。"
                    : "同名技能被覆盖。";

                var merged = new List<SkillDiagnostic>(existing.Diagnostics ?? new SkillDiagnostic[0])
                {
                    new SkillDiagnostic(
                        SkillDiagnosticLevel.Warning,
                        "skill.duplicate",
                        message,
                        bodyPath)
                };
                existing.Diagnostics = merged;
                diagnostics.Add(new SkillDiagnostic(
                    SkillDiagnosticLevel.Warning,
                    "skill.duplicate",
                    "「" + skill.Name + "」被覆盖：" + bodyPath));
                return;
            }

            seen[skill.Name] = skill;
            skills.Add(skill);
            if (!skill.IsValid)
            {
                diagnostics.Add(new SkillDiagnostic(
                    SkillDiagnosticLevel.Error,
                    "skill.invalid",
                    skill.Name + "：" + (skill.InvalidReason ?? "无法加载"),
                    bodyPath));
            }

            foreach (SkillDiagnostic diagnostic in skill.Diagnostics ?? new SkillDiagnostic[0])
            {
                diagnostics.Add(diagnostic);
            }
        }

        private static SkillDescriptor ReadSkill(
            string directory,
            string bodyPath,
            SkillSource source,
            bool legacy)
        {
            string fallbackName = legacy
                ? Path.GetFileNameWithoutExtension(bodyPath)
                : new DirectoryInfo(directory).Name;

            var skill = new SkillDescriptor
            {
                Name = fallbackName,
                Source = source,
                BodyPath = bodyPath,
                DirectoryPath = directory,
                IsLegacy = legacy,
                IsValid = false,
                Diagnostics = new List<SkillDiagnostic>()
            };

            try
            {
                var info = new FileInfo(bodyPath);
                skill.UpdatedAt = new DateTimeOffset(info.LastWriteTime);
                skill.SizeBytes = info.Length;

                byte[] bytes = File.ReadAllBytes(bodyPath);
                Utf8TextInfo text = Utf8Text.Read(bytes);
                if (!text.IsValid)
                {
                    skill.InvalidReason = "不是 UTF-8 文本";
                    return skill;
                }

                skill.LineCount = CountLines(text.Text);
                FrontMatter front = ParseFrontMatter(text.Text);
                if (!string.IsNullOrWhiteSpace(front.Name))
                {
                    skill.Name = front.Name.Trim();
                }

                skill.Description = string.IsNullOrWhiteSpace(front.Description)
                    ? ExtractDescription(text.Text)
                    : front.Description.Trim();

                if (string.IsNullOrWhiteSpace(skill.Name))
                {
                    skill.InvalidReason = "缺少名称";
                    return skill;
                }

                if (string.IsNullOrWhiteSpace(skill.Description))
                {
                    skill.InvalidReason = "缺少 description";
                    return skill;
                }

                if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
                {
                    skill.Files = ListFiles(directory);
                }
                else
                {
                    skill.Files = new List<SkillFileEntry>
                    {
                        new SkillFileEntry(Path.GetFileName(bodyPath), bodyPath, skill.SizeBytes)
                    };
                }

                skill.IsValid = true;
            }
            catch (Exception exception)
            {
                skill.InvalidReason = exception.Message;
                skill.Diagnostics = new List<SkillDiagnostic>
                {
                    new SkillDiagnostic(
                        SkillDiagnosticLevel.Error,
                        "skill.read",
                        "读取失败：" + exception.Message,
                        bodyPath)
                };
            }

            return skill;
        }

        private static IReadOnlyList<SkillFileEntry> ListFiles(string directory)
        {
            var files = new List<SkillFileEntry>();
            try
            {
                string root = Path.GetFullPath(directory)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string[] all = Directory.GetFiles(directory, "*", SearchOption.AllDirectories);
                Array.Sort(all, CompareSkillFiles);
                foreach (string file in all)
                {
                    if (files.Count >= MaxFilesListed)
                    {
                        break;
                    }

                    string full = Path.GetFullPath(file);
                    if (!IsInside(root, full))
                    {
                        continue;
                    }

                    string relative = full.Substring(root.Length)
                        .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    var info = new FileInfo(full);
                    files.Add(new SkillFileEntry(relative, full, info.Length));
                }
            }
            catch (Exception)
            {
            }

            return files;
        }

        private static List<string> LoadDisabled(string workspaceRoot, List<SkillDiagnostic> diagnostics)
        {
            var disabled = new List<string>();
            if (string.IsNullOrWhiteSpace(workspaceRoot))
            {
                return disabled;
            }

            string path;
            try
            {
                path = Path.Combine(AgentWorkspaceLayout.GetAgentDirectory(workspaceRoot), StateFileName);
            }
            catch (Exception)
            {
                return disabled;
            }

            if (!File.Exists(path))
            {
                return disabled;
            }

            try
            {
                JsonValue root = JsonParser.Parse(File.ReadAllText(path, Utf8NoBom));
                JsonValue list = root == null ? null : root.Get(DisabledKey);
                if (list == null || list.Kind != JsonKind.Array)
                {
                    if (diagnostics != null)
                    {
                        diagnostics.Add(new SkillDiagnostic(
                            SkillDiagnosticLevel.Warning,
                            "skill.state",
                            "技能启用状态格式不完整，已按全部启用处理。",
                            path));
                    }

                    return disabled;
                }

                foreach (JsonValue item in list.Items)
                {
                    string name = item == null ? null : item.AsString(null);
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        disabled.Add(name.Trim().ToLowerInvariant());
                    }
                }
            }
            catch (Exception exception)
            {
                if (diagnostics != null)
                {
                    diagnostics.Add(new SkillDiagnostic(
                        SkillDiagnosticLevel.Warning,
                        "skill.state",
                        "技能启用状态读取失败，已按全部启用处理：" + exception.Message,
                        path));
                }
            }

            return disabled;
        }

        private static bool WriteDisabled(string workspaceRoot, HashSet<string> disabled)
        {
            try
            {
                string agent = AgentWorkspaceLayout.GetAgentDirectory(workspaceRoot);
                Directory.CreateDirectory(agent);
                string path = Path.Combine(agent, StateFileName);

                JsonValue root = JsonValue.NewObject();
                root[SchemaVersionKey] = JsonValue.From(1);
                JsonValue list = JsonValue.NewArray();
                foreach (string name in disabled.OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
                {
                    list.Add(JsonValue.From(name));
                }

                root[DisabledKey] = list;
                string text = JsonWriter.WriteIndented(root) + "\n";
                string temp = path + ".tmp";
                File.WriteAllText(temp, text, Utf8NoBom);
                if (File.Exists(path))
                {
                    try
                    {
                        File.Replace(temp, path, null);
                    }
                    catch (Exception)
                    {
                        File.Copy(temp, path, true);
                        File.Delete(temp);
                    }
                }
                else
                {
                    File.Move(temp, path);
                }

                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static int CompareSkillFiles(string left, string right)
        {
            bool leftBody = string.Equals(Path.GetFileName(left), SkillFileName, StringComparison.OrdinalIgnoreCase);
            bool rightBody = string.Equals(Path.GetFileName(right), SkillFileName, StringComparison.OrdinalIgnoreCase);
            if (leftBody != rightBody)
            {
                return leftBody ? -1 : 1;
            }

            return string.Compare(left, right, StringComparison.OrdinalIgnoreCase);
        }

        private static int CompareSkills(SkillDescriptor left, SkillDescriptor right)
        {            if (left.IsValid != right.IsValid)
            {
                return left.IsValid ? -1 : 1;
            }

            if (left.Source != right.Source)
            {
                return ((int)left.Source).CompareTo((int)right.Source);
            }

            return string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeImportName(string frontMatterName, string folderName)
        {
            string value = string.IsNullOrWhiteSpace(frontMatterName) ? folderName : frontMatterName;
            value = (value ?? string.Empty).Trim().ToLowerInvariant();
            var builder = new StringBuilder(value.Length);
            foreach (char character in value)
            {
                if ((character >= 'a' && character <= 'z') || (character >= '0' && character <= '9'))
                {
                    builder.Append(character);
                }
                else if (character == '-' || character == '_' || character == ' ' || character == '.')
                {
                    if (builder.Length > 0 && builder[builder.Length - 1] != '-')
                    {
                        builder.Append('-');
                    }
                }
            }

            return builder.ToString().Trim('-');
        }

        private static bool IsInside(string root, string candidate)
        {
            string prefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            return candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }

        private static void TryDeleteDirectory(string directory)
        {
            try
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, true);
                }
            }
            catch (Exception)
            {
            }
        }

        private static string ReadTextOrEmpty(string path)
        {
            try
            {
                Utf8TextInfo text = Utf8Text.Read(File.ReadAllBytes(path));
                return text.IsValid ? text.Text : string.Empty;
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        private static int CountLines(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return 0;
            }

            int lines = 1;
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] == '\n')
                {
                    lines++;
                }
            }

            return lines;
        }

        private sealed class FrontMatter
        {
            public string Name;
            public string Description;
        }

        private static FrontMatter ParseFrontMatter(string text)
        {
            var result = new FrontMatter();
            if (string.IsNullOrEmpty(text))
            {
                return result;
            }

            string normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
            string[] lines = normalized.Split('\n');
            if (lines.Length == 0 || lines[0].Trim() != "---")
            {
                return result;
            }

            for (int i = 1; i < lines.Length; i++)
            {
                string line = lines[i];
                if (line.Trim() == "---")
                {
                    break;
                }

                int colon = line.IndexOf(':');
                if (colon <= 0)
                {
                    continue;
                }

                string key = line.Substring(0, colon).Trim();
                string value = line.Substring(colon + 1).Trim().Trim('"', '\'');
                if (string.Equals(key, "name", StringComparison.OrdinalIgnoreCase))
                {
                    result.Name = value;
                }
                else if (string.Equals(key, "description", StringComparison.OrdinalIgnoreCase))
                {
                    result.Description = value;
                }
            }

            return result;
        }

        private static string ExtractDescription(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            string normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
            string[] lines = normalized.Split('\n');
            bool inFrontMatter = false;
            bool frontMatterDone = false;
            for (int i = 0; i < lines.Length && i < 80; i++)
            {
                string line = lines[i].Trim();
                if (i == 0 && line == "---")
                {
                    inFrontMatter = true;
                    continue;
                }

                if (inFrontMatter)
                {
                    if (line == "---")
                    {
                        inFrontMatter = false;
                        frontMatterDone = true;
                    }

                    continue;
                }

                if (!frontMatterDone && i > 0 && line == "---")
                {
                    frontMatterDone = true;
                    continue;
                }

                if (line.Length == 0 || line == "---" || line.StartsWith("```", StringComparison.Ordinal))
                {
                    continue;
                }

                string cleaned = line.TrimStart('#', '-', '*', '>', ' ').Trim();
                if (cleaned.Length == 0)
                {
                    continue;
                }

                return cleaned.Length <= 200 ? cleaned : cleaned.Substring(0, 200) + "…";
            }

            return string.Empty;
        }
    }
}
