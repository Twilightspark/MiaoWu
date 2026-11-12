using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace MiaoWu.Infrastructure.Json
{
    /// <summary>
    /// 将 <see cref="JsonValue"/> 写回 JSON 文本，支持紧凑与缩进两种形式。
    /// </summary>
    /// <remarks>
    /// <para>字符串只转义 <c>"</c>、<c>\</c> 和所有小于 0x20 的控制字符；
    /// 中文等非 ASCII 字符原样输出，文件编码由调用方负责（建议 UTF-8 无 BOM）。</para>
    /// <para>缩进形式使用 2 个空格和 <c>\n</c> 换行，输出可被
    /// <see cref="JsonParser.Parse(string)"/> 原样还原。</para>
    /// </remarks>
    public static class JsonWriter
    {
        private const string IndentUnit = "  ";
        private const string LineBreak = "\n";

        /// <summary>
        /// 写出紧凑 JSON（不含多余空白），例如 <c>{"a":1}</c>。
        /// </summary>
        /// <param name="value">要写出的根节点。</param>
        /// <exception cref="ArgumentNullException"><paramref name="value"/> 为 <c>null</c>。</exception>
        /// <exception cref="ArgumentException">节点中包含 <see cref="double.NaN"/> 或无穷大数字。</exception>
        public static string Write(JsonValue value)
        {
            if (value == null)
            {
                throw new ArgumentNullException("value", "要写出的 JSON 值不能为空引用。");
            }

            StringBuilder builder = new StringBuilder(128);
            WriteValue(builder, value, 0, false);
            return builder.ToString();
        }

        /// <summary>
        /// 写出缩进 JSON（2 个空格缩进，<c>\n</c> 换行），便于人工查看与 diff。
        /// </summary>
        /// <param name="value">要写出的根节点。</param>
        /// <exception cref="ArgumentNullException"><paramref name="value"/> 为 <c>null</c>。</exception>
        /// <exception cref="ArgumentException">节点中包含 <see cref="double.NaN"/> 或无穷大数字。</exception>
        public static string WriteIndented(JsonValue value)
        {
            if (value == null)
            {
                throw new ArgumentNullException("value", "要写出的 JSON 值不能为空引用。");
            }

            StringBuilder builder = new StringBuilder(256);
            WriteValue(builder, value, 0, true);
            return builder.ToString();
        }

        private static void WriteValue(StringBuilder builder, JsonValue value, int level, bool indented)
        {
            switch (value.Kind)
            {
                case JsonKind.Null:
                    builder.Append("null");
                    return;
                case JsonKind.Boolean:
                    builder.Append(value.AsBoolean() ? "true" : "false");
                    return;
                case JsonKind.Number:
                    builder.Append(FormatNumber(value.AsDouble()));
                    return;
                case JsonKind.String:
                    WriteString(builder, value.AsString());
                    return;
                case JsonKind.Array:
                    WriteArray(builder, value, level, indented);
                    return;
                case JsonKind.Object:
                    WriteObject(builder, value, level, indented);
                    return;
                default:
                    throw new ArgumentException("未知的 JSON 值种类，无法写出。", "value");
            }
        }

        private static void WriteArray(StringBuilder builder, JsonValue value, int level, bool indented)
        {
            IReadOnlyList<JsonValue> items = value.Items;
            if (items.Count == 0)
            {
                builder.Append("[]");
                return;
            }

            builder.Append('[');
            for (int i = 0; i < items.Count; i++)
            {
                if (i > 0)
                {
                    builder.Append(',');
                }

                if (indented)
                {
                    builder.Append(LineBreak);
                    AppendIndent(builder, level + 1);
                }

                WriteValue(builder, items[i] ?? JsonValue.Null, level + 1, indented);
            }

            if (indented)
            {
                builder.Append(LineBreak);
                AppendIndent(builder, level);
            }

            builder.Append(']');
        }

        private static void WriteObject(StringBuilder builder, JsonValue value, int level, bool indented)
        {
            IReadOnlyList<KeyValuePair<string, JsonValue>> members = value.Members;
            if (members.Count == 0)
            {
                builder.Append("{}");
                return;
            }

            builder.Append('{');
            for (int i = 0; i < members.Count; i++)
            {
                if (i > 0)
                {
                    builder.Append(',');
                }

                if (indented)
                {
                    builder.Append(LineBreak);
                    AppendIndent(builder, level + 1);
                }

                WriteString(builder, members[i].Key);
                builder.Append(indented ? ": " : ":");
                WriteValue(builder, members[i].Value ?? JsonValue.Null, level + 1, indented);
            }

            if (indented)
            {
                builder.Append(LineBreak);
                AppendIndent(builder, level);
            }

            builder.Append('}');
        }

        private static string FormatNumber(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                throw new ArgumentException("JSON 不支持 NaN 或无穷大数字。", "value");
            }

            string text = value.ToString("R", CultureInfo.InvariantCulture);

            // "R" 对整数值本就不带小数部分，这里再兜底避免出现 ".0"。
            if (text.Length > 2 && text.EndsWith(".0", StringComparison.Ordinal))
            {
                text = text.Substring(0, text.Length - 2);
            }

            return text;
        }

        private static void WriteString(StringBuilder builder, string value)
        {
            builder.Append('"');

            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                switch (c)
                {
                    case '"':
                        builder.Append("\\\"");
                        break;
                    case '\\':
                        builder.Append("\\\\");
                        break;
                    case '\b':
                        builder.Append("\\b");
                        break;
                    case '\f':
                        builder.Append("\\f");
                        break;
                    case '\n':
                        builder.Append("\\n");
                        break;
                    case '\r':
                        builder.Append("\\r");
                        break;
                    case '\t':
                        builder.Append("\\t");
                        break;
                    default:
                        if (c < ' ')
                        {
                            builder.Append("\\u");
                            builder.Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            builder.Append(c);
                        }

                        break;
                }
            }

            builder.Append('"');
        }

        private static void AppendIndent(StringBuilder builder, int level)
        {
            for (int i = 0; i < level; i++)
            {
                builder.Append(IndentUnit);
            }
        }
    }
}
