using System;
using System.Collections.Generic;
using System.Text;

namespace MiaoWu.Core.Markdown
{
    /// <summary>行内元素的种类。</summary>
    public enum MarkdownInlineKind
    {
        /// <summary>普通文本，可能包含软换行。</summary>
        Text,

        /// <summary>行内代码，内容不做任何解析。</summary>
        Code,

        /// <summary>斜体。</summary>
        Emphasis,

        /// <summary>加粗。</summary>
        Strong,

        /// <summary>链接。</summary>
        Link
    }

    /// <summary>
    /// 一段行内内容。加粗与斜体不嵌套：其 Text 是去掉标记、解码转义与实体后的纯文本。
    /// </summary>
    public sealed class MarkdownInline
    {
        public MarkdownInline(MarkdownInlineKind kind, string text, string url = null)
        {
            Kind = kind;
            Text = text ?? string.Empty;
            Url = kind == MarkdownInlineKind.Link ? url : null;
        }

        public MarkdownInlineKind Kind { get; }

        /// <summary>显示文字；Link 时是链接文字。</summary>
        public string Text { get; }

        /// <summary>链接地址，仅 Link 非 null。</summary>
        public string Url { get; }

        /// <summary>创建普通文本。</summary>
        public static MarkdownInline Plain(string text)
        {
            return new MarkdownInline(MarkdownInlineKind.Text, text);
        }
    }

    /// <summary>
    /// 行内扫描器。按字符顺序单次前进，不做递归回溯；未闭合的标记按字面文本输出。
    /// </summary>
    internal static class MarkdownInlineParser
    {
        private const string AmpEntity = "&amp;";

        internal static IReadOnlyList<MarkdownInline> Parse(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return Array.Empty<MarkdownInline>();
            }

            List<MarkdownInline> result = new List<MarkdownInline>();
            StringBuilder pending = new StringBuilder(text.Length);
            int index = 0;
            while (index < text.Length)
            {
                char current = text[index];

                if (current == '\\')
                {
                    if (index + 1 < text.Length && IsAsciiPunctuation(text[index + 1]))
                    {
                        pending.Append(text[index + 1]);
                        index += 2;
                        continue;
                    }

                    pending.Append(current);
                    index++;
                    continue;
                }

                if (current == '`')
                {
                    int runLength = CountRun(text, index, '`');
                    int closing = FindClosingRun(text, index + runLength, '`', runLength, false);
                    if (closing >= 0)
                    {
                        Flush(result, pending);
                        string code = text.Substring(index + runLength, closing - index - runLength);
                        if (code.Length > 0)
                        {
                            result.Add(new MarkdownInline(MarkdownInlineKind.Code, code));
                        }

                        index = closing + runLength;
                        continue;
                    }

                    pending.Append(text, index, runLength);
                    index += runLength;
                    continue;
                }

                if (current == '*' || current == '_')
                {
                    MarkdownInline formatted;
                    int formattedEnd;
                    if (TryReadFormatted(text, index, out formatted, out formattedEnd))
                    {
                        Flush(result, pending);
                        result.Add(formatted);
                        index = formattedEnd;
                        continue;
                    }

                    pending.Append(current);
                    index++;
                    continue;
                }

                if (current == '!')
                {
                    int imageEnd = FindImageEnd(text, index);
                    if (imageEnd > index)
                    {
                        pending.Append(text, index, imageEnd - index);
                        index = imageEnd;
                        continue;
                    }
                }

                if (current == '[')
                {
                    MarkdownInline link;
                    int linkEnd;
                    if (TryReadLink(text, index, out link, out linkEnd))
                    {
                        Flush(result, pending);
                        result.Add(link);
                        index = linkEnd;
                        continue;
                    }
                }

                if (current == '&')
                {
                    string decoded;
                    int consumed;
                    if (TryDecodeEntity(text, index, out decoded, out consumed))
                    {
                        pending.Append(decoded);
                        index += consumed;
                        continue;
                    }
                }

                pending.Append(current);
                index++;
            }

            Flush(result, pending);
            return result.AsReadOnly();
        }

        private static bool TryReadFormatted(
            string text,
            int start,
            out MarkdownInline inline,
            out int next)
        {
            inline = null;
            next = start;

            char marker = text[start];
            int runLength = CountRun(text, start, marker);
            bool underscore = marker == '_';
            if (underscore && start > 0 && IsWordCharacter(text[start - 1]))
            {
                return false;
            }

            if (runLength >= 3
                && TryReadFormattedAt(text, start, marker, 3, underscore, out inline, out next))
            {
                return true;
            }

            if (runLength >= 2
                && TryReadFormattedAt(text, start, marker, 2, underscore, out inline, out next))
            {
                return true;
            }

            return TryReadFormattedAt(text, start, marker, 1, underscore, out inline, out next);
        }

        private static bool TryReadFormattedAt(
            string text,
            int start,
            char marker,
            int length,
            bool underscore,
            out MarkdownInline inline,
            out int next)
        {
            inline = null;
            next = start;

            int contentStart = start + length;
            int closing = FindClosingRun(text, contentStart, marker, length, underscore);
            if (closing <= contentStart)
            {
                return false;
            }

            string content = DecodeLiteral(text.Substring(contentStart, closing - contentStart));
            if (content.Length == 0)
            {
                return false;
            }

            MarkdownInlineKind kind = length >= 2
                ? MarkdownInlineKind.Strong
                : MarkdownInlineKind.Emphasis;
            inline = new MarkdownInline(kind, content);
            next = closing + length;
            return true;
        }

        private static int FindClosingRun(
            string text,
            int start,
            char marker,
            int length,
            bool underscoreBoundary)
        {
            int index = start;
            while (index <= text.Length - length)
            {
                char current = text[index];
                if (current == '\\')
                {
                    index += 2;
                    continue;
                }

                if (current == marker && CountRun(text, index, marker) >= length)
                {
                    if (!underscoreBoundary || !IsWordCharacterFollowing(text, index + length))
                    {
                        return index;
                    }

                    index += CountRun(text, index, marker);
                    continue;
                }

                index++;
            }

            return -1;
        }

        private static bool TryReadLink(
            string text,
            int start,
            out MarkdownInline inline,
            out int next)
        {
            inline = null;
            next = start;

            int labelEnd = FindUnescaped(text, start + 1, ']');
            if (labelEnd < 0 || labelEnd + 1 >= text.Length || text[labelEnd + 1] != '(')
            {
                return false;
            }

            int urlEnd = FindUnescaped(text, labelEnd + 2, ')');
            if (urlEnd < 0)
            {
                return false;
            }

            string label = text.Substring(start + 1, labelEnd - start - 1);
            string url = text.Substring(labelEnd + 2, urlEnd - labelEnd - 2);
            inline = new MarkdownInline(MarkdownInlineKind.Link, label, url);
            next = urlEnd + 1;
            return true;
        }

        /// <summary>
        /// 图片语法按普通文本处理：把 ![alt](url) 原样当作文字，不产生图片或链接。
        /// </summary>
        private static int FindImageEnd(string text, int start)
        {
            if (start + 1 >= text.Length || text[start] != '!' || text[start + 1] != '[')
            {
                return -1;
            }

            int labelEnd = FindUnescaped(text, start + 2, ']');
            if (labelEnd < 0 || labelEnd + 1 >= text.Length || text[labelEnd + 1] != '(')
            {
                return -1;
            }

            int urlEnd = FindUnescaped(text, labelEnd + 2, ')');
            return urlEnd < 0 ? -1 : urlEnd + 1;
        }

        private static int FindUnescaped(string text, int start, char target)
        {
            int index = start;
            while (index < text.Length)
            {
                char current = text[index];
                if (current == '\\')
                {
                    index += 2;
                    continue;
                }

                if (current == target)
                {
                    return index;
                }

                index++;
            }

            return -1;
        }

        private static string DecodeLiteral(string text)
        {
            StringBuilder builder = new StringBuilder(text.Length);
            int index = 0;
            while (index < text.Length)
            {
                char current = text[index];
                if (current == '\\'
                    && index + 1 < text.Length
                    && IsAsciiPunctuation(text[index + 1]))
                {
                    builder.Append(text[index + 1]);
                    index += 2;
                    continue;
                }

                if (current == '&')
                {
                    string decoded;
                    int consumed;
                    if (TryDecodeEntity(text, index, out decoded, out consumed))
                    {
                        builder.Append(decoded);
                        index += consumed;
                        continue;
                    }
                }

                builder.Append(current);
                index++;
            }

            return builder.ToString();
        }

        private static bool TryDecodeEntity(
            string text,
            int start,
            out string value,
            out int consumed)
        {
            if (MatchAt(text, start, AmpEntity))
            {
                value = "&";
                consumed = AmpEntity.Length;
                return true;
            }

            if (MatchAt(text, start, "&lt;"))
            {
                value = "<";
                consumed = 4;
                return true;
            }

            if (MatchAt(text, start, "&gt;"))
            {
                value = ">";
                consumed = 4;
                return true;
            }

            if (MatchAt(text, start, "&quot;"))
            {
                value = "\"";
                consumed = 6;
                return true;
            }

            if (MatchAt(text, start, "&#39;"))
            {
                value = "'";
                consumed = 5;
                return true;
            }

            value = null;
            consumed = 0;
            return false;
        }

        private static bool MatchAt(string text, int start, string token)
        {
            if (start + token.Length > text.Length)
            {
                return false;
            }

            return string.CompareOrdinal(text, start, token, 0, token.Length) == 0;
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

        private static bool IsWordCharacterFollowing(string text, int index)
        {
            return index < text.Length && IsWordCharacter(text[index]);
        }

        private static bool IsWordCharacter(char value)
        {
            return char.IsLetterOrDigit(value) || value == '_';
        }

        /// <summary>CommonMark 的 ASCII 标点集合，只有这些字符可以被反斜杠转义。</summary>
        private static bool IsAsciiPunctuation(char value)
        {
            return (value >= '!' && value <= '/')
                || (value >= ':' && value <= '@')
                || (value >= '[' && value <= '`')
                || (value >= '{' && value <= '~');
        }

        private static void Flush(List<MarkdownInline> result, StringBuilder pending)
        {
            if (pending.Length == 0)
            {
                return;
            }

            AppendText(result, pending.ToString());
            pending.Length = 0;
        }

        private static void AppendText(List<MarkdownInline> result, string value)
        {
            if (value.Length == 0)
            {
                return;
            }

            if (result.Count > 0)
            {
                MarkdownInline last = result[result.Count - 1];
                if (last.Kind == MarkdownInlineKind.Text)
                {
                    result[result.Count - 1] = new MarkdownInline(
                        MarkdownInlineKind.Text,
                        last.Text + value);
                    return;
                }
            }

            result.Add(new MarkdownInline(MarkdownInlineKind.Text, value));
        }
    }
}
