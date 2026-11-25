using System;
using System.Text;
using MiaoWu.Core.Text;
using Xunit;

namespace MiaoWu.Tests.Text
{
    public sealed class Utf8TextTests
    {
        private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);

        [Fact]
        public void Utf8BomIsStrippedAndReported()
        {
            byte[] body = Utf8NoBom.GetBytes("中文测试");
            byte[] bytes = WithBom(body);

            Utf8TextInfo info = Utf8Text.Read(bytes);

            Assert.True(info.IsValid);
            Assert.True(info.HasBom);
            Assert.Equal("中文测试", info.Text);
            Assert.True(info.Text.IndexOf('\uFEFF') < 0);
        }

        [Fact]
        public void PureAsciiIsValidUtf8()
        {
            Utf8TextInfo info = Utf8Text.Read(Utf8NoBom.GetBytes("plain ascii text"));

            Assert.True(info.IsValid);
            Assert.False(info.HasBom);
            Assert.Equal("plain ascii text", info.Text);
        }

        [Fact]
        public void ChineseUtf8WithoutBomIsValid()
        {
            Utf8TextInfo info = Utf8Text.Read(Utf8NoBom.GetBytes("中文测试，含标点。"));

            Assert.True(info.IsValid);
            Assert.False(info.HasBom);
            Assert.Equal("中文测试，含标点。", info.Text);
        }

        [Fact]
        public void GbkBytesAreRejected()
        {
            // GBK 的“中文”（D6 D0 CE C4）不是合法 UTF-8。
            Utf8TextInfo info = Utf8Text.Read(new byte[] { 0xD6, 0xD0, 0xCE, 0xC4 });

            Assert.False(info.IsValid);
            Assert.False(info.HasBom);
            Assert.Equal(string.Empty, info.Text);
        }

        [Fact]
        public void Utf16BomIsRejected()
        {
            Assert.False(Utf8Text.Read(new byte[] { 0xFF, 0xFE, 0x41, 0x00 }).IsValid);
            Assert.False(Utf8Text.Read(new byte[] { 0xFE, 0xFF, 0x00, 0x41 }).IsValid);
        }

        [Fact]
        public void EmptyArrayIsValidUtf8()
        {
            Utf8TextInfo info = Utf8Text.Read(new byte[0]);

            Assert.True(info.IsValid);
            Assert.False(info.HasBom);
            Assert.Equal(string.Empty, info.Text);
        }

        [Fact]
        public void InvalidUtf8SequencesAreRejected()
        {
            AssertInvalid(new byte[] { 0x41, 0xC3, 0x28 });
            AssertInvalid(new byte[] { 0x80, 0x80, 0x80 });
            AssertInvalid(new byte[] { 0xC0, 0xAF });
            AssertInvalid(new byte[] { 0xE0, 0x80, 0x80 });
            AssertInvalid(new byte[] { 0xED, 0xA0, 0x80 });
            AssertInvalid(new byte[] { 0xF4, 0x90, 0x80, 0x80 });
            AssertInvalid(new byte[] { 0xF5, 0x80, 0x80, 0x80 });
        }

        [Fact]
        public void TruncatedSequenceAtSampleEndIsAllowed()
        {
            // “中文” 的首字节被 count 截断，序列不完整但前缀合法；解码末尾会留一个 U+FFFD。
            byte[] bytes = Utf8NoBom.GetBytes("中文测试");

            Utf8TextInfo info = Utf8Text.Read(bytes, 2);

            Assert.True(info.IsValid);
            Assert.False(info.HasBom);
            Assert.EndsWith("\uFFFD", info.Text);
        }

        [Fact]
        public void TruncatedSequenceAtFileEndIsRejected()
        {
            // 完整读取时末尾不完整与 DSH 的 fatal TextDecoder 一样属于非法 UTF-8。
            byte[] bytes = new byte[] { 0xE4, 0xB8, 0xAD, 0xE6 };

            Assert.False(Utf8Text.Read(bytes).IsValid);
            Assert.False(Utf8Text.Read(bytes, bytes.Length).IsValid);
        }

        [Fact]
        public void CountIsClampedToArrayLength()
        {
            byte[] bytes = Utf8NoBom.GetBytes("中文");

            Assert.Equal("中文", Utf8Text.Read(bytes).Text);
            Assert.Equal("中文", Utf8Text.Read(bytes, 9999).Text);
            Assert.Equal("中文", Utf8Text.Read(bytes, bytes.Length).Text);
        }

        [Fact]
        public void PartialCountCanHideTheBom()
        {
            byte[] bytes = WithBom(Utf8NoBom.GetBytes("中文"));

            Utf8TextInfo info = Utf8Text.Read(bytes, 2);

            Assert.True(info.IsValid);
            Assert.False(info.HasBom);
        }

        [Fact]
        public void NonPositiveCountYieldsEmptyValidText()
        {
            byte[] bytes = Utf8NoBom.GetBytes("中文测试");

            Assert.True(Utf8Text.Read(bytes, 0).IsValid);
            Assert.Equal(string.Empty, Utf8Text.Read(bytes, 0).Text);
            Assert.True(Utf8Text.Read(bytes, -5).IsValid);
        }

        [Fact]
        public void InvalidByteLateInTheFileIsStillRejected()
        {
            byte[] bytes = new byte[9000];
            for (int i = 0; i < bytes.Length; i++)
            {
                bytes[i] = (byte)'a';
            }

            bytes[8500] = 0xFF;

            Assert.False(Utf8Text.Read(bytes).IsValid);
        }

        [Fact]
        public void GarbageBytesDoNotThrow()
        {
            byte[] bytes = new byte[512];
            for (int i = 0; i < bytes.Length; i++)
            {
                bytes[i] = (byte)(i * 37 % 256);
            }

            Utf8TextInfo info = Utf8Text.Read(bytes);

            Assert.False(info.IsValid);
            Assert.Equal(string.Empty, info.Text);
        }

        [Fact]
        public void NullInputThrows()
        {
            Assert.Throws<ArgumentNullException>(new Action(() => Utf8Text.Read(null)));
            Assert.Throws<ArgumentNullException>(new Action(() => Utf8Text.Read(null, 4)));
        }

        [Fact]
        public void DisplayNameIsUtf8()
        {
            Assert.Equal("UTF-8", Utf8Text.DisplayName);
        }

        private static void AssertInvalid(byte[] bytes)
        {
            Utf8TextInfo info = Utf8Text.Read(bytes);

            Assert.False(info.IsValid);
            Assert.False(info.HasBom);
            Assert.Equal(string.Empty, info.Text);
        }

        private static byte[] WithBom(byte[] body)
        {
            var bytes = new byte[3 + body.Length];
            bytes[0] = 0xEF;
            bytes[1] = 0xBB;
            bytes[2] = 0xBF;
            Buffer.BlockCopy(body, 0, bytes, 3, body.Length);
            return bytes;
        }
    }
}
