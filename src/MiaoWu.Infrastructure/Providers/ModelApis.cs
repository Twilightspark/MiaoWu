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

        /// <summary>
        /// 构造一次请求：Bearer 鉴权、SSE Accept、JSON 正文，以及供应商要求的附加头
        /// （OpenCode 网关的 <c>x-opencode-session</c>）。测试与 <see cref="Open"/> 共用这一份实现。
        /// </summary>
        public static HttpRequestMessage CreateRequest(
            ModelDescriptor model,
            LlmRequest request,
            ProviderCredential credential)
        {
            if (model == null)
            {
                throw new ArgumentNullException(nameof(model));
            }

            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            string baseUrl = ResolveBaseUrl(model, credential);
            string apiKey = credential == null ? null : credential.ApiKey;

            var message = new HttpRequestMessage(
                HttpMethod.Post,
                ProviderEndpoints.OpenAiChatCompletions(baseUrl));

            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            }

            message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
            message.Content = new StringContent(
                LlmRequestJson.BuildOpenAiBody(model, request),
                Encoding.UTF8,
                "application/json");

            ProviderRequestHeaders.Apply(
                message,
                baseUrl,
                request.SessionId,
                credential == null ? null : credential.Headers);

            return message;
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

            return new HttpLlmStream(
                () => CreateRequest(model, request, credential),
                new OpenAiStreamMapper(),
                _transport,
                _classifier,
                model.ProviderId,
                model.ModelId,
                cancellationToken);
        }

        internal static string ResolveBaseUrl(ModelDescriptor model, ProviderCredential credential)
        {
            return credential == null || string.IsNullOrWhiteSpace(credential.BaseUrl)
                ? model.BaseUrl
                : credential.BaseUrl;
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

        /// <summary>
        /// 构造一次请求：<c>x-api-key</c> 鉴权、<c>anthropic-version</c>、SSE Accept、JSON 正文，
        /// 以及供应商要求的附加头（OpenCode 网关的 <c>x-opencode-session</c>）。
        /// </summary>
        public static HttpRequestMessage CreateRequest(
            ModelDescriptor model,
            LlmRequest request,
            ProviderCredential credential)
        {
            if (model == null)
            {
                throw new ArgumentNullException(nameof(model));
            }

            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            string baseUrl = OpenAiCompletionsApi.ResolveBaseUrl(model, credential);
            string apiKey = credential == null ? null : credential.ApiKey;

            var message = new HttpRequestMessage(
                HttpMethod.Post,
                ProviderEndpoints.AnthropicMessages(baseUrl));

            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                message.Headers.TryAddWithoutValidation("x-api-key", apiKey);
            }

            message.Headers.TryAddWithoutValidation("anthropic-version", AnthropicVersion);
            message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
            message.Content = new StringContent(
                LlmRequestJson.BuildAnthropicBody(model, request),
                Encoding.UTF8,
                "application/json");

            ProviderRequestHeaders.Apply(
                message,
                baseUrl,
                request.SessionId,
                credential == null ? null : credential.Headers);

            return message;
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

            return new HttpLlmStream(
                () => CreateRequest(model, request, credential),
                new AnthropicStreamMapper(),
                _transport,
                _classifier,
                model.ProviderId,
                model.ModelId,
                cancellationToken);
        }
    }
}
