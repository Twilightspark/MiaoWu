using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MiaoWu.Core.Abstractions;
using MiaoWu.Core.Files;
using MiaoWu.Core.Text;

namespace MiaoWu.Infrastructure.Files
{
    /// <summary>
    /// 文本预览实现：
    /// 1. 先按前 8KB 判断二进制（UTF-16 BOM 除外）；
    /// 2. 编码顺序 BOM → UTF-8 严格校验 → 系统 ANSI（中文 936）；
    /// 3. 2MB / 5000 行先到为准，截断不抛异常；
    /// 4. 所有错误转成结构化字段。
    /// </summary>
    public sealed class FilePreviewService : IFilePreviewService
    {
        private readonly FilePreviewOptions _options;

        public FilePreviewService()
            : this(new FilePreviewOptions())
        {
        }

        public FilePreviewService(FilePreviewOptions options)
        {
            _options = options ?? new FilePreviewOptions();
        }

        public Task<FilePreview> LoadAsync(string filePath, CancellationToken cancellationToken)
        {
            return Task.Run(() => Load(filePath, cancellationToken), cancellationToken);
        }

        public FilePreview Load(string filePath, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                return FilePreview.Failed(filePath ?? string.Empty, "文件路径为空。");
            }

            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(filePath);
            }
            catch (Exception)
            {
                return FilePreview.Failed(filePath, "文件路径无效。");
            }

            FileInfo info;
            try
            {
                info = new FileInfo(fullPath);
                if (!info.Exists)
                {
                    return FilePreview.Failed(fullPath, "文件不存在或已被移动。");
                }
            }
            catch (Exception ex)
            {
                return FilePreview.Failed(fullPath, "无法访问文件：" + ex.Message);
            }

            try
            {
                cancellationToken.ThrowIfCancellationRequested();

                int maxRead = _options.MaxBytes + 16;
                byte[] buffer = new byte[Math.Min(maxRead, (int)Math.Min(info.Length, maxRead))];
                int read = ReadAtMost(fullPath, buffer, cancellationToken);

                if (read == 0)
                {
                    return CreateText(fullPath, info.Length, new byte[0], 0, false);
                }

                byte[] bytes = buffer;
                if (read != buffer.Length)
                {
                    bytes = new byte[read];
                    Array.Copy(buffer, bytes, read);
                }

                bool truncatedByBytes = info.Length > read;

                if (IsBinary(bytes, read))
                {
                    return FilePreview.Binary(fullPath, info.Length);
                }

                return CreateText(fullPath, info.Length, bytes, read, truncatedByBytes);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (UnauthorizedAccessException)
            {
                return FilePreview.Failed(fullPath, "没有权限读取此文件。");
            }
            catch (IOException ex)
            {
                return FilePreview.Failed(fullPath, "读取失败：" + ex.Message);
            }
            catch (Exception ex)
            {
                return FilePreview.Failed(fullPath, "读取失败：" + ex.Message);
            }
        }

        private static int ReadAtMost(string fullPath, byte[] buffer, CancellationToken cancellationToken)
        {
            using (var stream = new FileStream(
                fullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete))
            {
                int total = 0;
                while (total < buffer.Length)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int read = stream.Read(buffer, total, buffer.Length - total);
                    if (read <= 0)
                    {
                        break;
                    }

                    total += read;
                }

                return total;
            }
        }

        private static bool IsBinary(byte[] bytes, int count)
        {
            int probe = Math.Min(count, 8192);
            if (probe >= 2)
            {
                // UTF-16 文本天然包含 0x00，先看 BOM 再决定是否按二进制拒绝。
                if ((bytes[0] == 0xFF && bytes[1] == 0xFE)
                    || (bytes[0] == 0xFE && bytes[1] == 0xFF))
                {
                    return false;
                }
            }

            for (int index = 0; index < probe; index++)
            {
                if (bytes[index] == 0)
                {
                    return true;
                }
            }

            return false;
        }

        private FilePreview CreateText(
            string fullPath,
            long byteLength,
            byte[] bytes,
            int count,
            bool truncatedByBytes)
        {
            TextEncodingDetection detection = count == 0
                ? new TextEncodingDetection(new UTF8Encoding(false), "UTF-8", false, 0, false)
                : TextEncodingDetector.Detect(bytes, count);

            int contentOffset = Math.Min(detection.BomLength, count);
            string content = detection.Encoding.GetString(bytes, contentOffset, count - contentOffset);
            if (content.Length > 0 && content[0] == '\uFEFF')
            {
                content = content.Substring(1);
            }

            TextNewLineStyle newLine = NewLineDetector.Detect(content);
            int totalLines = TextLineCounter.Count(content);
            bool truncatedByLines = totalLines > _options.MaxLines;

            int loadedLines = totalLines;
            if (truncatedByLines)
            {
                content = CutAtLine(content, _options.MaxLines);
                loadedLines = TextLineCounter.Count(content);
            }

            string reason = null;
            if (truncatedByBytes)
            {
                reason = "已截断：超过 2 MB";
            }
            else if (truncatedByLines)
            {
                reason = "已截断：超过 5000 行";
            }

            return new FilePreview
            {
                FilePath = fullPath,
                Exists = true,
                IsBinary = false,
                EncodingName = detection.DisplayName,
                HasBom = detection.HasBom,
                NewLine = newLine,
                ByteLength = byteLength,
                LineCount = loadedLines,
                TotalLineCount = truncatedByBytes || truncatedByLines ? loadedLines : totalLines,
                IsTruncated = truncatedByBytes || truncatedByLines,
                TruncationReason = reason,
                Content = content,
                MetaText = DocumentMetaFormatter.Format(
                    detection.DisplayName,
                    newLine,
                    byteLength,
                    loadedLines,
                    reason == null ? null : "已截断")
            };
        }

        /// <summary>返回第 maxLines 行之后的位置，即只保留前 maxLines 行。</summary>
        private static string CutAtLine(string content, int maxLines)
        {
            int lines = 0;
            int index = 0;
            while (index < content.Length)
            {
                char current = content[index];
                if (current == '\r')
                {
                    index++;
                    if (index < content.Length && content[index] == '\n')
                    {
                        index++;
                    }

                    lines++;
                }
                else if (current == '\n')
                {
                    index++;
                    lines++;
                }
                else
                {
                    index++;
                    continue;
                }

                if (lines >= maxLines)
                {
                    return content.Substring(0, index);
                }
            }

            return content;
        }
    }
}
