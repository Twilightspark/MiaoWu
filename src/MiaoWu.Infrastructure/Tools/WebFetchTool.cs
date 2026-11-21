using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using MiaoWu.Core.Agents.Tools;

namespace MiaoWu.Infrastructure.Tools
{
    /// <summary>
    /// web_fetch：抓取一个公开 URL 并转成文本。只允许 http/https；拒绝本机与私网地址；
    /// 抓到的正文一律标记为不可信内容。
    /// </summary>
    public sealed class WebFetchTool : IAgentTool
    {
        private const int MaxUrlLength = 2000;
        private const int MaxTextChars = 60000;

        private readonly IWebPageFetcher _fetcher;

        public WebFetchTool(IWebPageFetcher fetcher)
        {
            _fetcher = fetcher ?? throw new ArgumentNullException(nameof(fetcher));
        }

        public string Name
        {
            get { return ToolNames.WebFetch; }
        }

        public string Description
        {
            get { return "抓取一个公开网页并返回文本（只支持 http/https，拒绝内网地址）。"; }
        }

        public ToolRisk Risk
        {
            get { return ToolRisk.Medium; }
        }

        public bool RequiresApproval
        {
            get { return false; }
        }

        public ToolApprovalPreview Describe(ToolCall call, ToolContext context)
        {
            return null;
        }

        public async Task<ToolResult> ExecuteAsync(
            ToolCall call,
            ToolContext context,
            CancellationToken cancellationToken)
        {
            Uri uri;
            try
            {
                ToolArguments arguments = ToolArguments.Parse(call == null ? null : call.ArgumentsJson);
                string url = arguments.GetRequiredString("url", MaxUrlLength);
                if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out uri)
                    || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                {
                    return ToolResult.Failure(
                        call,
                        ToolResultStatus.InvalidArguments,
                        "invalid_url",
                        "只接受 http/https 绝对地址。");
                }
            }
            catch (ToolArgumentException exception)
            {
                return ToolResult.Failure(
                    call,
                    ToolResultStatus.InvalidArguments,
                    exception.Code,
                    exception.Message);
            }

            if (IsLocalOrPrivate(uri))
            {
                return ToolResult.Failure(
                    call,
                    ToolResultStatus.InvalidArguments,
                    "blocked_host",
                    "不抓取本机或内网地址：" + uri.Host + "。");
            }

            WebPageContent page;
            try
            {
                page = await _fetcher.FetchAsync(uri, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return ToolResult.Failure(
                    call,
                    ToolResultStatus.Cancelled,
                    "cancelled",
                    "抓取已取消。");
            }
            catch (WebFetchException exception)
            {
                return ToolResult.Failure(
                    call,
                    ToolResultStatus.Failed,
                    exception.Code,
                    exception.Message);
            }

            if (page == null)
            {
                return ToolResult.Failure(
                    call,
                    ToolResultStatus.Failed,
                    "empty_response",
                    "服务器没有返回内容。");
            }

            bool html = page.ContentType != null
                && page.ContentType.IndexOf("html", StringComparison.OrdinalIgnoreCase) >= 0;
            string text = html ? HtmlText.Convert(page.Body) : (page.Body ?? string.Empty);
            text = text.Trim();

            bool truncated = text.Length > MaxTextChars;
            if (truncated)
            {
                text = text.Substring(0, MaxTextChars);
            }

            string body = "URL: " + (page.FinalUrl ?? uri.ToString())
                + "\nContent-Type: " + (string.IsNullOrWhiteSpace(page.ContentType) ? "(未知)" : page.ContentType)
                + (truncated ? "\n[内容已截断，只保留前 " + MaxTextChars + " 字]" : string.Empty)
                + "\n\n" + text;

            return ToolResult.Success(
                call,
                body,
                "抓取 " + uri.Host,
                true,
                truncated);
        }

        /// <summary>挡住本机、回环、私网与链路本地地址（字面量层面，不做 DNS 解析）。</summary>
        internal static bool IsLocalOrPrivate(Uri uri)
        {
            if (uri == null || string.IsNullOrWhiteSpace(uri.Host))
            {
                return true;
            }

            string host = uri.Host.Trim('[', ']');
            if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
                || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            IPAddress address;
            if (!IPAddress.TryParse(host, out address))
            {
                return false;
            }

            if (IPAddress.IsLoopback(address))
            {
                return true;
            }

            byte[] bytes = address.GetAddressBytes();
            if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            {
                return bytes[0] == 10
                    || bytes[0] == 0
                    || (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
                    || (bytes[0] == 192 && bytes[1] == 168)
                    || (bytes[0] == 169 && bytes[1] == 254);
            }

            // IPv6：fec0::/10（站点本地）、fc00::/7（唯一本地）与链路本地。
            return address.IsIPv6LinkLocal
                || address.IsIPv6SiteLocal
                || (bytes[0] & 0xFE) == 0xFC;
        }
    }
}
