using System;
using System.Text;

namespace MiaoWu.Core.Text
{
    /// <summary>
    /// 一次 UTF-8 文本检查 / 解码的结果。BOM 是编码标记，<see cref="Text"/> 已经剥掉它；
    /// 非法 UTF-8 返回 <see cref="IsValid"/> = false，不返回替换字符。
    /// </summary>
    public sealed class Utf8TextInfo
    {
        public Utf8TextInfo(bool isValid, bool hasBom, string text)
        {
            IsValid = isValid;
            HasBom = hasBom;
            Text = text ?? string.Empty;
        }

        /// <summary>字节是否是严格 UTF-8（样本末尾被截断的多字节序列允许不完整）。</summary>
        public bool IsValid { get; }

        /// <summary>是否以 UTF-8 BOM（EF BB BF）开头；BOM 不进入 <see cref="Text"/>。</summary>
        public bool HasBom { get; }

        /// <summary>解码后的文本；<see cref="IsValid"/> 为 false 时为空串。</summary>
        public string Text { get; }
    }

    /// <summary>
    /// 运行期唯一的文本读取入口：严格 UTF-8，只认 UTF-8 BOM。
    /// 与 DSH 的 <c>TextDecoder('utf-8', { fatal: true })</c> 对齐：不做 GBK / ANSI / UTF-16 猜测，
    /// 非法字节交回调用方转成“不是 UTF-8 文本”的结构化错误。
    /// </summary>
    public static class Utf8Text
    {
        /// <summary>文本编码在 UI 与工具结果里的唯一显示名。</summary>
        public const string DisplayName = "UTF-8";

        /// <summary>识别并解码整段字节；末尾不完整的多字节序列视为非法。</summary>
        public static Utf8TextInfo Read(byte[] bytes)
        {
            if (bytes == null)
            {
                throw new ArgumentNullException(nameof(bytes));
            }

            return ReadCore(bytes, bytes.Length, false);
        }

        /// <summary>
        /// 只识别前 <paramref name="count"/> 个字节；count 夹到 [0, bytes.Length]。
        /// 调用方传入的 count 小于数组长度时视为按上限截断，允许末尾不完整序列；
        /// 解码结果末尾可能带一个 U+FFFD。
        /// </summary>
        public static Utf8TextInfo Read(byte[] bytes, int count)
        {
            if (bytes == null)
            {
                throw new ArgumentNullException(nameof(bytes));
            }

            int clamped = Clamp(bytes.Length, count);
            return ReadCore(bytes, clamped, clamped < bytes.Length);
        }

        /// <summary>
        /// 显式指定是否允许末尾不完整序列；文件预览按字节上限截断时传 true，
        /// 完整文件读取传 false（与 DSH 的 fatal TextDecoder 在 EOF 的行为一致）。
        /// </summary>
        public static Utf8TextInfo Read(byte[] bytes, int count, bool allowTruncatedTail)
        {
            if (bytes == null)
            {
                throw new ArgumentNullException(nameof(bytes));
            }

            return ReadCore(bytes, Clamp(bytes.Length, count), allowTruncatedTail);
        }

        private static int Clamp(int length, int count)
        {
            if (count < 0)
            {
                return 0;
            }

            return count > length ? length : count;
        }

        private static Utf8TextInfo ReadCore(byte[] bytes, int count, bool allowTruncatedTail)
        {
            bool hasBom = count >= 3
                && bytes[0] == 0xEF
                && bytes[1] == 0xBB
                && bytes[2] == 0xBF;
            int offset = hasBom ? 3 : 0;

            if (!IsValidUtf8(bytes, offset, count - offset, allowTruncatedTail))
            {
                return new Utf8TextInfo(false, hasBom, string.Empty);
            }

            string text = TextEncodings.Utf8.GetString(bytes, offset, count - offset);
            return new Utf8TextInfo(true, hasBom, text);
        }

        /// <summary>
        /// 严格 UTF-8 校验：拒绝孤立续字节、非法首字节、过长编码、代理区与超出 U+10FFFF 的码点。
        /// allowTruncatedTail 只用于按字节上限截断的预览样本。
        /// </summary>
        private static bool IsValidUtf8(byte[] bytes, int start, int count, bool allowTruncatedTail)
        {
            int index = start;
            int end = start + count;
            while (index < end)
            {
                byte first = bytes[index];
                if (first <= 0x7F)
                {
                    index++;
                    continue;
                }

                int trailCount;
                int codePoint;
                if (first >= 0xC2 && first <= 0xDF)
                {
                    trailCount = 1;
                    codePoint = first & 0x1F;
                }
                else if (first >= 0xE0 && first <= 0xEF)
                {
                    trailCount = 2;
                    codePoint = first & 0x0F;
                }
                else if (first >= 0xF0 && first <= 0xF4)
                {
                    trailCount = 3;
                    codePoint = first & 0x07;
                }
                else
                {
                    // 0x80-0xC1（孤立续字节、过长编码首字节）与 0xF5-0xFF 都不合法。
                    return false;
                }

                for (int offset = 1; offset <= trailCount; offset++)
                {
                    if (index + offset >= end)
                    {
                        // 样本在序列中间被截断：只有按字节上限读取时才允许不完整。
                        return allowTruncatedTail;
                    }

                    byte next = bytes[index + offset];
                    if ((next & 0xC0) != 0x80)
                    {
                        return false;
                    }

                    if (offset == 1)
                    {
                        // 第二字节的边界直接决定是否过长编码、代理区或超出 U+10FFFF。
                        if (first == 0xE0 && next < 0xA0)
                        {
                            return false;
                        }

                        if (first == 0xED && next > 0x9F)
                        {
                            return false;
                        }

                        if (first == 0xF0 && next < 0x90)
                        {
                            return false;
                        }

                        if (first == 0xF4 && next > 0x8F)
                        {
                            return false;
                        }
                    }

                    codePoint = (codePoint << 6) | (next & 0x3F);
                }

                int minimum = trailCount == 1 ? 0x80 : (trailCount == 2 ? 0x800 : 0x10000);
                if (codePoint < minimum
                    || codePoint > 0x10FFFF
                    || (codePoint >= 0xD800 && codePoint <= 0xDFFF))
                {
                    return false;
                }

                index += trailCount + 1;
            }

            return true;
        }
    }
}
