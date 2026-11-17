using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using MiaoWu.Infrastructure.Providers;
using Xunit;

namespace MiaoWu.Tests.Providers
{
    public sealed class ModelCatalogTests
    {
        [Fact]
        public void BuildsModelsCandidatesForVersionedAndBareBaseUrls()
        {
            Assert.Equal(
                new[] { "https://api.openai.com/v1/models" },
                ProviderEndpoints.ModelsListCandidates("https://api.openai.com/v1"));

            Assert.Equal(
                new[] { "http://127.0.0.1:1235/v1/models" },
                ProviderEndpoints.ModelsListCandidates("http://127.0.0.1:1235/v1"));

            Assert.Equal(
                new[] { "https://api.deepseek.com/v1/models", "https://api.deepseek.com/models" },
                ProviderEndpoints.ModelsListCandidates("https://api.deepseek.com"));

            Assert.Equal(
                new[] { "https://host/openai/v1/models" },
                ProviderEndpoints.ModelsListCandidates("https://host/openai/v1/chat/completions"));

            Assert.Equal(
                new[] { "https://host/v1beta/models" },
                ProviderEndpoints.ModelsListCandidates("https://host/v1beta"));

            Assert.Equal(
                new[] { "https://host/v1/models" },
                ProviderEndpoints.ModelsListCandidates("https://host/v1/models"));
        }

        [Fact]
        public void ParsesOpenAiModelList()
        {
            const string json = "{\"object\":\"list\",\"data\":[" +
                "{\"id\":\"minicpm5-1b\",\"object\":\"model\"}," +
                "{\"id\":\"text-embedding-bge-m3\",\"object\":\"model\"}," +
                "{\"id\":\"minicpm5-2b\",\"object\":\"model\"}," +
                "{\"id\":\"paddleocr-vl-1.6\",\"object\":\"model\"}]}";

            IReadOnlyList<ModelCatalogEntry> entries = ModelCatalogClient.Parse(json);

            Assert.Equal(4, entries.Count);
            Assert.Equal("minicpm5-1b", entries[0].Id);
            Assert.Equal("minicpm5-1b", entries[0].DisplayName);
            Assert.Equal("paddleocr-vl-1.6", entries[3].Id);
        }

        [Fact]
        public void ParsesAnthropicDisplayNameAndBareArray()
        {
            const string json = "{\"data\":[{\"id\":\"claude-sonnet-4\",\"display_name\":\"Claude Sonnet 4\"}],\"has_more\":false}";
            IReadOnlyList<ModelCatalogEntry> entries = ModelCatalogClient.Parse(json);
            Assert.Single(entries);
            Assert.Equal("Claude Sonnet 4", entries[0].DisplayName);

            IReadOnlyList<ModelCatalogEntry> bare = ModelCatalogClient.Parse("[{\"id\":\"m1\"},{\"id\":\"m2\"}]");
            Assert.Equal(2, bare.Count);
        }

        [Fact]
        public void SkipsEmptyAndDuplicateIds()
        {
            const string json = "{\"data\":[" +
                "{\"id\":\"m1\"}," +
                "{\"id\":\"\"}," +
                "{\"id\":\"M1\"}," +
                "{\"name\":\"no-id\"}," +
                "{\"id\":\"m2\"}]}";

            IReadOnlyList<ModelCatalogEntry> entries = ModelCatalogClient.Parse(json);

            Assert.Equal(2, entries.Count);
            Assert.Equal("m1", entries[0].Id);
            Assert.Equal("m2", entries[1].Id);
            Assert.Empty(ModelCatalogClient.Parse(string.Empty));
            Assert.Empty(ModelCatalogClient.Parse("{\"data\":[]}"));
        }

        [Fact]
        public async Task FallsBackToBareModelsWhenVersionedPathIsMissing()
        {
            var urls = new List<string>();
            var client = new ModelCatalogClient((request, cancellationToken) =>
            {
                urls.Add(request.RequestUri.ToString());
                if (urls.Count == 1)
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
                }

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"data\":[{\"id\":\"m1\"}]}")
                });
            });

            IReadOnlyList<ModelCatalogEntry> entries = await client.ListAsync(
                "https://deepseek.test",
                "openai-completions",
                "key-1",
                CancellationToken.None);

            Assert.Single(entries);
            Assert.Equal("m1", entries[0].Id);
            Assert.Equal(
                new[] { "https://deepseek.test/v1/models", "https://deepseek.test/models" },
                urls);
        }

        [Fact]
        public async Task UsesBearerForOpenAiAndApiKeyHeaderForAnthropic()
        {
            HttpRequestMessage captured = null;
            var client = new ModelCatalogClient((request, cancellationToken) =>
            {
                captured = request;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"data\":[]}")
                });
            });

            await client.ListAsync("https://host/v1", "openai-completions", "sk-test", CancellationToken.None);

            Assert.Equal("Bearer", captured.Headers.Authorization.Scheme);
            Assert.Equal("sk-test", captured.Headers.Authorization.Parameter);
            Assert.False(captured.Headers.Contains("x-api-key"));

            HttpRequestMessage anthropic = null;
            var anthropicClient = new ModelCatalogClient((request, cancellationToken) =>
            {
                anthropic = request;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"data\":[]}")
                });
            });

            await anthropicClient.ListAsync("https://api.anthropic.com", "anthropic-messages", "sk-ant", CancellationToken.None);

            Assert.Equal("sk-ant", string.Join(",", anthropic.Headers.GetValues("x-api-key")));
            Assert.Equal("2023-06-01", string.Join(",", anthropic.Headers.GetValues("anthropic-version")));
            Assert.Null(anthropic.Headers.Authorization);
        }

        [Fact]
        public async Task ReportsUnauthorizedKey()
        {
            var client = new ModelCatalogClient((request, cancellationToken) => Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.Unauthorized)));

            InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
                () => client.ListAsync("https://host/v1", "openai-completions", "bad", CancellationToken.None));

            Assert.Contains("401", error.Message);
            Assert.Contains("API Key", error.Message);
        }

        [Fact]
        public async Task ReportsMissingEndpointAfterAllCandidates()
        {
            int calls = 0;
            var client = new ModelCatalogClient((request, cancellationToken) =>
            {
                calls++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            });

            InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
                () => client.ListAsync("https://host", "openai-completions", "k", CancellationToken.None));

            Assert.Equal(2, calls);
            Assert.Contains("404", error.Message);
        }
    }
}
