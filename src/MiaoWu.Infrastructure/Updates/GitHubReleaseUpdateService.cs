using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using MiaoWu.Core.Updates;
using MiaoWu.Infrastructure.Json;
using MiaoWu.Infrastructure.Providers;

namespace MiaoWu.Infrastructure.Updates
{
    /// <summary>
    /// 基于 GitHub Releases 的更新检查：读仓库的「latest release」，解析 <c>tag_name</c> 与
    /// <c>html_url</c>，与当前版本比较。只读接口，不下载资源、不写入任何本地状态。
    /// </summary>
    public sealed class GitHubReleaseUpdateService : IUpdateService
    {
        private const string GitHubAccept = "application/vnd.github+json";
        private const int TimeoutSeconds = 15;

        private readonly HttpTransport _transport;
        private readonly string _releasesUrl;
        private readonly string _currentVersion;

        /// <summary>
        /// <paramref name="releasesUrl"/> 可以是仓库的 releases 页、具体的 release 页，或已经写好的
        /// api.github.com 地址；不是 GitHub 时按原样请求。当前版本通常传 <c>AppInfo.Version</c>。
        /// </summary>
        public GitHubReleaseUpdateService(
            HttpTransport transport,
            string releasesUrl,
            string currentVersion)
        {
            _transport = transport ?? throw new ArgumentNullException(nameof(transport));
            _releasesUrl = releasesUrl;
            _currentVersion = string.IsNullOrWhiteSpace(currentVersion) ? "0.0.0" : currentVersion;
        }

        public async Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken)
        {
            string apiUrl = ReleasePages.ToLatestReleaseApi(_releasesUrl);
            if (string.IsNullOrWhiteSpace(apiUrl))
            {
                return Failure("未配置更新地址。");
            }

            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(TimeoutSeconds));

                HttpResponseMessage response;
                try
                {
                    using (var request = new HttpRequestMessage(HttpMethod.Get, apiUrl))
                    {
                        request.Headers.TryAddWithoutValidation("Accept", GitHubAccept);
                        response = await _transport
                            .SendAsync(request, timeout.Token)
                            .ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    return Failure("检查更新超时，请检查网络或代理后重试。");
                }
                catch (TimeoutException)
                {
                    return Failure("检查更新超时，请检查网络或代理后重试。");
                }
                catch (Exception exception)
                {
                    return Failure("检查更新失败：" + exception.Message);
                }

                using (response)
                {
                    string body = await ReadBodyAsync(response).ConfigureAwait(false);

                    if (response.StatusCode == HttpStatusCode.NotFound)
                    {
                        return Failure("仓库还没有发布任何版本。");
                    }

                    if (response.StatusCode == (HttpStatusCode)403
                        || response.StatusCode == (HttpStatusCode)429)
                    {
                        return Failure("GitHub 接口暂时不可用（HTTP " + (int)response.StatusCode
                            + "），请稍后再试。");
                    }

                    if (!response.IsSuccessStatusCode)
                    {
                        return Failure("检查更新失败（HTTP " + (int)response.StatusCode + "）。");
                    }

                    return ParseLatestRelease(body, _currentVersion, _releasesUrl);
                }
            }
        }

        /// <summary>
        /// 解析 GitHub「latest release」的 JSON。<paramref name="fallbackReleaseUrl"/> 是
        /// 响应缺少 <c>html_url</c> 时给界面的兜底地址。解析失败也返回结果对象，不抛异常。
        /// </summary>
        public static UpdateCheckResult ParseLatestRelease(
            string json,
            string currentVersion,
            string fallbackReleaseUrl)
        {
            JsonValue root;
            try
            {
                root = JsonParser.Parse(json ?? string.Empty);
            }
            catch (Exception exception)
            {
                return Failure("没能解析发布信息：" + exception.Message);
            }

            if (root == null || root.Kind != JsonKind.Object)
            {
                return Failure("没能解析发布信息：返回内容不是对象。");
            }

            string tag = root.Get("tag_name") == null ? null : root.Get("tag_name").AsString();
            string latest = UpdateVersions.Normalize(tag);
            if (string.IsNullOrWhiteSpace(latest))
            {
                return Failure("发布信息缺少版本号。");
            }

            string releaseUrl = root.Get("html_url") == null ? null : root.Get("html_url").AsString();
            if (string.IsNullOrWhiteSpace(releaseUrl))
            {
                releaseUrl = fallbackReleaseUrl;
            }

            string current = string.IsNullOrWhiteSpace(currentVersion) ? "0.0.0" : currentVersion;
            bool newer = UpdateVersions.Compare(latest, current) > 0;

            return new UpdateCheckResult
            {
                Succeeded = true,
                HasUpdate = newer,
                CurrentVersion = current,
                LatestVersion = latest,
                ReleaseUrl = releaseUrl,
                Message = newer
                    ? "发现新版本 " + latest + "（当前 " + current + "）。"
                    : "当前已是最新版本 " + current + "。"
            };
        }

        private static async Task<string> ReadBodyAsync(HttpResponseMessage response)
        {
            using (Stream stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
            using (var reader = new StreamReader(stream))
            {
                return await reader.ReadToEndAsync().ConfigureAwait(false);
            }
        }

        private static UpdateCheckResult Failure(string message)
        {
            return new UpdateCheckResult
            {
                Succeeded = false,
                HasUpdate = false,
                Message = message
            };
        }
    }
}
