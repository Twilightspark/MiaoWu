using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace MiaoWu.Core.Markdown
{
    /// <summary>
    /// 轻量 Markdown 解析器：只产出与 UI 无关的块级/行内模型，不执行 HTML、不依赖 WPF。
    /// 支持标题、段落、列表、引用、围栏代码、表格、分隔线，行内支持代码、加粗、斜体、链接、实体与转义。
    /// </summary>
    public static class MarkdownParser
    {
        private const char ByteOrderMark = '\uFEFF';
        private const int MaxListLevel = 3;
        private const int MaxHeadingLevel = 3;

        /// <summary>解析 Markdown 文本；text 为 null 时视为空文档。</summary>
        public static MarkdownDocument Parse(string text)
        {
            if (text == null)
            {
                return new MarkdownDocument(null);
            }

            if (text.Length > 0 && text[0] == ByteOrderMark)
            {
                text = text.Substring(1);
            }

            List<string> lines = SplitLines(text);
            List<MarkdownBlock> blocks = new List<MarkdownBlock>();
            int index = 0;
            while (index < lines.Count)
            {
                string line = lines[index];
                if (IsBlank(line))
                {
                    index++;
                    continue;
                }

                FenceInfo fence;
                if (TryReadFenceStart(line, out fence))
                {
                    index = ReadFenceBlock(lines, index, fence, blocks);
                    continue;
                }

                int headingLevel;
                string headingContent;
                if (TryReadHeading(line, out headingLevel, out headingContent))
                {
                    blocks.Add(new MarkdownHeading(
                        headingLevel,
                        MarkdownInlineParser.Parse(headingContent)));
                    index++;
                    continue;
                }

                MarkdownTable table;
                int tableEnd;
                if (TryReadTable(lines, index, out table, out tableEnd))
                {
                    blocks.Add(table);
                    index = tableEnd;
                    continue;
                }

                if (IsRule(line))
                {
                    blocks.Add(new MarkdownRule());
                    index++;
                    continue;
                }

                if (IsQuoteLine(line))
                {
                    index = ReadQuote(lines, index, blocks);
                    continue;
                }

                ListItemInfo item;
                if (TryReadListItem(line, out item))
                {
                    index = ReadList(lines, index, item, blocks);
                    continue;
                }

                index = ReadParagraph(lines, index, blocks);
            }

            return new MarkdownDocument(blocks);
        }

        private static List<string> SplitLines(string text)
        {
            List<string> lines = new List<string>();
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

                    start = index + 1;
                }

                index++;
            }

            lines.Add(text.Substring(start));
            return lines;
        }

        private static bool IsBlank(string line)
        {
            for (int index = 0; index < line.Length; index++)
            {
                if (!char.IsWhiteSpace(line[index]))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>缩进列数；制表符对齐到下一个 4 的倍数。</summary>
        private static int CountIndentColumns(string line, out int firstNonWhitespace)
        {
            int columns = 0;
            int index = 0;
            while (index < line.Length)
            {
                char current = line[index];
                if (current == ' ')
                {
                    columns++;
                    index++;
                    continue;
                }

                if (current == '\t')
                {
                    columns += 4 - (columns % 4);
                    index++;
                    continue;
                }

                break;
            }

            firstNonWhitespace = index;
            return columns;
        }

        private static int GetIndentColumns(string line)
        {
            int firstNonWhitespace;
            return CountIndentColumns(line, out firstNonWhitespace);
        }

        private static int CountRun(string text, int start, char marker)
        {
            int index = start;
            while (index < text.Length && text[index] == marker)
            {
                index++;
            }

            return index - start;
        }

        private static bool TryReadHeading(string line, out int level, out string content)
        {
            level = 0;
            content = string.Empty;

            int firstNonWhitespace;
            int indent = CountIndentColumns(line, out firstNonWhitespace);
            if (indent > 3)
            {
                return false;
            }

            int index = firstNonWhitespace;
            int hashes = 0;
            while (index < line.Length && line[index] == '#')
            {
                hashes++;
                index++;
            }

            if (hashes < 1 || hashes > 6)
            {
                return false;
            }

            // 规范要求 # 与标题文字之间必须有空格，`#foo` 是段落。
            if (index >= line.Length || (line[index] != ' ' && line[index] != '\t'))
            {
                return false;
            }

            while (index < line.Length && (line[index] == ' ' || line[index] == '\t'))
            {
                index++;
            }

            level = hashes > MaxHeadingLevel ? MaxHeadingLevel : hashes;
            content = line.Substring(index).Trim();
            return true;
        }

        private static bool IsHeadingLine(string line)
        {
            int level;
            string content;
            return TryReadHeading(line, out level, out content);
        }

        private static bool IsRule(string line)
        {
            string trimmed = line.Trim();
            if (trimmed.Length < 3)
            {
                return false;
            }

            char marker = '\0';
            int count = 0;
            for (int index = 0; index < trimmed.Length; index++)
            {
                char current = trimmed[index];
                if (char.IsWhiteSpace(current))
                {
                    continue;
                }

                if (marker == '\0')
                {
                    if (current != '-' && current != '*' && current != '_')
                    {
                        return false;
                    }

                    marker = current;
                }
                else if (current != marker)
                {
                    return false;
                }

                count++;
            }

            return count >= 3;
        }

        private static bool IsQuoteLine(string line)
        {
            int firstNonWhitespace;
            int indent = CountIndentColumns(line, out firstNonWhitespace);
            if (indent > 3)
            {
                return false;
            }

            return firstNonWhitespace < line.Length && line[firstNonWhitespace] == '>';
        }

        private static string StripQuotePrefix(string line)
        {
            int firstNonWhitespace;
            CountIndentColumns(line, out firstNonWhitespace);
            int contentStart = firstNonWhitespace + 1;
            if (contentStart < line.Length && line[contentStart] == ' ')
            {
                contentStart++;
            }

            return line.Substring(contentStart);
        }

        private static List<string> SplitCells(string line)
        {
            List<string> cells = new List<string>();
            string[] parts = line.Split('|');
            int from = 0;
            int to = parts.Length;
            if (to > 0 && parts[0].Trim().Length == 0)
            {
                from = 1;
            }

            if (to > from && parts[to - 1].Trim().Length == 0)
            {
                to--;
            }

            for (int index = from; index < to; index++)
            {
                cells.Add(parts[index].Trim());
            }

            return cells;
        }

        private static bool IsTableSeparatorCell(string cell)
        {
            string trimmed = cell.Trim();
            if (trimmed.Length == 0)
            {
                return false;
            }

            bool hasDash = false;
            for (int index = 0; index < trimmed.Length; index++)
            {
                char current = trimmed[index];
                if (current == '-')
                {
                    hasDash = true;
                    continue;
                }

                if (current == ':' || current == ' ')
                {
                    continue;
                }

                return false;
            }

            return hasDash;
        }

        private static bool IsTableStart(List<string> lines, int index)
        {
            if (index + 1 >= lines.Count)
            {
                return false;
            }

            string headerLine = lines[index];
            if (headerLine.IndexOf('|') < 0)
            {
                return false;
            }

            List<string> headers = SplitCells(headerLine);
            if (headers.Count == 0)
            {
                return false;
            }

            string separatorLine = lines[index + 1];
            if (separatorLine.IndexOf('|') < 0)
            {
                return false;
            }

            List<string> separatorCells = SplitCells(separatorLine);
            if (separatorCells.Count != headers.Count)
            {
                return false;
            }

            for (int cell = 0; cell < separatorCells.Count; cell++)
            {
                if (!IsTableSeparatorCell(separatorCells[cell]))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool TryReadTable(
            List<string> lines,
            int start,
            out MarkdownTable table,
            out int next)
        {
            table = null;
            next = start;
            if (!IsTableStart(lines, start))
            {
                return false;
            }

            List<string> headers = SplitCells(lines[start]);
            List<IReadOnlyList<string>> rows = new List<IReadOnlyList<string>>();
            int index = start + 2;
            while (index < lines.Count)
            {
                if (IsBlank(lines[index]) || lines[index].IndexOf('|') < 0)
                {
                    break;
                }

                List<string> cells = SplitCells(lines[index]);
                string[] row = new string[headers.Count];
                for (int column = 0; column < row.Length; column++)
                {
                    row[column] = column < cells.Count ? cells[column] : string.Empty;
                }

                rows.Add(row);
                index++;
            }

            table = new MarkdownTable(headers, rows);
            next = index;
            return true;
        }

        private static bool TryReadFenceStart(string line, out FenceInfo fence)
        {
            fence = null;

            int firstNonWhitespace;
            int indent = CountIndentColumns(line, out firstNonWhitespace);
            if (indent > 3 || firstNonWhitespace >= line.Length)
            {
                return false;
            }

            char marker = line[firstNonWhitespace];
            if (marker != '`' && marker != '~')
            {
                return false;
            }

            int length = CountRun(line, firstNonWhitespace, marker);
            if (length < 3)
            {
                return false;
            }

            string info = line.Substring(firstNonWhitespace + length).Trim();
            string language = null;
            if (info.Length > 0)
            {
                int wordEnd = info.Length;
                for (int index = 0; index < info.Length; index++)
                {
                    if (char.IsWhiteSpace(info[index]))
                    {
                        wordEnd = index;
                        break;
                    }
                }

                language = info.Substring(0, wordEnd);
            }

            fence = new FenceInfo(marker, length, language);
            return true;
        }

        private static bool IsFenceStartLine(string line)
        {
            FenceInfo fence;
            return TryReadFenceStart(line, out fence);
        }

        private static bool IsFenceClose(string line, FenceInfo fence)
        {
            int firstNonWhitespace;
            int indent = CountIndentColumns(line, out firstNonWhitespace);
            if (indent > 3 || firstNonWhitespace >= line.Length)
            {
                return false;
            }

            if (line[firstNonWhitespace] != fence.Marker)
            {
                return false;
            }

            int length = CountRun(line, firstNonWhitespace, fence.Marker);
            if (length < fence.Length)
            {
                return false;
            }

            for (int index = firstNonWhitespace + length; index < line.Length; index++)
            {
                if (!char.IsWhiteSpace(line[index]))
                {
                    return false;
                }
            }

            return true;
        }

        private static int ReadFenceBlock(
            List<string> lines,
            int start,
            FenceInfo fence,
            List<MarkdownBlock> blocks)
        {
            StringBuilder content = new StringBuilder();
            bool hasContent = false;
            int index = start + 1;
            while (index < lines.Count)
            {
                if (IsFenceClose(lines[index], fence))
                {
                    index++;
                    break;
                }

                if (hasContent)
                {
                    content.Append('\n');
                }

                content.Append(lines[index]);
                hasContent = true;
                index++;
            }

            if (hasContent)
            {
                content.Append('\n');
            }

            blocks.Add(new MarkdownCodeBlock(fence.Language, content.ToString()));
            return index;
        }

        private static bool TryReadListItem(string line, out ListItemInfo item)
        {
            item = null;

            int firstNonWhitespace;
            int indent = CountIndentColumns(line, out firstNonWhitespace);
            if (firstNonWhitespace >= line.Length)
            {
                return false;
            }

            bool ordered;
            int number = 0;
            int contentStart;
            char current = line[firstNonWhitespace];
            if (current == '-' || current == '*' || current == '+')
            {
                ordered = false;
                contentStart = firstNonWhitespace + 1;
                if (contentStart < line.Length && !IsSpaceOrTab(line[contentStart]))
                {
                    return false;
                }
            }
            else
            {
                int digits = 0;
                int cursor = firstNonWhitespace;
                while (cursor < line.Length
                    && line[cursor] >= '0'
                    && line[cursor] <= '9'
                    && digits < 9)
                {
                    digits++;
                    cursor++;
                }

                if (digits == 0 || cursor >= line.Length)
                {
                    return false;
                }

                char separator = line[cursor];
                if (separator != '.' && separator != ')')
                {
                    return false;
                }

                int afterMarker = cursor + 1;
                if (afterMarker < line.Length && !IsSpaceOrTab(line[afterMarker]))
                {
                    return false;
                }

                if (!int.TryParse(
                    line.Substring(firstNonWhitespace, digits),
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out number))
                {
                    return false;
                }

                ordered = true;
                contentStart = afterMarker;
            }

            while (contentStart < line.Length && IsSpaceOrTab(line[contentStart]))
            {
                contentStart++;
            }

            int level = indent / 2;
            if (level > MaxListLevel)
            {
                level = MaxListLevel;
            }

            item = new ListItemInfo(level, ordered, number, line.Substring(contentStart));
            return true;
        }

        private static bool IsListItemLine(string line)
        {
            ListItemInfo item;
            return TryReadListItem(line, out item);
        }

        private static bool IsSpaceOrTab(char value)
        {
            return value == ' ' || value == '\t';
        }

        private static int ReadList(
            List<string> lines,
            int start,
            ListItemInfo firstItem,
            List<MarkdownBlock> blocks)
        {
            List<MarkdownListItem> items = new List<MarkdownListItem>();
            bool isOrdered = firstItem.Ordered;
            int startNumber = firstItem.Ordered ? firstItem.Number : 1;
            int[] counters = new int[MaxListLevel + 1];
            bool[] initialized = new bool[MaxListLevel + 1];

            int index = start;
            while (index < lines.Count && !IsBlank(lines[index]))
            {
                ListItemInfo info;
                if (!TryReadListItem(lines[index], out info))
                {
                    break;
                }

                StringBuilder content = new StringBuilder(info.Content);
                index++;

                // 续行：缩进 >= 2 且不是新的列表项、围栏或标题。
                while (index < lines.Count && !IsBlank(lines[index]))
                {
                    if (IsListItemLine(lines[index])
                        || IsFenceStartLine(lines[index])
                        || IsHeadingLine(lines[index]))
                    {
                        break;
                    }

                    if (GetIndentColumns(lines[index]) < 2)
                    {
                        break;
                    }

                    content.Append('\n');
                    content.Append(lines[index].Trim());
                    index++;
                }

                string marker;
                if (info.Ordered)
                {
                    if (!initialized[info.Level])
                    {
                        counters[info.Level] = info.Number;
                        initialized[info.Level] = true;
                    }

                    marker = counters[info.Level].ToString(CultureInfo.InvariantCulture) + ".";
                    counters[info.Level] = counters[info.Level] + 1;
                }
                else
                {
                    marker = "•";
                }

                items.Add(new MarkdownListItem(
                    info.Level,
                    marker,
                    MarkdownInlineParser.Parse(content.ToString())));
            }

            blocks.Add(new MarkdownList(isOrdered, startNumber, items));
            return index;
        }

        private static int ReadQuote(List<string> lines, int start, List<MarkdownBlock> blocks)
        {
            StringBuilder content = new StringBuilder();
            int index = start;
            bool first = true;
            while (index < lines.Count && IsQuoteLine(lines[index]))
            {
                if (!first)
                {
                    content.Append('\n');
                }

                content.Append(StripQuotePrefix(lines[index]));
                first = false;
                index++;
            }

            MarkdownDocument inner = Parse(content.ToString());
            blocks.Add(new MarkdownQuote(inner.Blocks));
            return index;
        }

        private static int ReadParagraph(List<string> lines, int start, List<MarkdownBlock> blocks)
        {
            StringBuilder content = new StringBuilder();
            int index = start;
            bool first = true;
            while (index < lines.Count && !IsBlank(lines[index]))
            {
                if (!first && IsBlockStart(lines, index))
                {
                    break;
                }

                if (!first)
                {
                    content.Append('\n');
                }

                content.Append(lines[index].Trim());
                first = false;
                index++;
            }

            blocks.Add(new MarkdownParagraph(MarkdownInlineParser.Parse(content.ToString())));
            return index;
        }

        private static bool IsBlockStart(List<string> lines, int index)
        {
            string line = lines[index];
            return IsBlank(line)
                || IsFenceStartLine(line)
                || IsHeadingLine(line)
                || IsTableStart(lines, index)
                || IsRule(line)
                || IsQuoteLine(line)
                || IsListItemLine(line);
        }

        private sealed class FenceInfo
        {
            public FenceInfo(char marker, int length, string language)
            {
                Marker = marker;
                Length = length;
                Language = language;
            }

            public char Marker { get; }

            public int Length { get; }

            public string Language { get; }
        }

        private sealed class ListItemInfo
        {
            public ListItemInfo(int level, bool ordered, int number, string content)
            {
                Level = level;
                Ordered = ordered;
                Number = number;
                Content = content;
            }

            public int Level { get; }

            public bool Ordered { get; }

            public int Number { get; }

            public string Content { get; }
        }
    }
}
