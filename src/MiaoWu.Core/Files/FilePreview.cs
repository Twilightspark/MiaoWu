using MiaoWu.Core.Text;

namespace MiaoWu.Core.Files
{
    /// <summary>预览读取的上限。先到为准：2 MB 或 5000 行。</summary>
    public sealed class FilePreviewOptions
    {
        public FilePreviewOptions()
        {
            MaxBytes = 2 * 1024 * 1024;
            MaxLines = 5000;
            BinaryProbeBytes = 8192;
        }

        public int MaxBytes { get; set; }

        public int MaxLines { get; set; }

        public int BinaryProbeBytes { get; set; }
    }

    /// <summary>
    /// 一次文件预览的结果。二进制、超限、不存在都用结构化字段表达，
    /// 由中栏显示栏位内提示，不抛异常。
    /// </summary>
    public sealed class FilePreview
    {
        public string FilePath { get; set; }

        public bool Exists { get; set; }

        public bool IsBinary { get; set; }

        public string ErrorMessage { get; set; }

        public string EncodingName { get; set; }

        public bool HasBom { get; set; }

        public TextNewLineStyle NewLine { get; set; }

        public long ByteLength { get; set; }

        /// <summary>实际载入的行数（截断后）。</summary>
        public int LineCount { get; set; }

        /// <summary>文件总行数；只有完整读取时才准确。</summary>
        public int TotalLineCount { get; set; }

        public bool IsTruncated { get; set; }

        public string TruncationReason { get; set; }

        public string Content { get; set; }

        /// <summary>操作栏右端的“编码 · 换行 · 大小 · 行数”。</summary>
        public string MetaText { get; set; }

        public bool HasError
        {
            get { return !Exists || !string.IsNullOrEmpty(ErrorMessage); }
        }

        public bool IsEmpty
        {
            get { return !HasError && !IsBinary && string.IsNullOrEmpty(Content); }
        }

        public static FilePreview Failed(string filePath, string message)
        {
            return new FilePreview
            {
                FilePath = filePath,
                Exists = false,
                ErrorMessage = message,
                Content = string.Empty,
                MetaText = string.Empty,
                NewLine = TextNewLineStyle.None
            };
        }

        public static FilePreview Binary(string filePath, long byteLength)
        {
            return new FilePreview
            {
                FilePath = filePath,
                Exists = true,
                IsBinary = true,
                ByteLength = byteLength,
                Content = string.Empty,
                NewLine = TextNewLineStyle.None,
                MetaText = MiaoWu.Core.Text.DocumentMetaFormatter.Format(
                    "二进制",
                    TextNewLineStyle.None,
                    byteLength,
                    0)
            };
        }

        /// <summary>
        /// 文件存在但不是严格 UTF-8：与 DSH 的 <c>workspace-file/not-text</c> 对齐，
        /// 不把坏字节显示成 U+FFFD，也不猜测 GBK / UTF-16。
        /// </summary>
        public static FilePreview NotText(string filePath, long byteLength)
        {
            return new FilePreview
            {
                FilePath = filePath,
                Exists = true,
                IsBinary = false,
                ByteLength = byteLength,
                ErrorMessage = "文件不是 UTF-8 文本，无法预览；请先转换为 UTF-8。",
                Content = string.Empty,
                NewLine = TextNewLineStyle.None,
                MetaText = MiaoWu.Core.Text.DocumentMetaFormatter.Format(
                    "非 UTF-8",
                    TextNewLineStyle.None,
                    byteLength,
                    0)
            };
        }
    }
}
