using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using MiaoWu.Core.Input;

namespace MiaoWu.Infrastructure.Workspaces
{
    /// <summary>
    /// 把磁盘上的图片读成可发送的附件：校验大小 / 扩展名，计算 SHA-256 去重，
    /// 再编码 base64。读取失败与超限都返回 false 与中文原因，不抛给 UI。
    /// </summary>
    public static class ImageAttachmentLoader
    {
        public static bool TryLoad(
            string fullPath,
            string relativePath,
            out ComposerImageAttachment attachment,
            out string error)
        {
            attachment = null;
            error = null;

            if (string.IsNullOrWhiteSpace(fullPath))
            {
                error = "图片路径为空。";
                return false;
            }

            FileInfo info;
            try
            {
                info = new FileInfo(fullPath);
                if (!info.Exists)
                {
                    error = "图片不存在或已被移动。";
                    return false;
                }
            }
            catch (Exception exception)
            {
                error = "无法读取图片：" + exception.Message;
                return false;
            }

            string mimeType = MentionFormatter.GuessImageMimeType(fullPath);
            if (!ImageAttachmentPolicy.Check(mimeType, info.Length, out error))
            {
                return false;
            }

            byte[] bytes;
            try
            {
                bytes = File.ReadAllBytes(fullPath);
            }
            catch (Exception exception)
            {
                error = "无法读取图片：" + exception.Message;
                return false;
            }

            if (!ImageAttachmentPolicy.Check(mimeType, bytes.Length, out error))
            {
                return false;
            }

            string hash;
            using (SHA256 sha = SHA256.Create())
            {
                hash = ToHex(sha.ComputeHash(bytes));
            }

            string normalized = MentionFormatter.NormalizeRelativePath(relativePath);
            attachment = new ComposerImageAttachment
            {
                RelativePath = normalized,
                FullPath = fullPath,
                DisplayName = Path.GetFileName(fullPath),
                MimeType = mimeType,
                Base64Data = Convert.ToBase64String(bytes),
                ByteCount = bytes.Length,
                Sha256 = hash
            };

            return true;
        }

        private static string ToHex(byte[] bytes)
        {
            var builder = new StringBuilder(bytes.Length * 2);
            foreach (byte value in bytes)
            {
                builder.Append(value.ToString("x2"));
            }

            return builder.ToString();
        }
    }
}
