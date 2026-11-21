using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MiaoWu.Core.Agents.Tools;
using MiaoWu.Infrastructure.Tools;
using Xunit;

namespace MiaoWu.Tests.Tools
{
    /// <summary>web_search：结果解析、参数校验、后端失败与“不可信”标记。</summary>
    public sealed class WebSearchToolTests
    {
        private const string Fixture =
            "<div class=\"result results_links\"><h2 class=\"result__title\">"
            + "<a rel=\"nofollow\" class=\"result__a\" href=\"//duckduckgo.com/l/?uddg=https%3A%2F%2Fexample.com%2Fa%3Fx%3D1&amp;rut=abc\">Result <b>One</b></a>"
            + "</h2><a class=\"result__snippet\" href=\"//x\">Snippet &amp; one</a></div>"
            + "<div class=\"result\"><h2 class=\"result__title\">"
            + "<a rel=\"nofollow\" class=\"result__a\" href=\"https://example.org/two\">Result Two</a>"
            + "</h2><a class=\"result__snippet\">Second snippet</a></div>";

        [Fact]
        public void ParsesTitlesLinksAndSnippets()
        {
            IReadOnlyList<WebSearchResult> results = DuckDuckGoSearchProvider.Parse(Fixture, 5);

            Assert.Equal(2, results.Count);
            Assert.Equal("Result One", results[0].Title);
            Assert.Equal("https://example.com/a?x=1", results[0].Url);
            Assert.Equal("Snippet & one", results[0].Snippet);
            Assert.Equal("Result Two", results[1].Title);
            Assert.Equal("https://example.org/two", results[1].Url);
            Assert.Equal("Second snippet", results[1].Snippet);
        }

        [Fact]
        public void RespectsMaxResultsAndEmptyHtml()
        {
            Assert.Single(DuckDuckGoSearchProvider.Parse(Fixture, 1));
            Assert.Empty(DuckDuckGoSearchProvider.Parse(null, 5));
            Assert.Empty(DuckDuckGoSearchProvider.Parse("<html></html>", 5));
        }

        [Fact]
        public async Task FormatsResultsWithUntrustedMarker()
        {
            var provider = new FakeSearchProvider(new[]
            {
                new WebSearchResult { Title = "标题一", Url = "https://example.com/1", Snippet = "摘要一" }
            });

            ToolResult result = await new WebSearchTool(provider).ExecuteAsync(
                Call("{\"query\":\"miao wu\"}"),
                new ToolContext(),
                CancellationToken.None);

            Assert.Equal(ToolResultStatus.Succeeded, result.Status);
            Assert.Contains("搜索：miao wu", result.Content);
            Assert.Contains("1. 标题一", result.Content);
            Assert.Contains("https://example.com/1", result.Content);
            Assert.Contains("不可信数据", result.Content);
            Assert.Equal("miao wu", provider.LastQuery);
            Assert.Equal(5, provider.LastMaxResults);
        }

        [Fact]
        public async Task RejectsBlankQuery()
        {
            var provider = new FakeSearchProvider(new WebSearchResult[0]);
            var tool = new WebSearchTool(provider);

            ToolResult missing = await tool.ExecuteAsync(Call("{}"), new ToolContext(), CancellationToken.None);
            Assert.Equal(ToolResultStatus.InvalidArguments, missing.Status);

            ToolResult blank = await tool.ExecuteAsync(
                Call("{\"query\":\"   \"}"),
                new ToolContext(),
                CancellationToken.None);
            Assert.Equal(ToolResultStatus.InvalidArguments, blank.Status);
            Assert.Equal(0, provider.Calls);
        }

        [Fact]
        public async Task ReportsProviderFailure()
        {
            var provider = new FailingSearchProvider();
            ToolResult result = await new WebSearchTool(provider).ExecuteAsync(
                Call("{\"query\":\"x\"}"),
                new ToolContext(),
                CancellationToken.None);

            Assert.Equal(ToolResultStatus.Failed, result.Status);
            Assert.Equal("http_error", result.ErrorCode);
        }

        private static ToolCall Call(string argumentsJson)
        {
            return new ToolCall
            {
                Id = "call_web_search",
                Name = ToolNames.WebSearch,
                ArgumentsJson = argumentsJson
            };
        }

        private sealed class FakeSearchProvider : IWebSearchProvider
        {
            private readonly IReadOnlyList<WebSearchResult> _results;

            public FakeSearchProvider(IReadOnlyList<WebSearchResult> results)
            {
                _results = results;
            }

            public int Calls { get; private set; }

            public string LastQuery { get; private set; }

            public int LastMaxResults { get; private set; }

            public Task<IReadOnlyList<WebSearchResult>> SearchAsync(
                string query,
                int maxResults,
                CancellationToken cancellationToken)
            {
                Calls++;
                LastQuery = query;
                LastMaxResults = maxResults;
                return Task.FromResult(_results);
            }
        }

        private sealed class FailingSearchProvider : IWebSearchProvider
        {
            public Task<IReadOnlyList<WebSearchResult>> SearchAsync(
                string query,
                int maxResults,
                CancellationToken cancellationToken)
            {
                throw new WebFetchException("http_error", "搜索服务不可用。", null);
            }
        }
    }
}
