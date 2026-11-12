using System;
using System.Collections.Generic;

namespace MiaoWu.Core.Markdown
{
    /// <summary>块级元素的基类。</summary>
    public abstract class MarkdownBlock
    {
    }

    /// <summary>标题。Level 固定为 1..3，4～6 级标题会被夹到 3。</summary>
    public sealed class MarkdownHeading : MarkdownBlock
    {
        public MarkdownHeading(int level, IReadOnlyList<MarkdownInline> inlines)
        {
            Level = level < 1 ? 1 : (level > 3 ? 3 : level);
            Inlines = MarkdownCollections.Copy(inlines);
        }

        public int Level { get; }

        public IReadOnlyList<MarkdownInline> Inlines { get; }
    }

    /// <summary>段落。软换行保留在相邻行之间的 "\n" 里。</summary>
    public sealed class MarkdownParagraph : MarkdownBlock
    {
        public MarkdownParagraph(IReadOnlyList<MarkdownInline> inlines)
        {
            Inlines = MarkdownCollections.Copy(inlines);
        }

        public IReadOnlyList<MarkdownInline> Inlines { get; }
    }

    /// <summary>列表中的一项。Level 从 0 开始，最多 3。</summary>
    public sealed class MarkdownListItem
    {
        public MarkdownListItem(int level, string marker, IReadOnlyList<MarkdownInline> inlines)
        {
            Level = level < 0 ? 0 : (level > 3 ? 3 : level);
            Marker = marker ?? string.Empty;
            Inlines = MarkdownCollections.Copy(inlines);
        }

        public int Level { get; }

        /// <summary>无序列表为 "•"，有序列表为 "1." "2." 等。</summary>
        public string Marker { get; }

        public IReadOnlyList<MarkdownInline> Inlines { get; }
    }

    /// <summary>一个连续的列表；嵌套层级由项上的 Level 表达。</summary>
    public sealed class MarkdownList : MarkdownBlock
    {
        public MarkdownList(bool isOrdered, int start, IReadOnlyList<MarkdownListItem> items)
        {
            IsOrdered = isOrdered;
            Start = start;
            Items = MarkdownCollections.Copy(items);
        }

        public bool IsOrdered { get; }

        /// <summary>有序列表的首个编号；无序列表为 1。</summary>
        public int Start { get; }

        public IReadOnlyList<MarkdownListItem> Items { get; }
    }

    /// <summary>引用块，内部可以继续包含任意块（嵌套引用、列表、代码等）。</summary>
    public sealed class MarkdownQuote : MarkdownBlock
    {
        public MarkdownQuote(IReadOnlyList<MarkdownBlock> blocks)
        {
            Blocks = MarkdownCollections.Copy(blocks);
        }

        public IReadOnlyList<MarkdownBlock> Blocks { get; }
    }

    /// <summary>围栏代码块。代码内容不做任何 Markdown 解析，也不会执行 HTML。</summary>
    public sealed class MarkdownCodeBlock : MarkdownBlock
    {
        public MarkdownCodeBlock(string language, string text)
        {
            Language = string.IsNullOrEmpty(language) ? null : language;
            Text = text ?? string.Empty;
        }

        /// <summary>info string 的第一个词；没有时为 null。</summary>
        public string Language { get; }

        /// <summary>不含围栏的代码文本；行尾统一为 "\n"，有内容时末尾保留一个 "\n"。</summary>
        public string Text { get; }
    }

    /// <summary>管道表格。单元格是未经行内解析的纯文本。</summary>
    public sealed class MarkdownTable : MarkdownBlock
    {
        public MarkdownTable(
            IReadOnlyList<string> headers,
            IReadOnlyList<IReadOnlyList<string>> rows)
        {
            Headers = MarkdownCollections.Copy(headers);

            List<IReadOnlyList<string>> normalizedRows = new List<IReadOnlyList<string>>();
            if (rows != null)
            {
                for (int rowIndex = 0; rowIndex < rows.Count; rowIndex++)
                {
                    IReadOnlyList<string> row = rows[rowIndex];
                    string[] cells = new string[Headers.Count];
                    for (int column = 0; column < cells.Length; column++)
                    {
                        cells[column] = row != null && column < row.Count && row[column] != null
                            ? row[column]
                            : string.Empty;
                    }

                    normalizedRows.Add(Array.AsReadOnly(cells));
                }
            }

            Rows = normalizedRows.AsReadOnly();
        }

        public IReadOnlyList<string> Headers { get; }

        /// <summary>数据行；每行列数与 Headers 相同，缺列补空字符串。</summary>
        public IReadOnlyList<IReadOnlyList<string>> Rows { get; }
    }

    /// <summary>分隔线。</summary>
    public sealed class MarkdownRule : MarkdownBlock
    {
    }

    /// <summary>解析结果。为空文档时 Blocks 为空且 IsEmpty 为 true。</summary>
    public sealed class MarkdownDocument
    {
        public MarkdownDocument(IReadOnlyList<MarkdownBlock> blocks)
        {
            Blocks = MarkdownCollections.Copy(blocks);
        }

        public IReadOnlyList<MarkdownBlock> Blocks { get; }

        public bool IsEmpty
        {
            get { return Blocks.Count == 0; }
        }
    }

    /// <summary>把外部列表复制为只读快照，保证模型构造后不可变。</summary>
    internal static class MarkdownCollections
    {
        internal static IReadOnlyList<T> Copy<T>(IReadOnlyList<T> items)
        {
            if (items == null || items.Count == 0)
            {
                return Array.Empty<T>();
            }

            List<T> copy = new List<T>(items.Count);
            for (int index = 0; index < items.Count; index++)
            {
                copy.Add(items[index]);
            }

            return copy.AsReadOnly();
        }
    }
}
