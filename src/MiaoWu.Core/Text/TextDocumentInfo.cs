using System.Globalization;

namespace MiaoWu.Core.Text
{
    /// <summary>一段文本的换行风格。</summary>
    public enum TextNewLineStyle
    {
        /// <summary>没有任何换行符（空文本或单行）。</summary>
        None,

        /// <summary>只用 CRLF（Windows 默认）。</summary>
        CrLf,

        /// <summary>只用 LF。</summary>
        Lf,

        /// <summary>只用 CR。</summary>
        Cr,

        /// <summary>两种及以上混用。</summary>
        Mixed
    }

    /// <summary>统计 \r\n、单独的 \n、单独的 \r，用于中栏状态行显示。</summary>
    public static class NewLineDetector
    {
        /// <summary>判断文本的换行风格；null、空串与单行文本都返回 None。</summary>
        public static TextNewLineStyle Detect(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return TextNewLineStyle.None;
            }

            int crlf = 0;
            int lf = 0;
            int cr = 0;
            for (int i = 0; i < text.Length; i++)
            {
                char current = text[i];
                if (current == '\r')
                {
                    if (i + 1 < text.Length && text[i + 1] == '\n')
                    {
                        crlf++;
                        i++;
                    }
                    else
                    {
                        cr++;
                    }
                }
                else if (current == '\n')
                {
                    lf++;
                }
            }

            int kinds = 0;
            if (crlf > 0)
            {
                kinds++;
            }

            if (lf > 0)
            {
                kinds++;
            }

            if (cr > 0)
            {
                kinds++;
            }

            if (kinds == 0)
            {
                return TextNewLineStyle.None;
            }

            if (kinds > 1)
            {
                return TextNewLineStyle.Mixed;
            }

            if (crlf > 0)
            {
                return TextNewLineStyle.CrLf;
            }

            if (lf > 0)
            {
                return TextNewLineStyle.Lf;
            }

            return TextNewLineStyle.Cr;
        }

        /// <summary>状态行里的短名：CRLF / LF / CR / 混合 / —。</summary>
        public static string ToDisplayName(TextNewLineStyle style)
        {
            switch (style)
            {
                case TextNewLineStyle.CrLf:
                    return "CRLF";
                case TextNewLineStyle.Lf:
                    return "LF";
                case TextNewLineStyle.Cr:
                    return "CR";
                case TextNewLineStyle.Mixed:
                    return "混合";
                default:
                    return "—";
            }
        }
    }

    /// <summary>
    /// 逻辑行计数：末尾换行不额外产生一行，"a\n" 是 1 行而不是 2 行。
    /// </summary>
    public static class TextLineCounter
    {
        /// <summary>统计文本的逻辑行数；null 与空串为 0。</summary>
        public static int Count(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return 0;
            }

            int count = 0;
            int index = 0;
            while (index < text.Length)
            {
                char current = text[index];
                if (current == '\r')
                {
                    count++;
                    if (index + 1 < text.Length && text[index + 1] == '\n')
                    {
                        index += 2;
                    }
                    else
                    {
                        index++;
                    }
                }
                else if (current == '\n')
                {
                    count++;
                    index++;
                }
                else
                {
                    index++;
                }
            }

            char last = text[text.Length - 1];
            if (last != '\n' && last != '\r')
            {
                // 最后一行没有以换行结尾，也要算一行。
                count++;
            }

            return count;
        }
    }

    /// <summary>把字节数格式化成状态行里的 "4.1 KB"，固定一位小数并使用不变文化。</summary>
    public static class FileSizeFormatter
    {
        private const long Kilobyte = 1024L;
        private const long Megabyte = 1024L * 1024L;
        private const long Gigabyte = 1024L * 1024L * 1024L;

        /// <summary>小于 1 KB 用整数字节；负数按 0 处理。</summary>
        public static string Format(long bytes)
        {
            if (bytes < 0)
            {
                bytes = 0;
            }

            if (bytes < Kilobyte)
            {
                return bytes.ToString(CultureInfo.InvariantCulture) + " B";
            }

            if (bytes < Megabyte)
            {
                return FormatScaled(bytes, Kilobyte, "KB");
            }

            if (bytes < Gigabyte)
            {
                return FormatScaled(bytes, Megabyte, "MB");
            }

            return FormatScaled(bytes, Gigabyte, "GB");
        }

        private static string FormatScaled(long bytes, long divisor, string unit)
        {
            double value = (double)bytes / divisor;
            return value.ToString("F1", CultureInfo.InvariantCulture) + " " + unit;
        }
    }

    /// <summary>
    /// 中栏操作栏右端的元信息串，例如 "UTF-8 · LF · 4.1 KB · 128 行"。
    /// 分隔符是两侧各一个空格的 U+00B7。
    /// </summary>
    public static class DocumentMetaFormatter
    {
        private const string Separator = " \u00B7 ";
        private const string Placeholder = "—";

        /// <summary>拼接编码、换行、大小与行数。</summary>
        public static string Format(
            string encodingName,
            TextNewLineStyle newLine,
            long byteLength,
            int lineCount)
        {
            return Format(encodingName, newLine, byteLength, lineCount, null);
        }

        /// <summary>在末尾追加一段补充说明（例如 "已截断"）；extra 为空白时忽略。</summary>
        public static string Format(
            string encodingName,
            TextNewLineStyle newLine,
            long byteLength,
            int lineCount,
            string extra)
        {
            string name = string.IsNullOrEmpty(encodingName) ? Placeholder : encodingName;
            if (lineCount < 0)
            {
                lineCount = 0;
            }

            string result = name
                + Separator
                + NewLineDetector.ToDisplayName(newLine)
                + Separator
                + FileSizeFormatter.Format(byteLength)
                + Separator
                + lineCount.ToString(CultureInfo.InvariantCulture)
                + " 行";

            if (!string.IsNullOrWhiteSpace(extra))
            {
                result = result + Separator + extra;
            }

            return result;
        }
    }
}
