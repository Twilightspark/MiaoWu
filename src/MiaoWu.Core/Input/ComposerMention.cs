using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace MiaoWu.Core.Input
{
    /// <summary>输入框里一个引用 chip 的形态。</summary>
    public enum ComposerMentionKind
    {
        File,
        Folder,
        Image
    }

    /// <summary>
    /// 输入框里的一个引用。界面上渲染成 chip，序列化成 <see cref="Text"/> 进入草稿；
    /// 图片同时会作为图片附件随消息发送。
    /// </summary>
    public sealed class ComposerMention
    {
        /// <summary>相对工作区根的路径，统一使用正斜杠。</summary>
        public string RelativePath { get; set; }

        /// <summary>磁盘上的绝对路径；图片附件读取时使用。</summary>
        public string FullPath { get; set; }

        /// <summary>chip 上显示的名字（目录带尾斜杠）。</summary>
        public string DisplayName { get; set; }

        /// <summary>父目录相对路径；工作区根下为空。</summary>
        public string ParentPath { get; set; }

        public ComposerMentionKind Kind { get; set; }

        /// <summary>草稿里的序列化文本，例如 <c>@src/main.cs</c> 或 <c>@"a b.txt"</c>。</summary>
        public string Text
        {
            get { return MentionFormatter.Format(RelativePath, Kind == ComposerMentionKind.Folder, false); }
        }

        public bool IsImage
        {
            get { return Kind == ComposerMentionKind.Image; }
        }

        public ComposerMention Clone()
        {
            return new ComposerMention
            {
                RelativePath = RelativePath,
                FullPath = FullPath,
                DisplayName = DisplayName,
                ParentPath = ParentPath,
                Kind = Kind
            };
        }
    }

    /// <summary>
    /// `@` 引用的文本语法。对齐 DSH 的 <c>dsh-file-reference/grammar.ts</c>：
    /// 无空白写 <c>@path</c>；含空白写 <c>@"path"</c>；目录以斜杠结尾；
    /// 路径里有控制字符或内嵌引号时无法安全表达，返回 null（候选行直接不出现）。
    /// </summary>
    public static class MentionFormatter
    {
        private static readonly HashSet<string> ImageExtensions = new HashSet<string>(
            new[] { ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp" },
            StringComparer.OrdinalIgnoreCase);

        /// <summary>把绝对 / 相对路径规范成相对工作区、正斜杠、无首尾斜杠的形式。</summary>
        public static string NormalizeRelativePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return string.Empty;
            }

            string normalized = path.Replace('\\', '/').Trim();
            while (normalized.StartsWith("./", StringComparison.Ordinal))
            {
                normalized = normalized.Substring(2);
            }

            return normalized.Trim('/');
        }

        /// <summary>
        /// 生成 mention 文本；无法安全表达时返回 null。
        /// </summary>
        /// <param name="relativePath">相对路径（可带反斜杠）。</param>
        /// <param name="isDirectory">目录末尾补斜杠。</param>
        /// <param name="keepQuotedOpen">下钻中间态：引号保持打开，便于下一次击键继续命中。</param>
        public static string Format(string relativePath, bool isDirectory, bool keepQuotedOpen)
        {
            string path = NormalizeRelativePath(relativePath);
            if (path.Length == 0)
            {
                return null;
            }

            foreach (char c in path)
            {
                if (char.IsControl(c) || c == '"')
                {
                    return null;
                }
            }

            if (isDirectory && !path.EndsWith("/", StringComparison.Ordinal))
            {
                path += "/";
            }

            bool needsQuotes = path.IndexOf(' ') >= 0 || path.IndexOf('\t') >= 0;
            if (!needsQuotes)
            {
                return "@" + path;
            }

            return "@\"" + path + (keepQuotedOpen ? string.Empty : "\"");
        }

        /// <summary>按扩展名判断是否为可插入的图片。</summary>
        public static bool IsImagePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            string extension = Path.GetExtension(path);
            return !string.IsNullOrEmpty(extension) && ImageExtensions.Contains(extension);
        }

        /// <summary>根据扩展名给出 MIME；未知图片默认 image/png。</summary>
        public static string GuessImageMimeType(string path)
        {
            string extension = Path.GetExtension(path ?? string.Empty).ToLowerInvariant();
            switch (extension)
            {
                case ".jpg":
                case ".jpeg":
                    return "image/jpeg";
                case ".gif":
                    return "image/gif";
                case ".webp":
                    return "image/webp";
                case ".bmp":
                    return "image/bmp";
                default:
                    return "image/png";
            }
        }

        /// <summary>父目录相对路径；根目录下返回空串。</summary>
        public static string GetParentPath(string relativePath)
        {
            string path = NormalizeRelativePath(relativePath);
            int slash = path.LastIndexOf('/');
            return slash <= 0 ? string.Empty : path.Substring(0, slash);
        }

        /// <summary>文件名（目录带尾斜杠），用于 chip 与候选行主标题。</summary>
        public static string GetDisplayName(string relativePath, bool isDirectory)
        {
            string path = NormalizeRelativePath(relativePath);
            if (path.Length == 0)
            {
                return string.Empty;
            }

            int slash = path.LastIndexOf('/');
            string name = slash < 0 ? path : path.Substring(slash + 1);
            return isDirectory ? name + "/" : name;
        }
    }

    /// <summary>工作区文件索引里的一行；由 Infrastructure 扫描产生，Core 只做排序。</summary>
    public sealed class WorkspaceFileEntry
    {
        public WorkspaceFileEntry(string relativePath, bool isDirectory)
        {
            RelativePath = MentionFormatter.NormalizeRelativePath(relativePath);
            IsDirectory = isDirectory;
            Name = MentionFormatter.GetDisplayName(RelativePath, isDirectory);
            ParentPath = MentionFormatter.GetParentPath(RelativePath);
            IsImage = !isDirectory && MentionFormatter.IsImagePath(RelativePath);
        }

        public string RelativePath { get; private set; }

        public string Name { get; private set; }

        public string ParentPath { get; private set; }

        public bool IsDirectory { get; private set; }

        public bool IsImage { get; private set; }
    }

    /// <summary>
    /// `@` 候选的过滤与排序：纯函数，输入是全量文件表与查询词。
    /// 前缀优先、其次子串、再次子序列；同分时目录在前、按名称排序。
    /// </summary>
    public static class MentionSearch
    {
        /// <summary>候选行上限；菜单一次最多显示这么多行。</summary>
        public const int DefaultLimit = 40;

        public static IReadOnlyList<WorkspaceFileEntry> Rank(
            IEnumerable<WorkspaceFileEntry> entries,
            string query,
            int limit)
        {
            if (entries == null)
            {
                return new List<WorkspaceFileEntry>();
            }

            string raw = (query ?? string.Empty).Replace('\\', '/');
            bool childrenOnly = raw.EndsWith("/", StringComparison.Ordinal);
            string directoryPrefix = childrenOnly
                ? raw.Trim('/')
                : (raw.IndexOf('/') >= 0 ? raw.Substring(0, raw.LastIndexOf('/')).Trim('/') : null);
            string term = childrenOnly
                ? string.Empty
                : (raw.IndexOf('/') >= 0 ? raw.Substring(raw.LastIndexOf('/') + 1) : raw);
            string loweredTerm = term.ToLowerInvariant();

            var scored = new List<ScoredEntry>();
            foreach (WorkspaceFileEntry entry in entries)
            {
                if (entry == null)
                {
                    continue;
                }

                if (directoryPrefix != null)
                {
                    if (!string.Equals(entry.ParentPath, directoryPrefix, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                }

                if (loweredTerm.Length > 0)
                {
                    int score = Score(entry, loweredTerm);
                    if (score < 0)
                    {
                        continue;
                    }

                    scored.Add(new ScoredEntry(entry, score));
                }
                else
                {
                    scored.Add(new ScoredEntry(entry, 0));
                }
            }

            scored.Sort(CompareEntries);
            int take = limit <= 0 ? DefaultLimit : limit;
            var result = new List<WorkspaceFileEntry>(Math.Min(scored.Count, take));
            for (int i = 0; i < scored.Count && result.Count < take; i++)
            {
                result.Add(scored[i].Entry);
            }

            return result;
        }

        /// <summary>
        /// 计算匹配分数：越小越靠前，-1 表示不匹配。
        /// </summary>
        public static int Score(WorkspaceFileEntry entry, string loweredTerm)
        {
            if (entry == null || string.IsNullOrEmpty(loweredTerm))
            {
                return 0;
            }

            string name = (entry.Name ?? string.Empty).ToLowerInvariant();
            if (string.Equals(name, loweredTerm, StringComparison.Ordinal))
            {
                return 0;
            }

            // 主名命中（README.md 对 "readme"）优先于同前缀的其它文件。
            int dot = name.LastIndexOf('.');
            if (dot > 0 && string.Equals(name.Substring(0, dot), loweredTerm, StringComparison.Ordinal))
            {
                return 0;
            }

            if (name.StartsWith(loweredTerm, StringComparison.Ordinal))
            {
                return 1;
            }

            if (name.IndexOf(loweredTerm, StringComparison.Ordinal) >= 0)
            {
                return 2;
            }

            if (IsSubsequence(name, loweredTerm))
            {
                return 3;
            }

            string path = (entry.RelativePath ?? string.Empty).ToLowerInvariant();
            if (path.StartsWith(loweredTerm, StringComparison.Ordinal))
            {
                return 4;
            }

            if (path.IndexOf(loweredTerm, StringComparison.Ordinal) >= 0)
            {
                return 5;
            }

            if (IsSubsequence(path, loweredTerm))
            {
                return 6;
            }

            return -1;
        }

        /// <summary>按顺序包含查询字符（不要求连续）。</summary>
        public static bool IsSubsequence(string text, string loweredTerm)
        {
            if (string.IsNullOrEmpty(loweredTerm))
            {
                return true;
            }

            if (string.IsNullOrEmpty(text))
            {
                return false;
            }

            int position = 0;
            foreach (char c in text)
            {
                if (c == loweredTerm[position])
                {
                    position++;
                    if (position == loweredTerm.Length)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static int CompareEntries(ScoredEntry left, ScoredEntry right)
        {
            int byScore = left.Score.CompareTo(right.Score);
            if (byScore != 0)
            {
                return byScore;
            }

            if (left.Entry.IsDirectory != right.Entry.IsDirectory)
            {
                return left.Entry.IsDirectory ? -1 : 1;
            }

            int byName = string.Compare(left.Entry.Name, right.Entry.Name, StringComparison.OrdinalIgnoreCase);
            if (byName != 0)
            {
                return byName;
            }

            return string.Compare(left.Entry.RelativePath, right.Entry.RelativePath, StringComparison.OrdinalIgnoreCase);
        }

        private sealed class ScoredEntry
        {
            public ScoredEntry(WorkspaceFileEntry entry, int score)
            {
                Entry = entry;
                Score = score;
            }

            public WorkspaceFileEntry Entry { get; private set; }

            public int Score { get; private set; }
        }
    }
}
