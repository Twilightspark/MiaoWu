using System;

namespace MiaoWu.Core.Input
{
    /// <summary>
    /// 一张随消息发送的图片附件：路径用于 chip，base64 用于模型请求。
    /// 附件只在当前草稿内存在；发送成功即释放。
    /// </summary>
    public sealed class ComposerImageAttachment
    {
        public string RelativePath { get; set; }

        public string FullPath { get; set; }

        /// <summary>显示名（文件名）。</summary>
        public string DisplayName { get; set; }

        public string MimeType { get; set; }

        public string Base64Data { get; set; }

        public long ByteCount { get; set; }

        /// <summary>SHA-256（小写十六进制），用于同一张图重复插入时去重。</summary>
        public string Sha256 { get; set; }

        public int EstimatedBytes
        {
            get { return Base64Data == null ? 0 : Base64Data.Length; }
        }
    }

    /// <summary>
    /// 图片附件的准入规则。MiaoWu 不做压缩管线：超过上限就明确拒绝，
    /// 避免悄悄改变用户给的图片内容。
    /// </summary>
    public static class ImageAttachmentPolicy
    {
        /// <summary>单张图片的字节上限（10 MB）。</summary>
        public const int MaxBytes = 10 * 1024 * 1024;

        /// <summary>一次输入最多携带的图片数。</summary>
        public const int MaxImagesPerMessage = 8;

        /// <summary>大小与格式是否可接受；不可接受时给出中文原因。</summary>
        public static bool Check(string mimeType, long byteCount, out string error)
        {
            error = null;
            if (byteCount <= 0)
            {
                error = "图片是空文件。";
                return false;
            }

            if (byteCount > MaxBytes)
            {
                error = "图片超过 10 MB，无法作为附件发送。";
                return false;
            }

            if (string.IsNullOrWhiteSpace(mimeType)
                || !mimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            {
                error = "不是可识别的图片格式。";
                return false;
            }

            return true;
        }
    }
}
