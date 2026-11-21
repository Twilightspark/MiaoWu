using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace MiaoWu.Infrastructure.Tools
{
    /// <summary>抓取结果：最终地址、内容类型与原始正文（未做 HTML 转文本）。</summary>
    public sealed class WebPageContent
    {
        public string FinalUrl { get; set; }

        public string ContentType { get; set; }

        public string Body { get; set; }
    }

    /// <summary>抓取一个网页。实现必须自己限制大小与超时，并在失败时抛 <see cref="WebFetchException"/>。</summary>
    public interface IWebPageFetcher
    {
        Task<WebPageContent> FetchAsync(Uri uri, CancellationToken cancellationToken);
    }

    /// <summary>抓取失败的分类原因，供工具转成结构化结果。</summary>
    public sealed class WebFetchException : Exception
    {
        public WebFetchException(string code, string message, Exception inner)
            : base(message, inner)
        {
            Code = code;
        }

        public string Code { get; }
    }

    /// <summary>
    /// 基于 HttpClient 的抓取器：只允许 http/https，最多 5 次重定向、2 MB 正文、20 秒超时。
    /// 不做 JS 渲染，取到的就是服务端返回的 HTML/文本。
    /// </summary>
    public sealed class HttpWebPageFetcher : IWebPageFetcher, IDisposable
    {
        private const int MaxBytes = 2 * 1024 * 1024;

        private readonly HttpClient _client;

        public HttpWebPageFetcher()
        {
            var handler = new HttpClientHandler
            {
                AllowAutoRedirect = true,
                MaxAutomaticRedirections = 5,
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
            };

            _client = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(20)
            };

            _client.DefaultRequestHeaders.TryAddWithoutValidation(
                "User-Agent",
                "MiaoWu/0.3 (+local desktop agent)");
        }

        public async Task<WebPageContent> FetchAsync(Uri uri, CancellationToken cancellationToken)
        {
            try
            {
                using (var request = new HttpRequestMessage(HttpMethod.Get, uri))
                using (HttpResponseMessage response = await _client
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false))
                {
                    if (!response.IsSuccessStatusCode)
                    {
                        throw new WebFetchException(
                            "http_status",
                            "服务器返回 " + (int)response.StatusCode + " " + response.ReasonPhrase + "。",
                            null);
                    }

                    string contentType = response.Content.Headers.ContentType == null
                        ? string.Empty
                        : response.Content.Headers.ContentType.MediaType ?? string.Empty;

                    string body;
                    using (Stream stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                    {
                        body = await ReadCappedAsync(stream, cancellationToken).ConfigureAwait(false);
                    }

                    return new WebPageContent
                    {
                        FinalUrl = response.RequestMessage == null || response.RequestMessage.RequestUri == null
                            ? uri.ToString()
                            : response.RequestMessage.RequestUri.ToString(),
                        ContentType = contentType,
                        Body = body
                    };
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (TaskCanceledException exception)
            {
                throw new WebFetchException("timeout", "抓取超时（20 秒）。", exception);
            }
            catch (HttpRequestException exception)
            {
                throw new WebFetchException("network_error", "网络请求失败：" + exception.Message, exception);
            }
        }

        public void Dispose()
        {
            _client.Dispose();
        }

        private static async Task<string> ReadCappedAsync(Stream stream, CancellationToken cancellationToken)
        {
            var buffer = new byte[81920];
            using (var memory = new MemoryStream())
            {
                while (true)
                {
                    int read = await stream.ReadAsync(buffer, 0, buffer.Length, cancellationToken)
                        .ConfigureAwait(false);
                    if (read <= 0)
                    {
                        break;
                    }

                    int remaining = MaxBytes - (int)memory.Length;
                    if (remaining <= 0)
                    {
                        break;
                    }

                    memory.Write(buffer, 0, Math.Min(read, remaining));
                }

                return Encoding.UTF8.GetString(memory.ToArray());
            }
        }
    }

    /// <summary>极简 HTML → 文本：去掉脚本/样式，块级标签转换行，解码实体并压缩空白。</summary>
    public static class HtmlText
    {
        private static readonly Regex ScriptStyle = new Regex(
            "<(script|style)[^>]*>.*?</\\1>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

        private static readonly Regex BlockTags = new Regex(
            "</?(p|div|br|li|tr|h[1-6]|section|article|header|footer|blockquote|pre)[^>]*>",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex AnyTag = new Regex("<[^>]+>", RegexOptions.Compiled);

        private static readonly Regex ManyNewlines = new Regex("\n{3,}", RegexOptions.Compiled);

        public static string Convert(string html)
        {
            if (string.IsNullOrEmpty(html))
            {
                return string.Empty;
            }

            string text = ScriptStyle.Replace(html, " ");
            text = BlockTags.Replace(text, "\n");
            text = AnyTag.Replace(text, string.Empty);
            text = WebUtility.HtmlDecode(text);
            text = text.Replace("\r\n", "\n").Replace('\r', '\n');
            text = ManyNewlines.Replace(text, "\n\n");
            return text.Trim();
        }
    }
}
