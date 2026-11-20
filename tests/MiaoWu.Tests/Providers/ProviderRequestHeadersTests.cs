using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using MiaoWu.Core.Models;
using MiaoWu.Infrastructure.Providers;
using Xunit;

namespace MiaoWu.Tests.Providers
{
    public sealed class ProviderRequestHeadersTests
    {
        private const string OpenCodeGoBaseUrl = "https://opencode.ai/inference/go/openai/v1";

        [Fact]
        public void DetectsOpenCodeHostsOnly()
        {
            Assert.True(ProviderRequestHeaders.IsOpenCodeEndpoint(OpenCodeGoBaseUrl));
            Assert.True(ProviderRequestHeaders.IsOpenCodeEndpoint("https://opencode.ai/inference/anthropic/v1"));
            Assert.True(ProviderRequestHeaders.IsOpenCodeEndpoint("https://api.opencode.ai/inference"));

            Assert.False(ProviderRequestHeaders.IsOpenCodeEndpoint("https://api.deepseek.com/v1"));
            Assert.False(ProviderRequestHeaders.IsOpenCodeEndpoint("https://opencode.ai.evil.test/inference"));
            Assert.False(ProviderRequestHeaders.IsOpenCodeEndpoint("not-a-url"));
            Assert.False(ProviderRequestHeaders.IsOpenCodeEndpoint(null));
        }

        [Fact]
        public void AddsStableNativeSessionHeaderForOpenCode()
        {
            HttpRequestMessage first = OpenAiCompletionsApi.CreateRequest(
                OpenCodeModel(),
                Request("s_20261119140000_ab12cd34"),
                Credential(OpenCodeGoBaseUrl));

            HttpRequestMessage sameSession = OpenAiCompletionsApi.CreateRequest(
                OpenCodeModel(),
                Request("s_20261119140000_ab12cd34"),
                Credential(OpenCodeGoBaseUrl));

            HttpRequestMessage otherSession = OpenAiCompletionsApi.CreateRequest(
                OpenCodeModel(),
                Request("s_20261119150000_ff00ee11"),
                Credential(OpenCodeGoBaseUrl));

            string value = SessionHeader(first);
            Assert.Matches("^ses_[0-9a-f]{12}[0-9A-Za-z]{14}$", value);
            Assert.Equal(value, SessionHeader(sameSession));
            Assert.NotEqual(value, SessionHeader(otherSession));
        }

        [Fact]
        public void GeneratesSessionHeaderWithoutConversation()
        {
            HttpRequestMessage first = OpenAiCompletionsApi.CreateRequest(
                OpenCodeModel(),
                Request(null),
                Credential(OpenCodeGoBaseUrl));

            HttpRequestMessage second = OpenAiCompletionsApi.CreateRequest(
                OpenCodeModel(),
                Request(null),
                Credential(OpenCodeGoBaseUrl));

            Assert.Matches("^ses_[0-9a-f]{12}[0-9A-Za-z]{14}$", SessionHeader(first));
            Assert.Equal(SessionHeader(first), SessionHeader(second));
        }

        [Fact]
        public void LeavesOtherProvidersUntouched()
        {
            HttpRequestMessage message = OpenAiCompletionsApi.CreateRequest(
                OpenCodeModel(),
                Request("s_20261119140000_ab12cd34"),
                Credential("https://api.deepseek.com/v1"));

            Assert.Null(SessionHeader(message));
            Assert.Equal("Bearer", message.Headers.Authorization.Scheme);
        }

        [Fact]
        public void ExplicitSessionHeaderWins()
        {
            ProviderCredential credential = Credential(OpenCodeGoBaseUrl);
            credential.Headers["X-OpenCode-Session"] = "ses_custom000000000000000000";

            HttpRequestMessage message = OpenAiCompletionsApi.CreateRequest(
                OpenCodeModel(),
                Request("s_20261119140000_ab12cd34"),
                credential);

            IEnumerable<string> values;
            Assert.True(message.Headers.TryGetValues(ProviderRequestHeaders.OpenCodeSessionHeader, out values));
            Assert.Equal(new[] { "ses_custom000000000000000000" }, values);
        }

        [Fact]
        public void AnthropicMessagesAlsoCarriesSessionHeader()
        {
            HttpRequestMessage message = AnthropicMessagesApi.CreateRequest(
                OpenCodeModel("anthropic-messages"),
                Request("s_20261119140000_ab12cd34"),
                Credential("https://opencode.ai/inference/go/anthropic/v1"));

            Assert.Equal("https://opencode.ai/inference/go/anthropic/v1/messages", message.RequestUri.ToString());
            Assert.Matches("^ses_[0-9a-f]{12}[0-9A-Za-z]{14}$", SessionHeader(message));
            Assert.Equal("2023-06-01", string.Join(",", message.Headers.GetValues("anthropic-version")));
        }

        [Fact]
        public async Task ModelCatalogRequestCarriesSessionHeaderForOpenCode()
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

            await client.ListAsync(
                OpenCodeGoBaseUrl,
                "openai-completions",
                "oc_sk_test",
                CancellationToken.None);

            Assert.Equal("https://opencode.ai/inference/go/v1/models", captured.RequestUri.ToString());
            Assert.Matches("^ses_[0-9a-f]{12}[0-9A-Za-z]{14}$", SessionHeader(captured));
        }

        private static ModelDescriptor OpenCodeModel(string api = "openai-completions")
        {
            return new ModelDescriptor
            {
                ProviderId = "opencode-go",
                ModelId = "glm-5.3",
                DisplayName = "GLM-5.3",
                Api = api,
                BaseUrl = OpenCodeGoBaseUrl,
                ContextWindow = 200000,
                MaxOutputTokens = 4096
            };
        }

        private static ProviderCredential Credential(string baseUrl)
        {
            return new ProviderCredential
            {
                ProviderId = "opencode-go",
                ApiKey = "oc_sk_test",
                BaseUrl = baseUrl,
                Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            };
        }

        private static LlmRequest Request(string sessionId)
        {
            return new LlmRequest
            {
                SessionId = sessionId,
                SystemPrompt = "ping",
                MaxOutputTokens = 1,
                Messages = new List<LlmMessage>
                {
                    new LlmMessage
                    {
                        Role = "user",
                        Content = new List<LlmContent>
                        {
                            new LlmContent { Type = "text", Text = "ping" }
                        }
                    }
                }
            };
        }

        private static string SessionHeader(HttpRequestMessage message)
        {
            IEnumerable<string> values;
            return message.Headers.TryGetValues(ProviderRequestHeaders.OpenCodeSessionHeader, out values)
                ? string.Join(",", values)
                : null;
        }
    }
}
