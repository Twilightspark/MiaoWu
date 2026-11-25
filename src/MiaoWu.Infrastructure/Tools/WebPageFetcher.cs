using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MiaoWu.Core.Text;

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
                        byte[] bytes = await ReadCappedBytesAsync(stream, cancellationToken).ConfigureAwait(false);
                        string charset = response.Content.Headers.ContentType == null
                            ? null
                            : response.Content.Headers.ContentType.CharSet;
                        body = DecodeBody(bytes, charset);
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

        /// <summary>
        /// 按响应头声明的 charset 解码正文。与 DSH 的 web-fetch 对齐：未声明时按 UTF-8；
        /// 声明了就用该编码解码，未知编码返回结构化失败而不是静默乱码。
        /// </summary>
        public static string DecodeBody(byte[] bytes, string charset)
        {
            if (bytes == null)
            {
                throw new ArgumentNullException(nameof(bytes));
            }

            string label = (charset ?? string.Empty).Trim().Trim('"');
            Encoding encoding;
            if (label.Length == 0
                || string.Equals(label, "utf-8", StringComparison.OrdinalIgnoreCase)
                || string.Equals(label, "utf8", StringComparison.OrdinalIgnoreCase))
            {
                encoding = TextEncodings.Utf8;
            }
            else
            {
                try
                {
                    encoding = Encoding.GetEncoding(label);
                }
                catch (ArgumentException exception)
                {
                    throw new WebFetchException(
                        "unsupported_charset",
                        "网页声明了不支持的编码：" + label,
                        exception);
                }
                catch (NotSupportedException exception)
                {
                    throw new WebFetchException(
                        "unsupported_charset",
                        "网页声明了不支持的编码：" + label,
                        exception);
                }
            }

            return encoding.GetString(bytes);
        }

        private static async Task<byte[]> ReadCappedBytesAsync(Stream stream, CancellationToken cancellationToken)
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

                return memory.ToArray();
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
