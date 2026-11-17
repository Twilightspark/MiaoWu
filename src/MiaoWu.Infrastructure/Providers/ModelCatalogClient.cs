using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using MiaoWu.Infrastructure.Json;

namespace MiaoWu.Infrastructure.Providers
{
    /// <summary>GET /models 返回的一条模型记录。</summary>
    public sealed class ModelCatalogEntry
    {
        public ModelCatalogEntry(string id, string displayName)
        {
            Id = id;
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? id : displayName.Trim();
        }

        /// <summary>接口返回的模型 ID（保存到 providers.json 的 <c>id</c>）。</summary>
        public string Id { get; }

        /// <summary>展示名称；接口只给 ID 时与 <see cref="Id"/> 相同。</summary>
        public string DisplayName { get; }
    }

    /// <summary>
    /// 从 OpenAI-compatible / Anthropic 的模型列表接口读取可用模型。
    /// 只做 GET + JSON 解析，不写盘；设置页拿到列表后生成模型卡片。
    /// </summary>
    /// <remarks>
    /// 不同供应商的列表路径不一致：OpenAI 是 <c>{base}/v1/models</c>，
    /// DeepSeek 这类是 <c>{base}/models</c>，所以按 <see cref="ProviderEndpoints.ModelsListCandidates"/>
    /// 的顺序依次尝试，只有 404 才会换下一个候选。
    /// </remarks>
    public sealed class ModelCatalogClient
    {
        private const string AnthropicVersion = "2023-06-01";

        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _send;

        public ModelCatalogClient(HttpTransport transport)
            : this(transport == null ? (Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>)null
                : transport.SendAsync)
        {
        }

        /// <summary>注入发送委托，便于离线测试。</summary>
        public ModelCatalogClient(
            Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
        {
            _send = send ?? throw new ArgumentNullException(nameof(send));
        }

        /// <summary>读取模型列表；全部候选都 404 时抛出 <see cref="InvalidOperationException"/>。</summary>
        public async Task<IReadOnlyList<ModelCatalogEntry>> ListAsync(
            string baseUrl,
            string api,
            string apiKey,
            CancellationToken cancellationToken)
        {
            IReadOnlyList<string> candidates = ProviderEndpoints.ModelsListCandidates(baseUrl);
            ModelCatalogEndpointMissingException missing = null;

            foreach (string url in candidates)
            {
                try
                {
                    return await FetchAsync(url, api, apiKey, cancellationToken).ConfigureAwait(false);
                }
                catch (ModelCatalogEndpointMissingException exception)
                {
                    missing = exception;
                }
            }

            throw new InvalidOperationException(
                "接口没有提供模型列表（HTTP 404）：" + (missing == null ? baseUrl : missing.Url),
                missing);
        }

        /// <summary>解析 OpenAI / Anthropic 的 <c>{"data":[{"id":...}]}</c>；无数据时返回空列表。</summary>
        public static IReadOnlyList<ModelCatalogEntry> Parse(string json)
        {
            var entries = new List<ModelCatalogEntry>();
            if (string.IsNullOrWhiteSpace(json))
            {
                return entries;
            }

            JsonValue root = JsonParser.Parse(json);
            JsonValue data = root.Kind == JsonKind.Array ? root : root.Get("data");
            if (data == null || data.Kind != JsonKind.Array)
            {
                return entries;
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (JsonValue item in data.Items)
            {
                if (item == null || item.Kind != JsonKind.Object)
                {
                    continue;
                }

                JsonValue idNode = item.Get("id");
                string id = idNode == null ? null : idNode.AsString(null);
                if (string.IsNullOrWhiteSpace(id) || !seen.Add(id.Trim()))
                {
                    continue;
                }

                JsonValue nameNode = item.Get("display_name") ?? item.Get("name");
                string name = nameNode == null ? null : nameNode.AsString(null);
                entries.Add(new ModelCatalogEntry(id.Trim(), name));
            }

            return entries;
        }

        private async Task<IReadOnlyList<ModelCatalogEntry>> FetchAsync(
            string url,
            string api,
            string apiKey,
            CancellationToken cancellationToken)
        {
            using (var request = new HttpRequestMessage(HttpMethod.Get, url))
            {
                bool anthropic = string.Equals(api, "anthropic-messages", StringComparison.OrdinalIgnoreCase);
                if (!string.IsNullOrWhiteSpace(apiKey))
                {
                    if (anthropic)
                    {
                        request.Headers.TryAddWithoutValidation("x-api-key", apiKey.Trim());
                    }
                    else
                    {
                        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());
                    }
                }

                if (anthropic)
                {
                    request.Headers.TryAddWithoutValidation("anthropic-version", AnthropicVersion);
                }

                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

                using (HttpResponseMessage response = await _send(request, cancellationToken).ConfigureAwait(false))
                {
                    string body = response.Content == null
                        ? string.Empty
                        : await response.Content.ReadAsStringAsync().ConfigureAwait(false);

                    if (response.StatusCode == HttpStatusCode.NotFound)
                    {
                        throw new ModelCatalogEndpointMissingException(url);
                    }

                    if (!response.IsSuccessStatusCode)
                    {
                        throw new InvalidOperationException(DescribeStatus(response.StatusCode));
                    }

                    return Parse(body);
                }
            }
        }

        private static string DescribeStatus(HttpStatusCode status)
        {
            int code = (int)status;
            if (code == 401 || code == 403)
            {
                return "API Key 无效或没有权限（HTTP " + code + "）。";
            }

            if (code == 429)
            {
                return "请求过于频繁（HTTP 429）。";
            }

            if (code >= 500)
            {
                return "服务端错误（HTTP " + code + "）。";
            }

            return "模型列表请求失败（HTTP " + code + "）。";
        }

        /// <summary>候选地址 404；只用于切换下一个路径，不向用户展示。</summary>
        private sealed class ModelCatalogEndpointMissingException : Exception
        {
            public ModelCatalogEndpointMissingException(string url)
                : base("HTTP 404: " + url)
            {
                Url = url;
            }

            public string Url { get; }
        }
    }
}
