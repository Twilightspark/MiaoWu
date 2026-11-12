using MiaoWu.Core.Text;
using Xunit;

namespace MiaoWu.Tests.Text
{
    public sealed class TextDocumentInfoTests
    {
        [Theory]
        [InlineData("a\r\nb", TextNewLineStyle.CrLf)]
        [InlineData("a\r\nb\r\nc", TextNewLineStyle.CrLf)]
        [InlineData("a\nb", TextNewLineStyle.Lf)]
        [InlineData("a\rb", TextNewLineStyle.Cr)]
        [InlineData("a\r\nb\nc", TextNewLineStyle.Mixed)]
        [InlineData("a\rb\nc", TextNewLineStyle.Mixed)]
        [InlineData("a\r\nb\rc\nd", TextNewLineStyle.Mixed)]
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
        public void DisplayNameMatchesStatusBarWording(TextNewLineStyle style, string expected)
        {
            Assert.Equal(expected, NewLineDetector.ToDisplayName(style));
        }

        [Fact]
        public void DisplayNameFallsBackToDashForUnknownStyle()
        {
            Assert.Equal("—", NewLineDetector.ToDisplayName((TextNewLineStyle)99));
        }

        [Theory]
        [InlineData("", 0)]
        [InlineData("a", 1)]
        [InlineData("a\n", 1)]
        [InlineData("a\nb", 2)]
        [InlineData("a\nb\n", 2)]
        [InlineData("a\r\nb\r\n", 2)]
        [InlineData("a\rb", 2)]
        [InlineData("\n", 1)]
        [InlineData("\r\n", 1)]
        [InlineData("a\n\nb", 3)]
        [InlineData("a\nb\nc", 3)]
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
        [InlineData(512L, "512 B")]
        [InlineData(1023L, "1023 B")]
        [InlineData(1024L, "1.0 KB")]
        [InlineData(4200L, "4.1 KB")]
        [InlineData(1572864L, "1.5 MB")]
        [InlineData(1073741824L, "1.0 GB")]
        [InlineData(5368709120L, "5.0 GB")]
        [InlineData(-1L, "0 B")]
        [InlineData(-1048576L, "0 B")]
        public void FormatUsesInvariantUnits(long bytes, string expected)
        {
            Assert.Equal(expected, FileSizeFormatter.Format(bytes));
        }

        [Fact]
        public void MetaStringJoinsWithMiddleDot()
        {
            string meta = DocumentMetaFormatter.Format("UTF-8", TextNewLineStyle.Lf, 4200, 128);

            Assert.Equal("UTF-8 · LF · 4.1 KB · 128 行", meta);
            Assert.Contains("\u00B7", meta);
        }

        [Fact]
        public void MetaStringAppendsExtraSuffix()
        {
            string meta = DocumentMetaFormatter.Format("GBK", TextNewLineStyle.CrLf, 512, 3, "已截断");

            Assert.Equal("GBK · CRLF · 512 B · 3 行 · 已截断", meta);
        }

        [Fact]
        public void MetaStringIgnoresBlankExtra()
        {
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
        public void MetaStringUsesPlaceholderForUnknownParts()
        {
            Assert.Equal("— · — · 0 B · 0 行", DocumentMetaFormatter.Format(null, TextNewLineStyle.None, 0, 0));
            Assert.Equal("UTF-8 · — · 0 B · 0 行", DocumentMetaFormatter.Format("UTF-8", TextNewLineStyle.None, 0, 0));
        }

        [Fact]
        public void MetaStringClampsNegativeNumbers()
        {
            Assert.Equal("UTF-8 · LF · 0 B · 0 行", DocumentMetaFormatter.Format("UTF-8", TextNewLineStyle.Lf, -10, -2));
        }
    }
}
