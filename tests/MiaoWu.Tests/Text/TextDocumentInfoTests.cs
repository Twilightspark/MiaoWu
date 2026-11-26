using MiaoWu.Core.Text;
using Xunit;

namespace MiaoWu.Tests.Text
{
    public sealed class TextDocumentInfoTests
    {
        [Theory]
        [InlineData("a\r\nb", TextNewLineStyle.CrLf)]
        [InlineData("a\nb", TextNewLineStyle.Lf)]
        [InlineData("a\rb", TextNewLineStyle.Cr)]
        [InlineData("a\r\nb\nc", TextNewLineStyle.Mixed)]
        [InlineData("single line", TextNewLineStyle.None)]
        [InlineData("", TextNewLineStyle.None)]
        public void DetectReportsNewLineStyle(string text, TextNewLineStyle expected)
        {
            Assert.Equal(expected, NewLineDetector.Detect(text));
        }

        [Fact]
        public void DetectTreatsNullAsNone()
        {
            Assert.Equal(TextNewLineStyle.None, NewLineDetector.Detect(null));
        }

        [Theory]
        [InlineData(TextNewLineStyle.CrLf, "CRLF")]
        [InlineData(TextNewLineStyle.Lf, "LF")]
        [InlineData(TextNewLineStyle.Cr, "CR")]
        [InlineData(TextNewLineStyle.Mixed, "混合")]
        [InlineData(TextNewLineStyle.None, "—")]
        [InlineData((TextNewLineStyle)99, "—")]
        public void DisplayNameMatchesStatusBarWording(TextNewLineStyle style, string expected)
        {
            Assert.Equal(expected, NewLineDetector.ToDisplayName(style));
        }

        [Theory]
        [InlineData("", 0)]
        [InlineData("a", 1)]
        [InlineData("a\n", 1)]
        [InlineData("a\nb", 2)]
        [InlineData("a\r\nb\r\n", 2)]
        [InlineData("a\rb", 2)]
        [InlineData("a\n\nb", 3)]
        public void CountReportsLogicalLines(string text, int expected)
        {
            Assert.Equal(expected, TextLineCounter.Count(text));
        }

        [Fact]
        public void CountTreatsNullAsZero()
        {
            Assert.Equal(0, TextLineCounter.Count(null));
        }

        [Theory]
        [InlineData(0L, "0 B")]
        [InlineData(1023L, "1023 B")]
        [InlineData(1024L, "1.0 KB")]
        [InlineData(4200L, "4.1 KB")]
        [InlineData(1572864L, "1.5 MB")]
        [InlineData(1073741824L, "1.0 GB")]
        [InlineData(-1L, "0 B")]
        public void FormatUsesInvariantUnits(long bytes, string expected)
        {
            Assert.Equal(expected, FileSizeFormatter.Format(bytes));
        }

        [Fact]
        public void MetaStringJoinsPartsAndAppendsExtraSuffix()
        {
            Assert.Equal(
                "UTF-8 · LF · 4.1 KB · 128 行",
                DocumentMetaFormatter.Format("UTF-8", TextNewLineStyle.Lf, 4200, 128));
            Assert.Equal(
                "非 UTF-8 · CRLF · 512 B · 3 行 · 已截断",
                DocumentMetaFormatter.Format("非 UTF-8", TextNewLineStyle.CrLf, 512, 3, "已截断"));

            // 空白后缀与 null 都按「没有后缀」处理。
            Assert.Equal(
                "UTF-8 · LF · 512 B · 1 行",
                DocumentMetaFormatter.Format("UTF-8", TextNewLineStyle.Lf, 512, 1, string.Empty));
            Assert.Equal(
                "UTF-8 · LF · 512 B · 1 行",
                DocumentMetaFormatter.Format("UTF-8", TextNewLineStyle.Lf, 512, 1, "   "));
            Assert.Equal(
                "UTF-8 · LF · 512 B · 1 行",
                DocumentMetaFormatter.Format("UTF-8", TextNewLineStyle.Lf, 512, 1, null));
        }

        [Fact]
        public void MetaStringUsesPlaceholderForUnknownPartsAndClampsNegativeCounts()
        {
            Assert.Equal("— · — · 0 B · 0 行", DocumentMetaFormatter.Format(null, TextNewLineStyle.None, 0, 0));
            Assert.Equal("UTF-8 · — · 0 B · 0 行", DocumentMetaFormatter.Format("UTF-8", TextNewLineStyle.None, 0, 0));
            Assert.Equal("UTF-8 · LF · 0 B · 0 行", DocumentMetaFormatter.Format("UTF-8", TextNewLineStyle.Lf, -10, -2));
        }
    }
}
