using System;
using System.Collections.Generic;

namespace MiaoWu.Core.Text
{
    /// <summary>统一 diff 中一行的类别。</summary>
    public enum DiffLineKind
    {
        /// <summary>文件头（diff --git、index、---、+++ 等）。</summary>
        FileHeader,

        /// <summary>hunk 头（@@ -1,3 +1,4 @@）。</summary>
        HunkHeader,

        /// <summary>上下文行。</summary>
        Context,

        /// <summary>新增行（前缀 +）。</summary>
        Added,

        /// <summary>删除行（前缀 -）。</summary>
        Removed,

        /// <summary>"\ No newline at end of file" 标记。</summary>
        NoNewline
    }

    /// <summary>
    /// diff 中的一行。<see cref="Text"/> 不含 +/-/空格前缀，
    /// 但 FileHeader、HunkHeader 与 NoNewline 保留整行原文。
    /// </summary>
    public sealed class DiffLine
    {
        public DiffLine(DiffLineKind kind, string text, int? oldLineNumber, int? newLineNumber)
        {
            Kind = kind;
            Text = text ?? string.Empty;
            OldLineNumber = oldLineNumber;
            NewLineNumber = newLineNumber;
        }

        public DiffLineKind Kind { get; }

        /// <summary>行内容；上下文/增删行已去掉前缀。</summary>
        public string Text { get; }

        /// <summary>旧文件中的行号；新增行与文件头为 null。</summary>
        public int? OldLineNumber { get; }

        /// <summary>新文件中的行号；删除行与文件头为 null。</summary>
        public int? NewLineNumber { get; }

        /// <summary>是否是增删行。</summary>
        public bool IsChange
        {
            get { return Kind == DiffLineKind.Added || Kind == DiffLineKind.Removed; }
        }
    }

    /// <summary>解析后的一整份 diff，带增删计数。</summary>
    public sealed class DiffDocument
    {
        private static readonly DiffLine[] EmptyLines = new DiffLine[0];

        public DiffDocument(IReadOnlyList<DiffLine> lines, int addedCount, int removedCount)
        {
            Lines = lines ?? EmptyLines;
            AddedCount = addedCount < 0 ? 0 : addedCount;
            RemovedCount = removedCount < 0 ? 0 : removedCount;
        }

        public IReadOnlyList<DiffLine> Lines { get; }

        public int AddedCount { get; }

        public int RemovedCount { get; }

        /// <summary>没有任何可显示的行（空输入或纯文本输入）。</summary>
        public bool IsEmpty
        {
            get { return Lines.Count == 0; }
        }
    }

    /// <summary>
    /// 统一 diff（git diff）的轻量解析器，只服务于 P2 的静态展示：
    /// 1. 文件头整行保留，行号为 null；
    /// 2. hunk 头解析出旧/新起始行，解析失败也不抛异常，行号置 null 并从 1 开始计数；
    /// 3. hunk 内按前缀分发，行号连续递增，增删计数按实际行数统计。
    /// 纯文本、空串、畸形输入都只会得到空结果或部分结果，不会抛异常。
    /// </summary>
    public static class UnifiedDiffParser
    {
        private const string DiffHeaderPrefix = "diff --git";
        private static readonly string[] FileHeaderPrefixes =
        {
            "index ",
            "--- ",
            "+++ ",
            "new file mode",
            "deleted file mode",
            "similarity index"
        };

        /// <summary>解析统一 diff 文本；null 或空串返回空文档。</summary>
        public static DiffDocument Parse(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return new DiffDocument(null, 0, 0);
            }

            List<DiffLine> lines = new List<DiffLine>();
            bool inHunk = false;
            int oldLine = 1;
            int newLine = 1;
            int addedCount = 0;
            int removedCount = 0;

            foreach (string line in SplitLines(text))
            {
                // 新的 diff 段开始：无论当前是否在 hunk 内都要收尾，否则多文件 diff 会串行号。
                if (line.StartsWith(DiffHeaderPrefix, StringComparison.Ordinal))
                {
                    inHunk = false;
                    lines.Add(new DiffLine(DiffLineKind.FileHeader, line, null, null));
                    continue;
                }

                if (line.StartsWith("@@", StringComparison.Ordinal))
                {
                    int startOld;
                    int startNew;
                    int? headerOld = null;
                    int? headerNew = null;
                    if (TryParseHunkHeader(line, out startOld, out startNew))
                    {
                        headerOld = startOld;
                        headerNew = startNew;
                        oldLine = startOld;
                        newLine = startNew;
                    }
                    else
                    {
                        oldLine = 1;
                        newLine = 1;
                    }

                    lines.Add(new DiffLine(DiffLineKind.HunkHeader, line, headerOld, headerNew));
                    inHunk = true;
                    continue;
                }

                if (!inHunk)
                {
                    // hunk 外的空行与纯文本一律丢弃；只有文件头值得保留。
                    if (IsFileHeader(line))
                    {
                        lines.Add(new DiffLine(DiffLineKind.FileHeader, line, null, null));
                    }

                    continue;
                }

                if (line.Length == 0)
                {
                    // git 不会产生空上下文行（空行是单个空格），这里宽松地当成空上下文。
                    lines.Add(new DiffLine(DiffLineKind.Context, string.Empty, oldLine, newLine));
                    oldLine++;
                    newLine++;
                    continue;
                }

                switch (line[0])
                {
                    case '\\':
                        lines.Add(new DiffLine(DiffLineKind.NoNewline, line, null, null));
                        break;

                    case '+':
                        lines.Add(new DiffLine(DiffLineKind.Added, line.Substring(1), null, newLine));
                        newLine++;
                        addedCount++;
                        break;

                    case '-':
                        lines.Add(new DiffLine(DiffLineKind.Removed, line.Substring(1), oldLine, null));
                        oldLine++;
                        removedCount++;
                        break;

                    case ' ':
                        lines.Add(new DiffLine(DiffLineKind.Context, line.Substring(1), oldLine, newLine));
                        oldLine++;
                        newLine++;
                        break;

                    default:
                        // 畸形 hunk 内容不崩溃：整行当上下文，两个计数都推进。
                        lines.Add(new DiffLine(DiffLineKind.Context, line, oldLine, newLine));
                        oldLine++;
                        newLine++;
                        break;
                }
            }

            return new DiffDocument(lines, addedCount, removedCount);
        }

        private static bool IsFileHeader(string line)
        {
            for (int i = 0; i < FileHeaderPrefixes.Length; i++)
            {
                if (line.StartsWith(FileHeaderPrefixes[i], StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// 拆行，保留每一行的原文；末尾换行不会多出一个空行。
        /// 同时接受 CRLF、LF 与 CR，避免复制粘贴来的 diff 解析失败。
        /// </summary>
        private static List<string> SplitLines(string text)
        {
            List<string> result = new List<string>();
            int start = 0;
            int index = 0;
            while (index < text.Length)
            {
                char current = text[index];
                if (current == '\r' || current == '\n')
                {
                    result.Add(text.Substring(start, index - start));
                    if (current == '\r' && index + 1 < text.Length && text[index + 1] == '\n')
                    {
                        index++;
                    }

                    index++;
                    start = index;
                }
                else
                {
                    index++;
                }
            }

            if (start < text.Length)
            {
                result.Add(text.Substring(start));
            }

            return result;
        }

        /// <summary>解析 "@@ -a,b +c,d @@" 里的两个起始行；容忍缺省的行数。</summary>
        private static bool TryParseHunkHeader(string line, out int oldStart, out int newStart)
        {
            oldStart = 0;
            newStart = 0;

            int index = 0;
            while (index < line.Length && line[index] == '@')
            {
                index++;
            }

            while (index < line.Length && line[index] == ' ')
            {
                index++;
            }

            if (!TryReadNumber(line, ref index, '-', out oldStart))
            {
                return false;
            }

            if (index < line.Length && line[index] == ',')
            {
                index++;
                int ignoredOldCount;
                if (!TryReadDigits(line, ref index, out ignoredOldCount))
                {
                    return false;
                }
            }

            while (index < line.Length && line[index] == ' ')
            {
                index++;
            }

            if (!TryReadNumber(line, ref index, '+', out newStart))
            {
                return false;
            }

            if (index < line.Length && line[index] == ',')
            {
                index++;
                int ignoredNewCount;
                if (!TryReadDigits(line, ref index, out ignoredNewCount))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool TryReadNumber(string line, ref int index, char sign, out int value)
        {
            value = 0;
            if (index >= line.Length || line[index] != sign)
            {
                return false;
            }

            index++;
            return TryReadDigits(line, ref index, out value);
        }

        private static bool TryReadDigits(string line, ref int index, out int value)
        {
            value = 0;
            int start = index;
            while (index < line.Length && line[index] >= '0' && line[index] <= '9')
            {
                int digit = line[index] - '0';
                if (value > (int.MaxValue - digit) / 10)
                {
                    value = int.MaxValue;
                }
                else
                {
                    value = value * 10 + digit;
                }

                index++;
            }

            return index > start;
        }
    }
}
