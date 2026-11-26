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
        public void DecodesValidUtf8AndStripsTheBom()
        {
            Utf8TextInfo ascii = Utf8Text.Read(Utf8NoBom.GetBytes("plain ascii text"));
            Assert.True(ascii.IsValid);
            Assert.False(ascii.HasBom);
            Assert.Equal("plain ascii text", ascii.Text);

            Utf8TextInfo chinese = Utf8Text.Read(Utf8NoBom.GetBytes("中文测试，含标点。"));
            Assert.True(chinese.IsValid);
            Assert.False(chinese.HasBom);
            Assert.Equal("中文测试，含标点。", chinese.Text);

            Utf8TextInfo empty = Utf8Text.Read(new byte[0]);
            Assert.True(empty.IsValid);
            Assert.Equal(string.Empty, empty.Text);

            Utf8TextInfo bom = Utf8Text.Read(WithBom(Utf8NoBom.GetBytes("中文测试")));
            Assert.True(bom.IsValid);
            Assert.True(bom.HasBom);
            Assert.Equal("中文测试", bom.Text);
            Assert.True(bom.Text.IndexOf('\uFEFF') < 0);

            // 只读前 2 个字节时 BOM 不完整，应当被当成普通内容而不是 BOM。
            Utf8TextInfo partial = Utf8Text.Read(WithBom(Utf8NoBom.GetBytes("中文")), 2);
            Assert.True(partial.IsValid);
            Assert.False(partial.HasBom);
        }

        [Fact]
        public void RejectsLegacyEncodingsAndMalformedSequences()
        {
            // GBK 的“中文”（D6 D0 CE C4）不是合法 UTF-8。
            AssertInvalid(new byte[] { 0xD6, 0xD0, 0xCE, 0xC4 });

            // UTF-16 BOM 不是 UTF-8。
            AssertInvalid(new byte[] { 0xFF, 0xFE, 0x41, 0x00 });
            AssertInvalid(new byte[] { 0xFE, 0xFF, 0x00, 0x41 });

            AssertInvalid(new byte[] { 0x41, 0xC3, 0x28 });
            AssertInvalid(new byte[] { 0x80, 0x80, 0x80 });
            AssertInvalid(new byte[] { 0xC0, 0xAF });
            AssertInvalid(new byte[] { 0xE0, 0x80, 0x80 });
            AssertInvalid(new byte[] { 0xED, 0xA0, 0x80 });
            AssertInvalid(new byte[] { 0xF4, 0x90, 0x80, 0x80 });
            AssertInvalid(new byte[] { 0xF5, 0x80, 0x80, 0x80 });

            // 任意垃圾字节不抛异常；非法字节出现在文件后段也要被查出。
            byte[] garbage = new byte[512];
            for (int i = 0; i < garbage.Length; i++)
            {
                garbage[i] = (byte)(i * 37 % 256);
            }

            AssertInvalid(garbage);

            byte[] lateInvalid = new byte[9000];
            for (int i = 0; i < lateInvalid.Length; i++)
            {
                lateInvalid[i] = (byte)'a';
            }

            lateInvalid[8500] = 0xFF;
            AssertInvalid(lateInvalid);
        }

        [Fact]
        public void TruncatedTailIsAllowedButBrokenTailIsRejected()
        {
            // “中文” 的首字节被 count 截断，序列不完整但前缀合法；解码末尾会留一个 U+FFFD。
            byte[] bytes = Utf8NoBom.GetBytes("中文测试");
            Utf8TextInfo truncatedByCount = Utf8Text.Read(bytes, 2);
            Assert.True(truncatedByCount.IsValid);
            Assert.EndsWith("\uFFFD", truncatedByCount.Text);

            // 完整读取时末尾不完整与 DSH 的 fatal TextDecoder 一样属于非法 UTF-8。
            byte[] brokenTail = new byte[] { 0xE4, 0xB8, 0xAD, 0xE6 };
            Assert.False(Utf8Text.Read(brokenTail).IsValid);
            Assert.False(Utf8Text.Read(brokenTail, brokenTail.Length).IsValid);
        }

        [Fact]
        public void CountIsClampedAndNonPositiveYieldsEmptyText()
        {
            byte[] bytes = Utf8NoBom.GetBytes("中文");

            Assert.Equal("中文", Utf8Text.Read(bytes).Text);
            Assert.Equal("中文", Utf8Text.Read(bytes, 9999).Text);
            Assert.Equal("中文", Utf8Text.Read(bytes, bytes.Length).Text);

            Assert.True(Utf8Text.Read(bytes, 0).IsValid);
            Assert.Equal(string.Empty, Utf8Text.Read(bytes, 0).Text);
            Assert.True(Utf8Text.Read(bytes, -5).IsValid);
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
