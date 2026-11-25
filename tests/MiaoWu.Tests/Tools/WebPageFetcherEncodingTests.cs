using System;
using System.Text;
using MiaoWu.Infrastructure.Tools;
using Xunit;

namespace MiaoWu.Tests.Tools
{
    public sealed class WebPageFetcherEncodingTests
    {
        [Fact]
        public void MissingCharsetDefaultsToUtf8()
        {
            byte[] bytes = new UTF8Encoding(false).GetBytes("中文网页");

            Assert.Equal("中文网页", HttpWebPageFetcher.DecodeBody(bytes, null));
            Assert.Equal("中文网页", HttpWebPageFetcher.DecodeBody(bytes, string.Empty));
        }

        [Fact]
        public void DeclaredGbkCharsetIsHonored()
        {
            byte[] bytes = Encoding.GetEncoding(936).GetBytes("中文网页");

            Assert.Equal("中文网页", HttpWebPageFetcher.DecodeBody(bytes, "gbk"));
        }

        [Fact]
        public void UnknownCharsetIsStructuredFailure()
        {
            WebFetchException error = Assert.Throws<WebFetchException>(
                () => HttpWebPageFetcher.DecodeBody(new byte[] { 0x61 }, "x-not-a-charset"));

            Assert.Equal("unsupported_charset", error.Code);
            Assert.Contains("x-not-a-charset", error.Message);
        }
    }
}
