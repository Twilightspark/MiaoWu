using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using MiaoWu.Core.Errors;
using MiaoWu.Core.Models;

namespace MiaoWu.Infrastructure.Providers
{
    /// <summary>OpenAI-compatible /chat/completions 协议。</summary>
    public sealed class OpenAiCompletionsApi : IModelApi
    {
        private readonly HttpTransport _transport;
        private readonly AgentErrorClassifier _classifier;

        public OpenAiCompletionsApi(HttpTransport transport)
        {
            _transport = transport ?? throw new ArgumentNullException(nameof(transport));
            _classifier = new AgentErrorClassifier();
        }

        public string Id
        {
            get { return "openai-completions"; }
        }

        public ILlmStream Open(
            ModelDescriptor model,
            LlmRequest request,
            ProviderCredential credential,
            CancellationToken cancellationToken)
        {
            if (model == null)
            {
                throw new ArgumentNullException(nameof(model));
            }

            string baseUrl = credential == null || string.IsNullOrWhiteSpace(credential.BaseUrl)
                ? model.BaseUrl
                : credential.BaseUrl;

            string url = ProviderEndpoints.OpenAiChatCompletions(baseUrl);
            string body = LlmRequestJson.BuildOpenAiBody(model, request);
            string apiKey = credential == null ? null : credential.ApiKey;

            return new HttpLlmStream(
                () =>
                {
                    var message = new HttpRequestMessage(HttpMethod.Post, url);
                    if (!string.IsNullOrWhiteSpace(apiKey))
                    {
                        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                    }

                    message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
                    message.Content = new StringContent(body, Encoding.UTF8, "application/json");

                    if (credential != null && credential.Headers != null)
                    {
                        foreach (var header in credential.Headers)
                        {
                            message.Headers.TryAddWithoutValidation(header.Key, header.Value);
                        }
                    }

                    return message;
                },
                new OpenAiStreamMapper(),
                _transport,
                _classifier,
                model.ProviderId,
                model.ModelId,
                cancellationToken);
        }
    }

    /// <summary>Anthropic Messages 协议。</summary>
    public sealed class AnthropicMessagesApi : IModelApi
    {
        private const string AnthropicVersion = "2023-06-01";

        private readonly HttpTransport _transport;
        private readonly AgentErrorClassifier _classifier;

        public AnthropicMessagesApi(HttpTransport transport)
        {
            _transport = transport ?? throw new ArgumentNullException(nameof(transport));
            _classifier = new AgentErrorClassifier();
        }

        public string Id
        {
            get { return "anthropic-messages"; }
        }

        public ILlmStream Open(
            ModelDescriptor model,
            LlmRequest request,
            ProviderCredential credential,
            CancellationToken cancellationToken)
        {
            if (model == null)
            {
                throw new ArgumentNullException(nameof(model));
            }

            string baseUrl = credential == null || string.IsNullOrWhiteSpace(credential.BaseUrl)
                ? model.BaseUrl
                : credential.BaseUrl;

            string url = ProviderEndpoints.AnthropicMessages(baseUrl);
            string body = LlmRequestJson.BuildAnthropicBody(model, request);
            string apiKey = credential == null ? null : credential.ApiKey;

            return new HttpLlmStream(
                () =>
                {
                    var message = new HttpRequestMessage(HttpMethod.Post, url);
                    if (!string.IsNullOrWhiteSpace(apiKey))
                    {
                        message.Headers.TryAddWithoutValidation("x-api-key", apiKey);
                    }

                    message.Headers.TryAddWithoutValidation("anthropic-version", AnthropicVersion);
                    message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
                    message.Content = new StringContent(body, Encoding.UTF8, "application/json");

                    if (credential != null && credential.Headers != null)
                    {
                        foreach (var header in credential.Headers)
                        {
                            message.Headers.TryAddWithoutValidation(header.Key, header.Value);
                        }
                    }

                    return message;
                },
                new AnthropicStreamMapper(),
                _transport,
                _classifier,
                model.ProviderId,
                model.ModelId,
                cancellationToken);
        }
    }
}
