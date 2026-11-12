using System;
using System.Globalization;
using System.Text;

namespace MiaoWu.Infrastructure.Json
{
    /// <summary>
    /// 严格的 RFC 8259 JSON 解析器（手写递归下降，无第三方依赖）。
    /// </summary>
    /// <remarks>
    /// <para>接受首尾空白，允许开头出现一个 U+FEFF；不接受注释、尾逗号、单引号键或未加引号的键。</para>
    /// <para>对象中的重复键以后出现的值为准，位置保持在第一次出现处。</para>
    /// <para>嵌套深度上限为 64 层，避免恶意输入导致栈溢出。</para>
    /// </remarks>
    public static class JsonParser
    {
        /// <summary>对象 / 数组的最大嵌套层数。</summary>
        private const int MaxDepth = 64;

        /// <summary>
        /// 解析 JSON 文本，返回可读写的 DOM 根节点。
        /// </summary>
        /// <param name="text">JSON 文本。</param>
        /// <returns>解析得到的根节点；顶层 <c>null</c> 返回 <see cref="JsonValue.Null"/>。</returns>
        /// <exception cref="ArgumentNullException"><paramref name="text"/> 为 <c>null</c>。</exception>
        /// <exception cref="JsonParseException">文本不是合法的 JSON。</exception>
        public static JsonValue Parse(string text)
        {
            if (text == null)
            {
                throw new ArgumentNullException("text", "要解析的 JSON 文本不能为空引用。");
            }

            return new JsonReader(text).ParseDocument();
        }

        /// <summary>
        /// 单次解析的状态载体，避免为每个节点分配对象。
        /// </summary>
        private sealed class JsonReader
        {
            private readonly string _text;
            private int _position;

            internal JsonReader(string text)
            {
                _text = text;
                _position = 0;
            }

            internal JsonValue ParseDocument()
            {
                // 允许 UTF-8 文件开头残留的 BOM。
                if (_text.Length > 0 && _text[0] == '\uFEFF')
                {
                    _position = 1;
                }

                SkipWhitespace();
                if (_position >= _text.Length)
                {
                    throw new JsonParseException("JSON 文本为空，无法解析。", _position);
                }

                JsonValue value = ParseValue(0);

                SkipWhitespace();
                if (_position < _text.Length)
                {
                    throw new JsonParseException(
                        string.Format(
                            CultureInfo.InvariantCulture,
                            "JSON 根值之后存在多余字符 '{0}'。",
                            Describe(_text[_position])),
                        _position);
                }

                return value;
            }

            /// <summary>
            /// 解析一个值。<paramref name="depth"/> 是当前已进入的容器层数（根值为 0）。
            /// </summary>
            private JsonValue ParseValue(int depth)
            {
                if (_position >= _text.Length)
                {
                    throw new JsonParseException("JSON 文本意外结束，期望一个值。", _position);
                }

                char c = _text[_position];
                if (c == '{')
                {
                    return ParseObject(depth + 1);
                }

                if (c == '[')
                {
                    return ParseArray(depth + 1);
                }

                if (c == '"')
                {
                    return JsonValue.From(ParseString());
                }

                if (c == 't')
                {
                    return ParseLiteral("true", JsonValue.From(true));
                }

                if (c == 'f')
                {
                    return ParseLiteral("false", JsonValue.From(false));
                }

                if (c == 'n')
                {
                    return ParseLiteral("null", JsonValue.Null);
                }

                if (c == '-' || (c >= '0' && c <= '9'))
                {
                    return ParseNumber();
                }

                throw new JsonParseException(
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "无法识别的字符 '{0}'，期望一个 JSON 值。",
                        Describe(c)),
                    _position);
            }

            private JsonValue ParseLiteral(string token, JsonValue value)
            {
                if (_position + token.Length > _text.Length ||
                    string.CompareOrdinal(_text, _position, token, 0, token.Length) != 0)
                {
                    throw new JsonParseException(
                        string.Format(
                            CultureInfo.InvariantCulture,
                            "无效的字面量，期望 '{0}'。",
                            token),
                        _position);
                }

                _position += token.Length;
                return value;
            }

            private JsonValue ParseNumber()
            {
                int start = _position;

                if (_text[_position] == '-')
                {
                    _position++;
                    if (_position >= _text.Length)
                    {
                        throw new JsonParseException("数字意外结束，'-' 之后期望数字。", _position);
                    }
                }

                char c = _text[_position];
                if (c == '0')
                {
                    _position++;
                    if (_position < _text.Length && IsDigit(_text[_position]))
                    {
                        throw new JsonParseException("数字不允许以 0 开头。", _position);
                    }
                }
                else if (c >= '1' && c <= '9')
                {
                    _position++;
                    while (_position < _text.Length && IsDigit(_text[_position]))
                    {
                        _position++;
                    }
                }
                else
                {
                    throw new JsonParseException(
                        string.Format(
                            CultureInfo.InvariantCulture,
                            "无法识别的字符 '{0}'，期望数字。",
                            Describe(c)),
                        _position);
                }

                if (_position < _text.Length && _text[_position] == '.')
                {
                    _position++;
                    if (_position >= _text.Length || !IsDigit(_text[_position]))
                    {
                        throw new JsonParseException("小数点之后至少需要一位数字。", _position);
                    }

                    while (_position < _text.Length && IsDigit(_text[_position]))
                    {
                        _position++;
                    }
                }

                if (_position < _text.Length && (_text[_position] == 'e' || _text[_position] == 'E'))
                {
                    _position++;
                    if (_position < _text.Length && (_text[_position] == '+' || _text[_position] == '-'))
                    {
                        _position++;
                    }

                    if (_position >= _text.Length || !IsDigit(_text[_position]))
                    {
                        throw new JsonParseException("指数部分之后至少需要一位数字。", _position);
                    }

                    while (_position < _text.Length && IsDigit(_text[_position]))
                    {
                        _position++;
                    }
                }

                string literal = _text.Substring(start, _position - start);
                double number;
                try
                {
                    number = double.Parse(literal, NumberStyles.Float, CultureInfo.InvariantCulture);
                }
                catch (Exception error) when (error is FormatException || error is OverflowException)
                {
                    throw new JsonParseException(
                        string.Format(
                            CultureInfo.InvariantCulture,
                            "数字 '{0}' 超出可表示范围。",
                            literal),
                        start);
                }

                if (double.IsNaN(number) || double.IsInfinity(number))
                {
                    throw new JsonParseException(
                        string.Format(
                            CultureInfo.InvariantCulture,
                            "数字 '{0}' 超出可表示范围。",
                            literal),
                        start);
                }

                return JsonValue.From(number);
            }

            /// <summary>
            /// 解析双引号字符串（调用时 <see cref="_position"/> 指向起始引号）。
            /// </summary>
            private string ParseString()
            {
                int start = _position;
                _position++; // 跳过起始引号
                StringBuilder builder = new StringBuilder();

                while (true)
                {
                    if (_position >= _text.Length)
                    {
                        throw new JsonParseException("字符串未闭合，缺少结束引号。", start);
                    }

                    char c = _text[_position];
                    if (c == '"')
                    {
                        _position++;
                        return builder.ToString();
                    }

                    if (c == '\\')
                    {
                        ParseEscape(builder);
                        continue;
                    }

                    if (c < ' ')
                    {
                        throw new JsonParseException(
                            string.Format(
                                CultureInfo.InvariantCulture,
                                "字符串中不允许出现未转义的控制字符 U+{0:X4}。",
                                (int)c),
                            _position);
                    }

                    builder.Append(c);
                    _position++;
                }
            }

            private void ParseEscape(StringBuilder builder)
            {
                int escapeStart = _position;
                _position++; // 跳过反斜杠

                if (_position >= _text.Length)
                {
                    throw new JsonParseException("字符串未闭合，转义序列不完整。", escapeStart);
                }

                char c = _text[_position];
                switch (c)
                {
                    case '"':
                        builder.Append('"');
                        _position++;
                        return;
                    case '\\':
                        builder.Append('\\');
                        _position++;
                        return;
                    case '/':
                        builder.Append('/');
                        _position++;
                        return;
                    case 'b':
                        builder.Append('\b');
                        _position++;
                        return;
                    case 'f':
                        builder.Append('\f');
                        _position++;
                        return;
                    case 'n':
                        builder.Append('\n');
                        _position++;
                        return;
                    case 'r':
                        builder.Append('\r');
                        _position++;
                        return;
                    case 't':
                        builder.Append('\t');
                        _position++;
                        return;
                    case 'u':
                        _position++;
                        builder.Append(ParseUnicodeEscape());
                        return;
                    default:
                        throw new JsonParseException(
                            string.Format(
                                CultureInfo.InvariantCulture,
                                "无效的转义序列 '\\{0}'。",
                                Describe(c)),
                            _position);
                }
            }

            /// <summary>
            /// 解析 \uXXXX 中的四位十六进制数字（调用时 <see cref="_position"/> 指向第一位）。
            /// 代理对不做配对校验，按两个 UTF-16 码元原样保存。
            /// </summary>
            private char ParseUnicodeEscape()
            {
                if (_position + 4 > _text.Length)
                {
                    throw new JsonParseException("\\u 转义需要 4 位十六进制数字。", _position);
                }

                int value = 0;
                for (int i = 0; i < 4; i++)
                {
                    char digit = _text[_position + i];
                    int hex = HexValue(digit);
                    if (hex < 0)
                    {
                        throw new JsonParseException(
                            string.Format(
                                CultureInfo.InvariantCulture,
                                "\\u 转义中出现非法的十六进制数字 '{0}'。",
                                Describe(digit)),
                            _position + i);
                    }

                    value = (value << 4) | hex;
                }

                _position += 4;
                return (char)value;
            }

            private JsonValue ParseObject(int level)
            {
                if (level > MaxDepth)
                {
                    throw DepthError();
                }

                int opening = _position;
                _position++; // 跳过 '{'

                JsonValue result = JsonValue.NewObject();

                SkipWhitespace();
                if (_position >= _text.Length)
                {
                    throw Unterminated(opening, '}');
                }

                if (_text[_position] == '}')
                {
                    _position++;
                    return result;
                }

                while (true)
                {
                    SkipWhitespace();
                    if (_position >= _text.Length)
                    {
                        throw Unterminated(opening, '}');
                    }

                    char c = _text[_position];
                    if (c == '}')
                    {
                        // 只有在刚消费完逗号时才会走到这里。
                        throw new JsonParseException("对象末尾不允许出现多余的逗号。", _position);
                    }

                    if (c != '"')
                    {
                        throw new JsonParseException(
                            string.Format(
                                CultureInfo.InvariantCulture,
                                "对象的键必须是双引号字符串，实际遇到 '{0}'。",
                                Describe(c)),
                            _position);
                    }

                    string key = ParseString();

                    SkipWhitespace();
                    if (_position >= _text.Length)
                    {
                        throw Unterminated(opening, '}');
                    }

                    if (_text[_position] != ':')
                    {
                        throw new JsonParseException("对象的键之后缺少 ':'。", _position);
                    }

                    _position++; // 跳过 ':'

                    SkipWhitespace();
                    JsonValue memberValue = ParseValue(level);
                    result.Add(key, memberValue);

                    SkipWhitespace();
                    if (_position >= _text.Length)
                    {
                        throw Unterminated(opening, '}');
                    }

                    char separator = _text[_position];
                    if (separator == ',')
                    {
                        _position++;
                        continue;
                    }

                    if (separator == '}')
                    {
                        _position++;
                        return result;
                    }

                    throw new JsonParseException(
                        string.Format(
                            CultureInfo.InvariantCulture,
                            "对象成员之间缺少 ','，或对象未正确以 '}}' 结束（遇到 '{0}'）。",
                            Describe(separator)),
                        _position);
                }
            }

            private JsonValue ParseArray(int level)
            {
                if (level > MaxDepth)
                {
                    throw DepthError();
                }

                int opening = _position;
                _position++; // 跳过 '['

                JsonValue result = JsonValue.NewArray();

                SkipWhitespace();
                if (_position >= _text.Length)
                {
                    throw Unterminated(opening, ']');
                }

                if (_text[_position] == ']')
                {
                    _position++;
                    return result;
                }

                while (true)
                {
                    SkipWhitespace();
                    if (_position >= _text.Length)
                    {
                        throw Unterminated(opening, ']');
                    }

                    if (_text[_position] == ']')
                    {
                        // 只有在刚消费完逗号时才会走到这里。
                        throw new JsonParseException("数组末尾不允许出现多余的逗号。", _position);
                    }

                    result.Add(ParseValue(level));

                    SkipWhitespace();
                    if (_position >= _text.Length)
                    {
                        throw Unterminated(opening, ']');
                    }

                    char separator = _text[_position];
                    if (separator == ',')
                    {
                        _position++;
                        continue;
                    }

                    if (separator == ']')
                    {
                        _position++;
                        return result;
                    }

                    throw new JsonParseException(
                        string.Format(
                            CultureInfo.InvariantCulture,
                            "数组元素之间缺少 ','，或数组未正确以 ']' 结束（遇到 '{0}'）。",
                            Describe(separator)),
                        _position);
                }
            }

            private void SkipWhitespace()
            {
                while (_position < _text.Length)
                {
                    char c = _text[_position];
                    if (c != ' ' && c != '\t' && c != '\n' && c != '\r')
                    {
                        return;
                    }

                    _position++;
                }
            }

            private JsonParseException DepthError()
            {
                return new JsonParseException(
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "JSON 嵌套层级超过上限 {0} 层。",
                        MaxDepth),
                    _position);
            }

            private JsonParseException Unterminated(int opening, char closing)
            {
                return new JsonParseException(
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "JSON 文本意外结束：'{0}' 没有对应的 '{1}'。",
                        _text[opening],
                        closing),
                    _text.Length);
            }

            private static bool IsDigit(char c)
            {
                return c >= '0' && c <= '9';
            }

            private static int HexValue(char c)
            {
                if (c >= '0' && c <= '9')
                {
                    return c - '0';
                }

                if (c >= 'a' && c <= 'f')
                {
                    return (c - 'a') + 10;
                }

                if (c >= 'A' && c <= 'F')
                {
                    return (c - 'A') + 10;
                }

                return -1;
            }

            private static string Describe(char c)
            {
                if (c < ' ')
                {
                    return "\\u" + ((int)c).ToString("X4", CultureInfo.InvariantCulture);
                }

                return c.ToString();
            }
        }
    }
}
