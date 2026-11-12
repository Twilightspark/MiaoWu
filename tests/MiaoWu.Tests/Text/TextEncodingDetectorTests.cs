using System;
using System.Text;
using MiaoWu.Core.Text;
using Xunit;

namespace MiaoWu.Tests.Text
{
    public sealed class TextEncodingDetectorTests
    {
        private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);
        private static readonly Encoding Gbk = Encoding.GetEncoding(936);

        [Fact]
        public void Utf8BomWinsOverUtf8Validation()
        {
            byte[] body = Utf8NoBom.GetBytes("中文测试");
            byte[] bytes = new byte[3 + body.Length];
            bytes[0] = 0xEF;
            bytes[1] = 0xBB;
            bytes[2] = 0xBF;
            Buffer.BlockCopy(body, 0, bytes, 3, body.Length);

            TextEncodingDetection detection = TextEncodingDetector.Detect(bytes);

            Assert.Equal("UTF-8 BOM", detection.DisplayName);
            Assert.True(detection.HasBom);
            Assert.Equal(3, detection.BomLength);
            Assert.False(detection.IsFallback);
        }

        [Fact]
        public void Utf16LittleEndianBomIsDetected()
        {
            Encoding utf16Le = new UnicodeEncoding(false, false);
            byte[] body = utf16Le.GetBytes("中文测试");

            TextEncodingDetection detection = TextEncodingDetector.Detect(WithBom(0xFF, 0xFE, body));

            Assert.Equal("UTF-16 LE", detection.DisplayName);
            Assert.True(detection.HasBom);
            Assert.Equal(2, detection.BomLength);
            Assert.False(detection.IsFallback);
        }

        [Fact]
        public void Utf16BigEndianBomIsDetected()
        {
            Encoding utf16Be = new UnicodeEncoding(true, false);
            byte[] body = utf16Be.GetBytes("中文测试");

            TextEncodingDetection detection = TextEncodingDetector.Detect(WithBom(0xFE, 0xFF, body));

            Assert.Equal("UTF-16 BE", detection.DisplayName);
            Assert.True(detection.HasBom);
            Assert.Equal(2, detection.BomLength);
            Assert.False(detection.IsFallback);
        }

        [Fact]
        public void Utf32LittleEndianBomStillFallsBackToUtf16LittleEndian()
        {
            TextEncodingDetection detection = TextEncodingDetector.Detect(new byte[] { 0xFF, 0xFE, 0x00, 0x00 });

            Assert.Equal("UTF-16 LE", detection.DisplayName);
            Assert.Equal(2, detection.BomLength);
        }

        [Fact]
        public void PureAsciiIsTreatedAsUtf8()
        {
            TextEncodingDetection detection = TextEncodingDetector.Detect(Utf8NoBom.GetBytes("plain ascii text"));

            Assert.Equal("UTF-8", detection.DisplayName);
            Assert.False(detection.HasBom);
            Assert.Equal(0, detection.BomLength);
            Assert.False(detection.IsFallback);
        }

        [Fact]
        public void ChineseUtf8WithoutBomIsDetected()
        {
            TextEncodingDetection detection = TextEncodingDetector.Detect(Utf8NoBom.GetBytes("中文测试，含标点。"));

            Assert.Equal("UTF-8", detection.DisplayName);
            Assert.False(detection.HasBom);
            Assert.False(detection.IsFallback);
            Assert.IsType<UTF8Encoding>(detection.Encoding);
            Assert.Empty(detection.Encoding.GetPreamble());
        }

        [Fact]
        public void GbkBytesFallBackToCodePage936()
        {
            byte[] bytes = Gbk.GetBytes("中文测试");

            TextEncodingDetection detection = TextEncodingDetector.Detect(bytes);

            Assert.Equal("GBK", detection.DisplayName);
            Assert.True(detection.IsFallback);
            Assert.False(detection.HasBom);
            Assert.Equal(0, detection.BomLength);
            Assert.Equal(936, detection.Encoding.CodePage);
        }

        [Fact]
        public void EmptyArrayFallsBackWithoutThrowing()
        {
            TextEncodingDetection detection = TextEncodingDetector.Detect(new byte[0]);

            Assert.True(detection.IsFallback);
            Assert.Equal("GBK", detection.DisplayName);
            Assert.False(detection.HasBom);
        }

        [Fact]
        public void InvalidUtf8SequencesFallBack()
        {
            AssertFallback(new byte[] { 0x41, 0xC3, 0x28 });
            AssertFallback(new byte[] { 0x80, 0x80, 0x80 });
            AssertFallback(new byte[] { 0xC0, 0xAF });
            AssertFallback(new byte[] { 0xE0, 0x80, 0x80 });
            AssertFallback(new byte[] { 0xED, 0xA0, 0x80 });
            AssertFallback(new byte[] { 0xF4, 0x90, 0x80, 0x80 });
            AssertFallback(new byte[] { 0xF5, 0x80, 0x80, 0x80 });
        }

        [Fact]
        public void TruncatedSequenceAtSampleEndIsStillUtf8()
        {
            // "中文" 的首字节被 count 截断，序列不完整但前缀合法。
            byte[] bytes = Utf8NoBom.GetBytes("中文测试");

            TextEncodingDetection detection = TextEncodingDetector.Detect(bytes, 2);

            Assert.Equal("UTF-8", detection.DisplayName);
            Assert.False(detection.HasBom);
        }

        [Fact]
        public void CountIsClampedToArrayLength()
        {
            byte[] bytes = new byte[] { 0xFF, 0xFE, 0x41, 0x00 };

            Assert.Equal("UTF-16 LE", TextEncodingDetector.Detect(bytes).DisplayName);
            Assert.Equal("UTF-16 LE", TextEncodingDetector.Detect(bytes, 9999).DisplayName);
            Assert.Equal("UTF-16 LE", TextEncodingDetector.Detect(bytes, bytes.Length).DisplayName);
        }

        [Fact]
        public void PartialCountCanHideTheBom()
        {
            byte[] body = Utf8NoBom.GetBytes("中文");
            byte[] bytes = new byte[3 + body.Length];
            bytes[0] = 0xEF;
            bytes[1] = 0xBB;
            bytes[2] = 0xBF;
            Buffer.BlockCopy(body, 0, bytes, 3, body.Length);

            TextEncodingDetection detection = TextEncodingDetector.Detect(bytes, 2);

            Assert.Equal("UTF-8", detection.DisplayName);
            Assert.False(detection.HasBom);
        }

        [Fact]
        public void NonPositiveCountFallsBack()
        {
            byte[] bytes = Utf8NoBom.GetBytes("中文测试");

            Assert.True(TextEncodingDetector.Detect(bytes, 0).IsFallback);
            Assert.True(TextEncodingDetector.Detect(bytes, -5).IsFallback);
        }

        [Fact]
        public void SampleBeyondLimitIsIgnored()
        {
            byte[] bytes = new byte[TextEncodingDetector.SampleSize + 256];
            for (int i = 0; i < bytes.Length; i++)
            {
                bytes[i] = (byte)'a';
            }

            // 坏字节落在样本之外：仍是 UTF-8。
            bytes[bytes.Length - 1] = 0xFF;
            Assert.Equal("UTF-8", TextEncodingDetector.Detect(bytes).DisplayName);

            // 同样的坏字节落在样本之内：回退。
            bytes[100] = 0xFF;
            Assert.True(TextEncodingDetector.Detect(bytes).IsFallback);
        }

        [Fact]
        public void GarbageBytesDoNotThrow()
        {
            byte[] bytes = new byte[512];
            for (int i = 0; i < bytes.Length; i++)
            {
                bytes[i] = (byte)(i * 37 % 256);
            }

            TextEncodingDetection detection = TextEncodingDetector.Detect(bytes);

            Assert.NotNull(detection);
            Assert.NotNull(detection.Encoding);
            Assert.NotEqual(string.Empty, detection.DisplayName);
        }

        [Fact]
        public void NullInputThrows()
        {
            Assert.Throws<ArgumentNullException>(new Action(() => TextEncodingDetector.Detect(null)));
            Assert.Throws<ArgumentNullException>(new Action(() => TextEncodingDetector.Detect(null, 4)));
        }

        [Fact]
        public void DetectionResultExposesDisplayNamesFromSpec()
        {
            Assert.Equal("UTF-8", TextEncodingDetector.Detect(new byte[] { 0x61 }).DisplayName);
            Assert.Equal("UTF-8 BOM", TextEncodingDetector.Detect(new byte[] { 0xEF, 0xBB, 0xBF }).DisplayName);
            Assert.Equal("UTF-16 LE", TextEncodingDetector.Detect(new byte[] { 0xFF, 0xFE, 0x41, 0x00 }).DisplayName);
            Assert.Equal("UTF-16 BE", TextEncodingDetector.Detect(new byte[] { 0xFE, 0xFF, 0x00, 0x41 }).DisplayName);
            Assert.Equal("GBK", TextEncodingDetector.Detect(new byte[] { 0xD6, 0xD0 }).DisplayName);
        }

        private static void AssertFallback(byte[] bytes)
        {
            TextEncodingDetection detection = TextEncodingDetector.Detect(bytes);

            Assert.True(detection.IsFallback);
            Assert.Equal("GBK", detection.DisplayName);
            Assert.False(detection.HasBom);
            Assert.Equal(0, detection.BomLength);
        }

        private static byte[] WithBom(byte first, byte second, byte[] body)
        {
            byte[] bytes = new byte[2 + body.Length];
            bytes[0] = first;
            bytes[1] = second;
            Buffer.BlockCopy(body, 0, bytes, 2, body.Length);
            return bytes;
        }
    }
}
