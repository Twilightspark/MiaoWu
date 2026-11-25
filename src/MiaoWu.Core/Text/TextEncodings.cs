using System.Text;

namespace MiaoWu.Core.Text
{
    /// <summary>
    /// 运行期统一的文本编码：文件与命令输出都按 UTF-8。
    /// 读取用替换回退（命令输出里的坏字节变成 U+FFFD，不抛异常）；
    /// 写入用异常回退，避免孤立代理项被静默替换成问号后落盘。
    /// 文件读取的“严格 UTF-8 + BOM 剥离”在 <see cref="Utf8Text"/>。
    /// </summary>
    public static class TextEncodings
    {
        /// <summary>读取用 UTF-8：无 BOM，遇到非法字节用 U+FFFD 替换。</summary>
        public static readonly Encoding Utf8 = new UTF8Encoding(false, false);

        /// <summary>写入用 UTF-8：无 BOM，无法编码的字符直接抛 <see cref="EncoderFallbackException"/>。</summary>
        public static readonly Encoding Utf8Strict = new UTF8Encoding(false, true);
    }
}
