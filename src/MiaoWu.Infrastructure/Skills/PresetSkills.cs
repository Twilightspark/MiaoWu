using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using MiaoWu.Core.Skills;
using MiaoWu.Infrastructure.Json;

namespace MiaoWu.Infrastructure.Skills
{
    /// <summary>预设技能里的一个文件：释放到技能文件夹的相对路径 + 程序集资源名。</summary>
    public sealed class PresetSkillFile
    {
        public PresetSkillFile(string relativePath, string resourceName)
        {
            RelativePath = relativePath ?? string.Empty;
            ResourceName = resourceName ?? string.Empty;
        }

        /// <summary>相对技能文件夹的路径，例如 <c>bin\miaowu_office.exe</c>。</summary>
        public string RelativePath { get; }

        /// <summary>在 <c>MiaoWu.Infrastructure.dll</c> 里的资源名。</summary>
        public string ResourceName { get; }
    }

    /// <summary>一份随应用发布的预设技能。</summary>
    public sealed class PresetSkillPackage
    {
        public PresetSkillPackage(string id, string version, IReadOnlyList<PresetSkillFile> files)
        {
            Id = id ?? string.Empty;
            Version = version ?? string.Empty;
            Files = files ?? new PresetSkillFile[0];
        }

        /// <summary>技能名，同时是用户级技能目录下的文件夹名。</summary>
        public string Id { get; }

        /// <summary>内容版本，只用于展示与诊断；是否重新释放按内容指纹判断。</summary>
        public string Version { get; }

        public IReadOnlyList<PresetSkillFile> Files { get; }
    }

    /// <summary>一次预设技能释放的处理结果。</summary>
    public enum PresetSkillAction
    {
        /// <summary>原本不存在，已释放。</summary>
        Installed = 0,

        /// <summary>已存在且是应用释放的旧版本，已按新内容覆盖。</summary>
        Updated = 1,

        /// <summary>已有同名用户技能且正文与预设一致，只补缺失文件并认领为预设。</summary>
        Adopted = 2,

        /// <summary>已是最新内容，没有写盘。</summary>
        Skipped = 3,

        /// <summary>已有同名用户技能但内容不同，保留用户版本。</summary>
        Conflict = 4,

        /// <summary>释放失败（权限、磁盘、资源缺失），只影响这一个技能。</summary>
        Failed = 5
    }

    /// <summary>一条预设技能的释放结果。</summary>
    public sealed class PresetSkillEntry
    {
        public PresetSkillEntry(
            string id,
            string version,
            PresetSkillAction action,
            string directory,
            string message)
        {
            Id = id ?? string.Empty;
            Version = version ?? string.Empty;
            Action = action;
            Directory = directory;
            Message = message ?? string.Empty;
        }

        public string Id { get; }

        public string Version { get; }

        public PresetSkillAction Action { get; }

        public string Directory { get; }

        public string Message { get; }

        /// <summary>一行中文描述，用于启动日志。</summary>
        public string Describe()
        {
            string action;
            switch (Action)
            {
                case PresetSkillAction.Installed:
                    action = "已释放";
                    break;
                case PresetSkillAction.Updated:
                    action = "已更新";
                    break;
                case PresetSkillAction.Adopted:
                    action = "已认领";
                    break;
                case PresetSkillAction.Skipped:
                    action = "已是最新";
                    break;
                case PresetSkillAction.Conflict:
                    action = "跳过（同名技能内容不同）";
                    break;
                default:
                    action = "失败";
                    break;
            }

            string text = Id + " " + action;
            if (!string.IsNullOrEmpty(Version))
            {
                text += "（" + Version + "）";
            }

            if (!string.IsNullOrEmpty(Message))
            {
                text += "：" + Message;
            }

            return text;
        }
    }

    /// <summary>一次释放全部预设技能的结果。</summary>
    public sealed class PresetSkillInstallReport
    {
        public PresetSkillInstallReport(IReadOnlyList<PresetSkillEntry> entries)
        {
            Entries = entries ?? new PresetSkillEntry[0];
        }

        public IReadOnlyList<PresetSkillEntry> Entries { get; }

        /// <summary>是否有条目真的写了盘（新建 / 更新 / 认领）。</summary>
        public bool Changed
        {
            get
            {
                foreach (PresetSkillEntry entry in Entries)
                {
                    if (entry.Action == PresetSkillAction.Installed
                        || entry.Action == PresetSkillAction.Updated
                        || entry.Action == PresetSkillAction.Adopted)
                    {
                        return true;
                    }
                }

                return false;
            }
        }

        /// <summary>是否有条目失败；失败只写日志，不阻塞启动。</summary>
        public bool HasFailures
        {
            get
            {
                foreach (PresetSkillEntry entry in Entries)
                {
                    if (entry.Action == PresetSkillAction.Failed)
                    {
                        return true;
                    }
                }

                return false;
            }
        }

        /// <summary>一行中文描述，异常与“无变化”都不刷屏。</summary>
        public string Describe()
        {
            if (Entries.Count == 0)
            {
                return string.Empty;
            }

            var parts = new List<string>(Entries.Count);
            foreach (PresetSkillEntry entry in Entries)
            {
                parts.Add(entry.Describe());
            }

            return string.Join("；", parts);
        }
    }

    /// <summary>
    /// 随应用发布的预设技能清单。内容嵌在 <c>MiaoWu.Infrastructure.dll</c> 里，
    /// 启动时由 <see cref="PresetSkillInstaller"/> 释放到用户级技能目录，
    /// 能力市场里默认就能看到，不需要用户导入技能包。
    /// </summary>
    public static class PresetSkills
    {
        /// <summary>预设技能包；新增预设在这里加一条，并在 csproj 里登记资源。</summary>
        public static IReadOnlyList<PresetSkillPackage> All { get; } = new[]
        {
            new PresetSkillPackage("office-wps", "1.0.0", new[]
            {
                new PresetSkillFile("SKILL.md", "MiaoWu.Presets.office-wps.SKILL.md"),
                new PresetSkillFile(@"bin\miaowu_office.exe", "MiaoWu.Presets.office-wps.bin.miaowu_office.exe"),
                new PresetSkillFile(@"examples\word-paragraph.json", "MiaoWu.Presets.office-wps.examples.word-paragraph.json"),
                new PresetSkillFile(@"examples\word-table.json", "MiaoWu.Presets.office-wps.examples.word-table.json"),
                new PresetSkillFile(@"examples\word-find-replace.json", "MiaoWu.Presets.office-wps.examples.word-find-replace.json")
            })
        };
    }

    /// <summary>
    /// 把预设技能释放到用户级技能目录。规则：
    /// 已由应用释放过且内容未变 → 不写盘；内容变了 → 覆盖预设文件；用户删掉了 → 下次启动补回；
    /// 已有同名用户技能且正文不同 → 不动它（用户自己那份优先）。
    /// 任何异常都收敛成 <see cref="PresetSkillAction.Failed"/>，不影响应用启动。
    /// </summary>
    public sealed class PresetSkillInstaller
    {
        /// <summary>与技能导入一致的上限，防止将来塞进过大的包。</summary>
        private const int MaxFiles = 200;
        private const long MaxBytes = 5 * 1024 * 1024;

        private const string IdKey = "id";
        private const string VersionKey = "version";
        private const string HashKey = "hash";
        private const string InstalledAtKey = "installedAt";
        private const string ManagedKey = "managed";

        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);

        private readonly string _userSkillsDirectory;
        private readonly Assembly _assembly;

        public PresetSkillInstaller(string userSkillsDirectory)
            : this(userSkillsDirectory, null)
        {
        }

        /// <summary><paramref name="resourceAssembly"/> 仅供测试指定程序集；生产路径传 null。</summary>
        public PresetSkillInstaller(string userSkillsDirectory, Assembly resourceAssembly)
        {
            _userSkillsDirectory = string.IsNullOrWhiteSpace(userSkillsDirectory)
                ? string.Empty
                : Path.GetFullPath(userSkillsDirectory);
            _assembly = resourceAssembly ?? typeof(PresetSkillInstaller).Assembly;
        }

        public string UserSkillsDirectory
        {
            get { return _userSkillsDirectory; }
        }

        /// <summary>释放全部预设技能；调用方可以忽略返回值，失败详情在条目里。</summary>
        public PresetSkillInstallReport Install()
        {
            var entries = new List<PresetSkillEntry>();
            foreach (PresetSkillPackage package in PresetSkills.All)
            {
                try
                {
                    entries.Add(InstallOne(package));
                }
                catch (Exception exception)
                {
                    entries.Add(new PresetSkillEntry(
                        package.Id,
                        package.Version,
                        PresetSkillAction.Failed,
                        null,
                        exception.Message));
                }
            }

            return new PresetSkillInstallReport(entries);
        }

        /// <summary>释放一份预设技能。</summary>
        public PresetSkillEntry InstallOne(PresetSkillPackage package)
        {
            if (package == null)
            {
                throw new ArgumentNullException(nameof(package));
            }

            if (string.IsNullOrEmpty(_userSkillsDirectory))
            {
                return new PresetSkillEntry(
                    package.Id, package.Version, PresetSkillAction.Failed, null, "没有用户级技能目录");
            }

            string target = Path.GetFullPath(Path.Combine(_userSkillsDirectory, package.Id));
            if (!IsInside(_userSkillsDirectory, target))
            {
                return new PresetSkillEntry(
                    package.Id, package.Version, PresetSkillAction.Failed, null, "技能目录越界");
            }

            var payload = new List<KeyValuePair<string, byte[]>>(package.Files.Count);
            long totalBytes = 0;
            byte[] body = null;
            foreach (PresetSkillFile file in package.Files)
            {
                byte[] bytes = ReadResource(file);
                if (bytes == null)
                {
                    return new PresetSkillEntry(
                        package.Id,
                        package.Version,
                        PresetSkillAction.Failed,
                        target,
                        "程序集里缺少 " + file.ResourceName);
                }

                if (string.Equals(file.RelativePath, SkillCatalogService.SkillFileName, StringComparison.OrdinalIgnoreCase))
                {
                    body = bytes;
                }

                payload.Add(new KeyValuePair<string, byte[]>(file.RelativePath, bytes));
                totalBytes += bytes.LongLength;
            }

            if (payload.Count > MaxFiles || totalBytes > MaxBytes)
            {
                return new PresetSkillEntry(
                    package.Id, package.Version, PresetSkillAction.Failed, target, "预设内容超过 200 个文件 / 5 MB");
            }

            string hash = ComputeHash(payload);
            string markerPath = Path.Combine(target, SkillDescriptor.PresetMarkerFileName);

            if (Directory.Exists(target))
            {
                string previous = ReadMarkerHash(markerPath);
                if (previous != null)
                {
                    if (string.Equals(previous, hash, StringComparison.OrdinalIgnoreCase))
                    {
                        return new PresetSkillEntry(
                            package.Id, package.Version, PresetSkillAction.Skipped, target, null);
                    }

                    // 应用带的新内容：整体覆盖预设文件（用户对预设文件的改动会丢），并更新标记。
                    WriteFiles(target, payload, false);
                    WriteMarker(markerPath, package, hash);
                    return new PresetSkillEntry(
                        package.Id, package.Version, PresetSkillAction.Updated, target, null);
                }

                // 没有标记：同名的用户技能。只有正文与预设完全一致（此前用 zip 导入过）才接管。
                string bodyPath = Path.Combine(target, SkillCatalogService.SkillFileName);
                if (body == null || !File.Exists(bodyPath) || !BytesEqual(File.ReadAllBytes(bodyPath), body))
                {
                    return new PresetSkillEntry(
                        package.Id,
                        package.Version,
                        PresetSkillAction.Conflict,
                        target,
                        "保留用户自己的同名技能");
                }

                WriteFiles(target, payload, true);
                WriteMarker(markerPath, package, hash);
                return new PresetSkillEntry(
                    package.Id, package.Version, PresetSkillAction.Adopted, target, null);
            }

            WriteFiles(target, payload, false);
            WriteMarker(markerPath, package, hash);
            return new PresetSkillEntry(
                package.Id, package.Version, PresetSkillAction.Installed, target, null);
        }

        private byte[] ReadResource(PresetSkillFile file)
        {
            if (file == null || string.IsNullOrEmpty(file.ResourceName))
            {
                return null;
            }

            using (Stream stream = _assembly.GetManifestResourceStream(file.ResourceName))
            {
                if (stream == null)
                {
                    return null;
                }

                using (var buffer = new MemoryStream())
                {
                    stream.CopyTo(buffer);
                    return buffer.ToArray();
                }
            }
        }

        /// <summary><paramref name="onlyMissing"/> 为 true 时只补缺失文件，不覆盖用户已有内容。</summary>
        private static void WriteFiles(
            string target,
            IReadOnlyList<KeyValuePair<string, byte[]>> payload,
            bool onlyMissing)
        {
            Directory.CreateDirectory(target);
            foreach (KeyValuePair<string, byte[]> file in payload)
            {
                string destination = Path.GetFullPath(Path.Combine(target, file.Key));
                if (!IsInside(target, destination))
                {
                    continue;
                }

                if (onlyMissing && File.Exists(destination))
                {
                    continue;
                }

                string parent = Path.GetDirectoryName(destination);
                if (!string.IsNullOrEmpty(parent))
                {
                    Directory.CreateDirectory(parent);
                }

                File.WriteAllBytes(destination, file.Value);
            }
        }

        private static void WriteMarker(string markerPath, PresetSkillPackage package, string hash)
        {
            JsonValue root = JsonValue.NewObject();
            root[IdKey] = JsonValue.From(package.Id);
            root[VersionKey] = JsonValue.From(package.Version);
            root[HashKey] = JsonValue.From(hash);
            root[ManagedKey] = JsonValue.From(true);
            root[InstalledAtKey] = JsonValue.From(
                DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture));

            File.WriteAllText(markerPath, JsonWriter.WriteIndented(root) + "\n", Utf8NoBom);
        }

        /// <summary>读标记里的内容指纹；标记缺失或损坏时返回 null（当作没有标记）。</summary>
        private static string ReadMarkerHash(string markerPath)
        {
            if (!File.Exists(markerPath))
            {
                return null;
            }

            try
            {
                JsonValue root = JsonParser.Parse(File.ReadAllText(markerPath, Utf8NoBom));
                JsonValue hash = root == null ? null : root.Get(HashKey);
                string value = hash == null ? null : hash.AsString(null);
                return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static string ComputeHash(IReadOnlyList<KeyValuePair<string, byte[]>> payload)
        {
            using (var sha = SHA256.Create())
            {
                using (var buffer = new MemoryStream())
                {
                    foreach (KeyValuePair<string, byte[]> file in payload)
                    {
                        byte[] name = Utf8NoBom.GetBytes(file.Key);
                        buffer.Write(name, 0, name.Length);
                        buffer.WriteByte(0);
                        byte[] length = Utf8NoBom.GetBytes(file.Value.LongLength.ToString(CultureInfo.InvariantCulture));
                        buffer.Write(length, 0, length.Length);
                        buffer.WriteByte(0);
                        buffer.Write(file.Value, 0, file.Value.Length);
                    }

                    buffer.Position = 0;
                    byte[] digest = sha.ComputeHash(buffer);
                    var text = new StringBuilder(digest.Length * 2);
                    foreach (byte value in digest)
                    {
                        text.Append(value.ToString("x2", CultureInfo.InvariantCulture));
                    }

                    return text.ToString();
                }
            }
        }

        private static bool BytesEqual(byte[] left, byte[] right)
        {
            if (left == null || right == null || left.Length != right.Length)
            {
                return false;
            }

            for (int i = 0; i < left.Length; i++)
            {
                if (left[i] != right[i])
                {
                    return false;
                }
            }

            return true;
        }

        private static bool IsInside(string root, string candidate)
        {
            string prefix = Path.GetFullPath(root)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            return candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }
    }
}
