using System;
using System.Text;

namespace MiaoWu.Core.Text
{
    /// <summary>
    /// 一次编码识别的结果。调用方解码时应先跳过 <see cref="BomLength"/> 个字节，
    /// 否则 BOM 会以 U+FEFF 的形式出现在正文开头。
    /// </summary>
    public sealed class TextEncodingDetection
    {
        public TextEncodingDetection(
            Encoding encoding,
            string displayName,
            bool hasBom,
            int bomLength,
            bool isFallback)
        {
            if (encoding == null)
            {
                throw new ArgumentNullException(nameof(encoding));
            }

            Encoding = encoding;
            DisplayName = displayName ?? string.Empty;
            HasBom = hasBom;
            BomLength = bomLength < 0 ? 0 : bomLength;
            IsFallback = isFallback;
        }

        /// <summary>用于解码该文本的编码实例。</summary>
        public Encoding Encoding { get; }

        /// <summary>给状态栏显示的名字：UTF-8 / UTF-8 BOM / UTF-16 LE / UTF-16 BE / GBK / ANSI。</summary>
        public string DisplayName { get; }

        /// <summary>是否检测到字节序标记。</summary>
        public bool HasBom { get; }

        /// <summary>字节序标记的长度：UTF-8 为 3，UTF-16 为 2，无 BOM 为 0。</summary>
        public int BomLength { get; }

        /// <summary>true 表示没有可信证据，落到了系统 ANSI（中文为 936）。</summary>
        public bool IsFallback { get; }
    }

    /// <summary>
    /// 文本编码识别，顺序与 docs/03-development-plan.md 3.3 一致：
    /// 1. 检测 BOM；
    /// 2. 用最多 <see cref="SampleSize"/> 字节做严格 UTF-8 校验（纯 ASCII 也归到 UTF-8）；
    /// 3. 回退到系统 GBK（936），取不到再退到 <see cref="Encoding.Default"/>。
    /// 除 null 参数外不抛异常：坏字节只会得到回退结果。
    /// </summary>
    public static class TextEncodingDetector
    {
        /// <summary>参与 UTF-8 校验的最大样本字节数。</summary>
        public const int SampleSize = 8192;

        private const string Utf8Name = "UTF-8";
        private const string Utf8BomName = "UTF-8 BOM";
        private const string Utf16LeName = "UTF-16 LE";
        private const string Utf16BeName = "UTF-16 BE";
        private const string GbkName = "GBK";
        private const string AnsiName = "ANSI";

        /// <summary>识别整段字节的编码。</summary>
        public static TextEncodingDetection Detect(byte[] bytes)
        {
            if (bytes == null)
            {
                throw new ArgumentNullException(nameof(bytes));
            }

            return DetectSample(bytes, bytes.Length);
        }

        /// <summary>
        /// 只识别前 <paramref name="count"/> 个字节；count 会被夹到 [0, bytes.Length]，
        /// 因此可以直接传入“已读取字节数”而不必先修剪数组。
        /// </summary>
        public static TextEncodingDetection Detect(byte[] bytes, int count)
        {
            if (bytes == null)
            {
                throw new ArgumentNullException(nameof(bytes));
            }

            if (count < 0)
            {
                count = 0;
            }
            else if (count > bytes.Length)
            {
                count = bytes.Length;
            }

            return DetectSample(bytes, count);
        }

        private static TextEncodingDetection DetectSample(byte[] bytes, int count)
        {
            if (count >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            {
                return new TextEncodingDetection(new UTF8Encoding(true), Utf8BomName, true, 3, false);
            }

            // FF FE 00 00 是 UTF-32 LE 的 BOM；按约定仍当 UTF-16 LE 处理，不做额外分支。
            if (count >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            {
                return new TextEncodingDetection(new UnicodeEncoding(false, true), Utf16LeName, true, 2, false);
            }

            if (count >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            {
                return new TextEncodingDetection(new UnicodeEncoding(true, true), Utf16BeName, true, 2, false);
            }

            // 完全没有样本等于没有证据，直接回退，避免把空文件标成 UTF-8。
            if (count <= 0)
            {
                return CreateFallback();
            }

            int sampleLength = count < SampleSize ? count : SampleSize;
            if (IsValidUtf8(bytes, sampleLength))
            {
                return new TextEncodingDetection(new UTF8Encoding(false), Utf8Name, false, 0, false);
            }

            return CreateFallback();
        }

        /// <summary>
        /// 严格 UTF-8 校验：拒绝孤立续字节、非法首字节、过长编码、代理区与超出 U+10FFFF 的码点。
        /// 样本末尾被截断的多字节序列不算非法。
        /// </summary>
        private static bool IsValidUtf8(byte[] bytes, int count)
        {
            int index = 0;
            while (index < count)
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
                    if (index + offset >= count)
                    {
                        // 样本在序列中间被截断：已看到的字节都合法，允许不完整。
                        return true;
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

        private static TextEncodingDetection CreateFallback()
        {
            try
            {
                Encoding gbk = Encoding.GetEncoding(936);
                if (gbk != null)
                {
                    return new TextEncodingDetection(gbk, GbkName, false, 0, true);
                }
            }
            catch (ArgumentException)
            {
                // 目标机器没有 936 代码页（例如精简版系统），落到系统默认。
            }
            catch (NotSupportedException)
            {
                // 同上：编码提供程序缺失。
            }

            return new TextEncodingDetection(Encoding.Default, AnsiName, false, 0, true);
        }
    }
}
