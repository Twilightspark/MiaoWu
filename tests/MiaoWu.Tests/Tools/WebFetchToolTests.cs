using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using MiaoWu.Core.Agents.Tools;
using MiaoWu.Infrastructure.Tools;
using Xunit;

namespace MiaoWu.Tests.Tools
{
    public sealed class WebFetchToolTests
    {
        [Fact]
        public async Task ConvertsHtmlToPlainText()
        {
            var fetcher = new FakeFetcher
            {
                Content = new WebPageContent
                {
                    FinalUrl = "https://example.test/page",
                    ContentType = "text/html; charset=utf-8",
                    Body = "<html><head><style>.x{color:red}</style></head><body>"
                        + "<h1>标题</h1><p>第一段 &amp; 内容</p><script>alert(1)</script></body></html>"
                }
            };

            ToolResult result = await ExecuteAsync(new WebFetchTool(fetcher), "{\"url\":\"https://example.test/page\"}");

            Assert.True(result.IsSuccess);
            Assert.Contains("标题", result.Content);
            Assert.Contains("第一段 & 内容", result.Content);
            Assert.DoesNotContain("alert(1)", result.Content);
            Assert.DoesNotContain("color:red", result.Content);
            Assert.True(result.IsUntrusted);
        }

        [Fact]
        public async Task PassesPlainTextThrough()
        {
            var fetcher = new FakeFetcher
            {
                Content = new WebPageContent
                {
                    ContentType = "text/plain",
                    Body = "raw text"
                }
            };

            ToolResult result = await ExecuteAsync(new WebFetchTool(fetcher), "{\"url\":\"https://example.test/a.txt\"}");

            Assert.True(result.IsSuccess);
            Assert.Contains("raw text", result.Content);
        }

        [Fact]
        public async Task HttpErrorBecomesFailedResult()
        {
            var fetcher = new FakeFetcher
            {
                Error = new WebFetchException("http_status", "服务器返回 404 NotFound。", null)
            };

            ToolResult result = await ExecuteAsync(new WebFetchTool(fetcher), "{\"url\":\"https://example.test/missing\"}");

            Assert.Equal(ToolResultStatus.Failed, result.Status);
            Assert.Equal("http_status", result.ErrorCode);
        }

        [Fact]
        public async Task RejectsNonHttpScheme()
        {
            ToolResult result = await ExecuteAsync(
                new WebFetchTool(new FakeFetcher()),
                "{\"url\":\"file:///C:/Windows/win.ini\"}");

            Assert.Equal(ToolResultStatus.InvalidArguments, result.Status);
            Assert.Equal("invalid_url", result.ErrorCode);
        }

        [Fact]
        public async Task RejectsLocalAndPrivateHosts()
        {
            var tool = new WebFetchTool(new FakeFetcher());

            ToolResult loopback = await ExecuteAsync(tool, "{\"url\":\"http://127.0.0.1:8080/\"}");
            ToolResult lan = await ExecuteAsync(tool, "{\"url\":\"http://192.168.1.10/\"}");
            ToolResult localhost = await ExecuteAsync(tool, "{\"url\":\"http://localhost/\"}");

            Assert.Equal("blocked_host", loopback.ErrorCode);
            Assert.Equal("blocked_host", lan.ErrorCode);
            Assert.Equal("blocked_host", localhost.ErrorCode);
        }

        [Fact]
        public async Task TruncatesVeryLongText()
        {
            var fetcher = new FakeFetcher
            {
                Content = new WebPageContent
                {
                    ContentType = "text/plain",
                    Body = new string('a', 70000)
                }
            };

            ToolResult result = await ExecuteAsync(new WebFetchTool(fetcher), "{\"url\":\"https://example.test/big\"}");

            Assert.True(result.IsSuccess);
            Assert.True(result.Truncated);
            Assert.Contains("内容已截断", result.Content);
            Assert.True(result.Content.Length < 70000);
        }

        private static Task<ToolResult> ExecuteAsync(WebFetchTool tool, string argumentsJson)
        {
            var context = new ToolContext
            {
                SessionId = "s1",
                WorkspaceId = "w1",
                WorkspaceRoot = @"C:\Work",
                Now = DateTimeOffset.UtcNow
            };

            return tool.ExecuteAsync(
                new ToolCall { Id = "c1", Name = ToolNames.WebFetch, ArgumentsJson = argumentsJson },
                context,
                CancellationToken.None);
        }

        private sealed class FakeFetcher : IWebPageFetcher
        {
            public WebPageContent Content { get; set; }

            public WebFetchException Error { get; set; }

            public Task<WebPageContent> FetchAsync(Uri uri, CancellationToken cancellationToken)
            {
                if (Error != null)
                {
                    throw Error;
                }

                return Task.FromResult(Content ?? new WebPageContent { Body = string.Empty });
            }
        }
    }
}
