using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace MiaoWu.Core.Text
{
    /// <summary>生成统一 diff 的选项。</summary>
    public sealed class UnifiedDiffOptions
    {
        /// <summary>每个 hunk 前后保留的上下文行数。</summary>
        public int ContextLines { get; set; } = 3;

        /// <summary>单侧超过该行数就不做逐行 diff，直接返回“文件过大”。</summary>
        public int MaxInputLines { get; set; } = 50000;

        /// <summary>编辑距离超过该值退化为“整段替换”，避免最坏情况 O(ND) 爆炸。</summary>
        public int MaxEditDistance { get; set; } = 1000;

        /// <summary>diff 文本的字符上限。</summary>
        public int MaxOutputChars { get; set; } = 200000;
    }

    /// <summary>一次 diff 的结果：文本、增删计数与是否被截断。</summary>
    public sealed class UnifiedDiffResult
    {
        public UnifiedDiffResult(string text, int addedCount, int removedCount, bool truncated)
        {
            Text = text ?? string.Empty;
            AddedCount = addedCount < 0 ? 0 : addedCount;
            RemovedCount = removedCount < 0 ? 0 : removedCount;
            Truncated = truncated;
        }

        public string Text { get; }

        public int AddedCount { get; }

        public int RemovedCount { get; }

        public bool Truncated { get; }

        public bool IsEmpty
        {
            get { return Text.Length == 0; }
        }
    }

    /// <summary>
    /// 行级统一 diff 生成器。纯托管、无依赖；输入为文本，输出可被
    /// <see cref="UnifiedDiffParser"/> 原样解析。
    /// </summary>
    /// <remarks>
    /// <para>先裁掉公共前缀/后缀，再用 Myers 最短编辑距离算法求中间段；
    /// 编辑距离超过 <see cref="UnifiedDiffOptions.MaxEditDistance"/> 时退化为整段替换。</para>
    /// <para>输出使用 LF 换行，路径标签默认 <c>a/{path}</c> 与 <c>b/{path}</c>。</para>
    /// </remarks>
    public static class UnifiedDiffBuilder
    {
        private enum EditKind
        {
            Equal = 0,
            Delete = 1,
            Insert = 2
        }

        private struct Edit
        {
            public EditKind Kind;
            public string Line;
        }

        public static UnifiedDiffResult Build(string oldText, string newText, string path)
        {
            return Build(oldText, newText, path, new UnifiedDiffOptions());
        }

        public static UnifiedDiffResult Build(
            string oldText,
            string newText,
            string path,
            UnifiedDiffOptions options)
        {
            string normalizedPath = string.IsNullOrWhiteSpace(path) ? "file" : path.Replace('\\', '/');
            return Build(
                oldText,
                newText,
                "a/" + normalizedPath,
                "b/" + normalizedPath,
                options);
        }

        public static UnifiedDiffResult Build(
            string oldText,
            string newText,
            string oldLabel,
            string newLabel,
            UnifiedDiffOptions options)
        {
            options = options ?? new UnifiedDiffOptions();

            List<string> oldLines = SplitLines(oldText);
            List<string> newLines = SplitLines(newText);

            if (string.Equals(oldText ?? string.Empty, newText ?? string.Empty, StringComparison.Ordinal))
            {
                return new UnifiedDiffResult(string.Empty, 0, 0, false);
            }

            if (oldLines.Count > options.MaxInputLines || newLines.Count > options.MaxInputLines)
            {
                string message = "--- " + (oldLabel ?? "a") + "\n"
                    + "+++ " + (newLabel ?? "b") + "\n"
                    + "文件过大，未生成逐行 diff（旧 " + oldLines.Count.ToString(CultureInfo.InvariantCulture)
                    + " 行 / 新 " + newLines.Count.ToString(CultureInfo.InvariantCulture) + " 行）。\n";
                return new UnifiedDiffResult(message, 0, 0, true);
            }

            int prefix = 0;
            while (prefix < oldLines.Count
                   && prefix < newLines.Count
                   && string.Equals(oldLines[prefix], newLines[prefix], StringComparison.Ordinal))
            {
                prefix++;
            }

            int suffix = 0;
            while (suffix < oldLines.Count - prefix
                   && suffix < newLines.Count - prefix
                   && string.Equals(
                       oldLines[oldLines.Count - 1 - suffix],
                       newLines[newLines.Count - 1 - suffix],
                       StringComparison.Ordinal))
            {
                suffix++;
            }

            int oldMiddleCount = oldLines.Count - prefix - suffix;
            int newMiddleCount = newLines.Count - prefix - suffix;

            List<Edit> middle = BuildMiddleEdits(
                oldLines,
                newLines,
                prefix,
                oldMiddleCount,
                newMiddleCount,
                options);

            List<Edit> script = new List<Edit>(oldMiddleCount + newMiddleCount + 2);
            for (int i = 0; i < prefix; i++)
            {
                script.Add(new Edit { Kind = EditKind.Equal, Line = oldLines[i] });
            }

            script.AddRange(middle);

            for (int i = suffix; i > 0; i--)
            {
                script.Add(new Edit
                {
                    Kind = EditKind.Equal,
                    Line = oldLines[oldLines.Count - i]
                });
            }

            return Render(script, oldLines.Count, newLines.Count, oldLabel, newLabel, options);
        }

        private static List<Edit> BuildMiddleEdits(
            List<string> oldLines,
            List<string> newLines,
            int offset,
            int oldCount,
            int newCount,
            UnifiedDiffOptions options)
        {
            var edits = new List<Edit>();
            if (oldCount == 0 && newCount == 0)
            {
                return edits;
            }

            if (oldCount == 0)
            {
                for (int i = 0; i < newCount; i++)
                {
                    edits.Add(new Edit { Kind = EditKind.Insert, Line = newLines[offset + i] });
                }

                return edits;
            }

            if (newCount == 0)
            {
                for (int i = 0; i < oldCount; i++)
                {
                    edits.Add(new Edit { Kind = EditKind.Delete, Line = oldLines[offset + i] });
                }

                return edits;
            }

            var a = new string[oldCount];
            var b = new string[newCount];
            for (int i = 0; i < oldCount; i++)
            {
                a[i] = oldLines[offset + i];
            }

            for (int i = 0; i < newCount; i++)
            {
                b[i] = newLines[offset + i];
            }

            List<Edit> myers = MyersDiff(a, b, options.MaxEditDistance);
            if (myers != null)
            {
                edits.AddRange(myers);
                return edits;
            }

            // 退化为整段替换：语义仍然是正确的 unified diff，只是不最小。
            for (int i = 0; i < oldCount; i++)
            {
                edits.Add(new Edit { Kind = EditKind.Delete, Line = a[i] });
            }

            for (int i = 0; i < newCount; i++)
            {
                edits.Add(new Edit { Kind = EditKind.Insert, Line = b[i] });
            }

            return edits;
        }

        /// <summary>
        /// Myers 最短编辑距离。超过 maxEditDistance 时返回 null，由调用方退化处理。
        /// </summary>
        private static List<Edit> MyersDiff(string[] a, string[] b, int maxEditDistance)
        {
            int n = a.Length;
            int m = b.Length;
            int max = n + m;
            int limit = Math.Min(maxEditDistance, max);
            int offset = limit + 1;
            var v = new int[2 * limit + 3];
            var trace = new List<int[]>(limit + 1);

            for (int d = 0; d <= limit; d++)
            {
                trace.Add((int[])v.Clone());
                for (int k = -d; k <= d; k += 2)
                {
                    int x;
                    if (k == -d || (k != d && v[offset + k - 1] < v[offset + k + 1]))
                    {
                        x = v[offset + k + 1];
                    }
                    else
                    {
                        x = v[offset + k - 1] + 1;
                    }

                    int y = x - k;
                    while (x < n && y < m && string.Equals(a[x], b[y], StringComparison.Ordinal))
                    {
                        x++;
                        y++;
                    }

                    v[offset + k] = x;
                    if (x >= n && y >= m)
                    {
                        return Backtrack(trace, a, b, offset);
                    }
                }
            }

            return null;
        }

        private static List<Edit> Backtrack(
            List<int[]> trace,
            string[] a,
            string[] b,
            int offset)
        {
            int x = a.Length;
            int y = b.Length;
            var reversed = new List<Edit>(a.Length + b.Length);

            for (int d = trace.Count - 1; d >= 0; d--)
            {
                int[] v = trace[d];
                int k = x - y;

                if (d == 0)
                {
                    while (x > 0 && y > 0)
                    {
                        reversed.Add(new Edit { Kind = EditKind.Equal, Line = a[x - 1] });
                        x--;
                        y--;
                    }

                    break;
                }

                int prevK;
                if (k == -d || (k != d && v[offset + k - 1] < v[offset + k + 1]))
                {
                    prevK = k + 1;
                }
                else
                {
                    prevK = k - 1;
                }

                int prevX = v[offset + prevK];
                int prevY = prevX - prevK;

                while (x > prevX && y > prevY)
                {
                    reversed.Add(new Edit { Kind = EditKind.Equal, Line = a[x - 1] });
                    x--;
                    y--;
                }

                if (x == prevX)
                {
                    reversed.Add(new Edit { Kind = EditKind.Insert, Line = b[y - 1] });
                    y--;
                }
                else
                {
                    reversed.Add(new Edit { Kind = EditKind.Delete, Line = a[x - 1] });
                    x--;
                }
            }

            reversed.Reverse();
            return reversed;
        }

        private static UnifiedDiffResult Render(
            List<Edit> script,
            int oldTotal,
            int newTotal,
            string oldLabel,
            string newLabel,
            UnifiedDiffOptions options)
        {
            int added = 0;
            int removed = 0;
            var changedIndexes = new List<int>();
            for (int i = 0; i < script.Count; i++)
            {
                if (script[i].Kind == EditKind.Insert)
                {
                    added++;
                    changedIndexes.Add(i);
                }
                else if (script[i].Kind == EditKind.Delete)
                {
                    removed++;
                    changedIndexes.Add(i);
                }
            }

            if (changedIndexes.Count == 0)
            {
                return new UnifiedDiffResult(string.Empty, 0, 0, false);
            }

            var builder = new StringBuilder(Math.Min(options.MaxOutputChars, 4096));
            builder.Append("--- ").Append(oldLabel ?? "a").Append('\n');
            builder.Append("+++ ").Append(newLabel ?? "b").Append('\n');

            int context = options.ContextLines < 0 ? 0 : options.ContextLines;
            int index = 0;
            int changePointer = 0;
            bool truncated = false;

            // 为每一行计算旧/新行号，供 hunk 头与行号推进使用。
            while (index < script.Count)
            {
                while (changePointer < changedIndexes.Count && changedIndexes[changePointer] < index)
                {
                    changePointer++;
                }

                if (changePointer >= changedIndexes.Count)
                {
                    break;
                }

                int changeIndex = changedIndexes[changePointer];
                int hunkStart = Math.Max(0, changeIndex - context);

                int lastChange = changeIndex;
                int lookahead = changePointer + 1;
                while (lookahead < changedIndexes.Count
                       && changedIndexes[lookahead] - lastChange <= context * 2 + 1)
                {
                    lastChange = changedIndexes[lookahead];
                    lookahead++;
                }

                changePointer = lookahead;
                int hunkEnd = Math.Min(script.Count, lastChange + context + 1);

                int oldStart = 1;
                int newStart = 1;
                for (int i = 0; i < hunkStart; i++)
                {
                    if (script[i].Kind != EditKind.Insert)
                    {
                        oldStart++;
                    }

                    if (script[i].Kind != EditKind.Delete)
                    {
                        newStart++;
                    }
                }

                int oldCount = 0;
                int newCount = 0;
                for (int i = hunkStart; i < hunkEnd; i++)
                {
                    if (script[i].Kind != EditKind.Insert)
                    {
                        oldCount++;
                    }

                    if (script[i].Kind != EditKind.Delete)
                    {
                        newCount++;
                    }
                }

                builder.Append("@@ -")
                    .Append(oldCount == 0 ? oldStart - 1 : oldStart)
                    .Append(',')
                    .Append(oldCount)
                    .Append(" +")
                    .Append(newCount == 0 ? newStart - 1 : newStart)
                    .Append(',')
                    .Append(newCount)
                    .Append(" @@\n");

                for (int i = hunkStart; i < hunkEnd; i++)
                {
                    Edit edit = script[i];
                    switch (edit.Kind)
                    {
                        case EditKind.Equal:
                            builder.Append(' ').Append(edit.Line).Append('\n');
                            break;
                        case EditKind.Delete:
                            builder.Append('-').Append(edit.Line).Append('\n');
                            break;
                        default:
                            builder.Append('+').Append(edit.Line).Append('\n');
                            break;
                    }

                    if (builder.Length > options.MaxOutputChars)
                    {
                        builder.Append("…（diff 已截断）\n");
                        truncated = true;
                        break;
                    }
                }

                if (truncated)
                {
                    break;
                }

                index = hunkEnd;
            }

            return new UnifiedDiffResult(builder.ToString(), added, removed, truncated);
        }

        /// <summary>按 CRLF / LF / CR 拆行；末尾换行不产生额外的空行。</summary>
        internal static List<string> SplitLines(string text)
        {
            var lines = new List<string>();
            if (string.IsNullOrEmpty(text))
            {
                return lines;
            }

            int start = 0;
            int index = 0;
            while (index < text.Length)
            {
                char current = text[index];
                if (current == '\r' || current == '\n')
                {
                    lines.Add(text.Substring(start, index - start));
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
                lines.Add(text.Substring(start));
            }

            return lines;
        }
    }
}
