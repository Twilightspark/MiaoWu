using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using MiaoWu.Core.Abstractions;

namespace MiaoWu.Infrastructure.Providers
{
    public sealed class HttpTransportOptions
    {
        public HttpTransportOptions()
        {
            ProxyMode = ProxyModes.System;
            ConnectTimeoutSeconds = 20;
            IdleTimeoutSeconds = 60;
            UserAgent = "MiaoWu/0.1.0";
        }

        public string ProxyMode { get; set; }

        public string ProxyUrl { get; set; }

        public int ConnectTimeoutSeconds { get; set; }

        public int IdleTimeoutSeconds { get; set; }

        public string UserAgent { get; set; }
    }

    /// <summary>
    /// HttpClient 包装：TLS 1.2、代理三模式、连接超时。
    /// 流式响应不设置 HttpClient.Timeout（否则会把整个流算进超时）。
    /// </summary>
    public sealed class HttpTransport : IDisposable
    {
        private readonly HttpClient _client;
        private readonly HttpTransportOptions _options;
        private bool _disposed;

        public HttpTransport(HttpTransportOptions options)
        {
            _options = options ?? new HttpTransportOptions();
            ConfigureTls();

            var handler = new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
                UseCookies = false,
                AllowAutoRedirect = true
            };

            string mode = _options.ProxyMode ?? ProxyModes.System;
            if (string.Equals(mode, ProxyModes.Direct, StringComparison.OrdinalIgnoreCase))
            {
                handler.UseProxy = false;
            }
            else if (string.Equals(mode, ProxyModes.Manual, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(_options.ProxyUrl))
            {
                handler.UseProxy = true;
                handler.Proxy = new WebProxy(_options.ProxyUrl.Trim());
            }
            else
            {
                handler.UseProxy = true;
                handler.Proxy = WebRequest.DefaultWebProxy;
            }

            _client = new HttpClient(handler)
            {
                Timeout = Timeout.InfiniteTimeSpan,
                MaxResponseContentBufferSize = int.MaxValue
            };

            if (!string.IsNullOrWhiteSpace(_options.UserAgent))
            {
                _client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", _options.UserAgent);
            }
        }

        public HttpTransportOptions Options
        {
            get { return _options; }
        }

        public async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            using (var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                int seconds = _options.ConnectTimeoutSeconds <= 0 ? 20 : _options.ConnectTimeoutSeconds;
                connectCts.CancelAfter(TimeSpan.FromSeconds(seconds));

                try
                {
                    return await _client
                        .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, connectCts.Token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new TimeoutException("连接或等待响应头超时。");
                }
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _client.Dispose();
        }

        private static void ConfigureTls()
        {
            try
            {
                // Win7 默认只启用 TLS 1.0；显式加上 1.1 / 1.2（补丁缺失时连接会失败，
                // 由错误分类给出“TLS 连接失败”的提示）。
                ServicePointManager.SecurityProtocol |=
                    SecurityProtocolType.Tls11 | SecurityProtocolType.Tls12;
                ServicePointManager.DefaultConnectionLimit = 32;
            }
            catch (Exception)
            {
            }
        }
    }

    /// <summary>端点归一化。URL 校验在设置页保存时完成。</summary>
    public static class ProviderEndpoints
    {
        public static string NormalizeBaseUrl(string baseUrl)
        {
            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                throw new ArgumentException("Base URL 不能为空。", nameof(baseUrl));
            }

            string value = baseUrl.Trim().TrimEnd('/');
            return value;
        }

        public static string OpenAiChatCompletions(string baseUrl)
        {
            string value = NormalizeBaseUrl(baseUrl);
            const string suffix = "/chat/completions";
            if (value.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                value = value.Substring(0, value.Length - suffix.Length);
            }

            return value + suffix;
        }

        public static string AnthropicMessages(string baseUrl)
        {
            string value = NormalizeBaseUrl(baseUrl);
            if (value.EndsWith("/v1/messages", StringComparison.OrdinalIgnoreCase))
            {
                return value;
            }

            if (value.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
            {
                return value + "/messages";
            }

            return value + "/v1/messages";
        }
    }
}
