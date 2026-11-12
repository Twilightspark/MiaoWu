using System;
using System.IO;
using System.Text;
using MiaoWu.Core.Files;
using MiaoWu.Core.Text;
using MiaoWu.Infrastructure.Files;
using Xunit;

namespace MiaoWu.Tests.Files
{
    public sealed class FilePreviewServiceTests : IDisposable
    {
        private readonly string _directory;
        private readonly FilePreviewService _service = new FilePreviewService();

        public FilePreviewServiceTests()
        {
            _directory = Path.Combine(Path.GetTempPath(), "miaowu-preview-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_directory);
        }

        public void Dispose()
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, true);
            }
        }

        [Fact]
        public void Utf8BomCrlfIsDetectedAndDecoded()
        {
            string path = WriteBytes(
                "utf8.txt",
                new byte[] { 0xEF, 0xBB, 0xBF },
                Encoding.UTF8.GetBytes("中文第一行\r\n第二行"));

            FilePreview preview = _service.Load(path, default(System.Threading.CancellationToken));

            Assert.Equal("UTF-8 BOM", preview.EncodingName);
            Assert.True(preview.HasBom);
            Assert.Equal(TextNewLineStyle.CrLf, preview.NewLine);
            Assert.Equal(2, preview.LineCount);
            Assert.StartsWith("中文第一行", preview.Content);
            Assert.False(preview.IsTruncated);
            Assert.Contains("UTF-8 BOM", preview.MetaText);
            Assert.Contains("CRLF", preview.MetaText);
        }

        [Fact]
        public void GbkFallbackKeepsChineseText()
        {
            byte[] bytes = Encoding.GetEncoding(936).GetBytes("中文测试\r\n第二行");
            string path = WriteBytes("gbk.txt", new byte[0], bytes);

            FilePreview preview = _service.Load(path, default(System.Threading.CancellationToken));

            Assert.Equal("GBK", preview.EncodingName);
            Assert.Contains("中文测试", preview.Content);
            Assert.Equal(2, preview.LineCount);
        }

        [Fact]
        public void Utf16LeWithBomIsNotTreatedAsBinary()
        {
            string path = WriteBytes(
                "utf16.txt",
                new byte[] { 0xFF, 0xFE },
                Encoding.Unicode.GetBytes("宽字符一行\r\n第二行"));

            FilePreview preview = _service.Load(path, default(System.Threading.CancellationToken));

            Assert.False(preview.IsBinary);
            Assert.Equal("UTF-16 LE", preview.EncodingName);
            Assert.Contains("宽字符一行", preview.Content);
        }

        [Fact]
        public void NullBytesAreRejectedAsBinary()
        {
            string path = WriteBytes("binary.bin", new byte[0], new byte[] { 0x50, 0x4B, 0x00, 0x01, 0x02 });

            FilePreview preview = _service.Load(path, default(System.Threading.CancellationToken));

            Assert.True(preview.IsBinary);
            Assert.Empty(preview.Content);
            Assert.Equal(5, preview.ByteLength);
        }

        [Fact]
        public void EmptyFileUsesUtf8Placeholder()
        {
            string path = WriteBytes("empty.txt", new byte[0], new byte[0]);

            FilePreview preview = _service.Load(path, default(System.Threading.CancellationToken));

            Assert.False(preview.IsBinary);
            Assert.Equal("UTF-8", preview.EncodingName);
            Assert.Equal(0, preview.LineCount);
            Assert.Empty(preview.Content);
        }

        [Fact]
        public void LineLimitTruncatesAtFiveThousand()
        {
            var builder = new StringBuilder();
            for (int index = 0; index < 6000; index++)
            {
                builder.Append("line ").Append(index).Append("\r\n");
            }

            string path = WriteBytes(
                "many.txt",
                new byte[0],
                new UTF8Encoding(false).GetBytes(builder.ToString()));

            FilePreview preview = _service.Load(path, default(System.Threading.CancellationToken));

            Assert.True(preview.IsTruncated);
            Assert.Equal(5000, preview.LineCount);
            Assert.Contains("5000", preview.TruncationReason);
            Assert.Contains("已截断", preview.MetaText);
        }

        [Fact]
        public void ByteLimitTruncatesAtTwoMegabytes()
        {
            var builder = new StringBuilder();
            while (builder.Length < 3 * 1024 * 1024)
            {
                builder.Append("0123456789abcdef\r\n");
            }

            string path = WriteBytes(
                "big.txt",
                new byte[0],
                new UTF8Encoding(false).GetBytes(builder.ToString()));

            FilePreview preview = _service.Load(path, default(System.Threading.CancellationToken));

            Assert.True(preview.IsTruncated);
            Assert.Contains("2 MB", preview.TruncationReason);
            Assert.True(preview.ByteLength > 2 * 1024 * 1024);
        }

        [Fact]
        public void MissingFileReturnsStructuredError()
        {
            FilePreview preview = _service.Load(
                Path.Combine(_directory, "missing.txt"),
                default(System.Threading.CancellationToken));

            Assert.True(preview.HasError);
            Assert.False(preview.Exists);
            Assert.False(string.IsNullOrEmpty(preview.ErrorMessage));
        }

        [Fact]
        public void LongSingleLineIsNotSplitByLineLimit()
        {
            string text = new string('x', 10000);
            string path = WriteBytes("long.txt", new byte[0], new UTF8Encoding(false).GetBytes(text));

            FilePreview preview = _service.Load(path, default(System.Threading.CancellationToken));

            Assert.False(preview.IsTruncated);
            Assert.Equal(1, preview.LineCount);
            Assert.Equal(10000, preview.Content.Length);
        }

        private string WriteBytes(string name, byte[] prefix, byte[] content)
        {
            string path = Path.Combine(_directory, name);
            using (var stream = File.Create(path))
            {
                if (prefix != null && prefix.Length > 0)
                {
                    stream.Write(prefix, 0, prefix.Length);
                }

                if (content != null && content.Length > 0)
                {
                    stream.Write(content, 0, content.Length);
                }
            }

            return path;
        }
    }
}
