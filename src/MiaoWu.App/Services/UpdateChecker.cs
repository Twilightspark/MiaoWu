using System;
using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MiaoWu.Core;
using MiaoWu.Infrastructure.Providers;

namespace MiaoWu.App.Services
{
    public sealed class UpdateCheckResult
    {
        public bool Succeeded { get; set; }

        public bool HasUpdate { get; set; }

        public string LatestVersion { get; set; }

        public string Message { get; set; }
    }

    /// <summary>
    /// 检查更新：只读远端版本说明，不下载、不替换。
    /// 无服务器/离线时退化为“打开下载页”，且不阻塞启动。
    /// </summary>
    public sealed class UpdateChecker
    {
        private static readonly Regex VersionPattern = new Regex(
            @"v?(\d+\.\d+\.\d+)",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly Regex TagPattern = new Regex(
            @"""tag_name""\s*:\s*""v?([^""]+)""",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private readonly HttpTransport _transport;
        private readonly string _updateUrl;

        public UpdateChecker(HttpTransport transport, string updateUrl)
        {
            _transport = transport;
            _updateUrl = updateUrl;
        }

        public async Task<UpdateCheckResult> CheckAsync()
        {
            if (string.IsNullOrWhiteSpace(_updateUrl))
            {
                return new UpdateCheckResult
                {
                    Succeeded = false,
                    Message = "未配置更新地址。"
                };
            }

            string url = ToApiUrl(_updateUrl);
            try
            {
                using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12)))
                using (var request = new HttpRequestMessage(HttpMethod.Get, url))
                {
                    request.Headers.TryAddWithoutValidation("Accept", "application/json, text/html");
                    HttpResponseMessage response = await _transport.SendAsync(request, timeout.Token);

                    if (!response.IsSuccessStatusCode)
                    {
                        return new UpdateCheckResult
                        {
                            Succeeded = false,
                            Message = "无法检查更新（HTTP " + (int)response.StatusCode + "），可打开下载页。"
                        };
                    }

                    string body;
                    using (Stream stream = await response.Content.ReadAsStreamAsync())
                    using (var reader = new StreamReader(stream))
                    {
                        body = await reader.ReadToEndAsync();
                    }

                    string latest = ExtractVersion(body);
                    if (string.IsNullOrWhiteSpace(latest))
                    {
                        return new UpdateCheckResult
                        {
                            Succeeded = false,
                            Message = "未能解析版本信息，可打开下载页查看。"
                        };
                    }

                    bool newer = CompareVersions(latest, AppInfo.Version) > 0;
                    return new UpdateCheckResult
                    {
                        Succeeded = true,
                        HasUpdate = newer,
                        LatestVersion = latest,
                        Message = newer
                            ? "发现新版本 " + latest + "（当前 " + AppInfo.Version + "）。"
                            : "当前已是最新版本 " + AppInfo.Version + "。"
                    };
                }
            }
            catch (OperationCanceledException)
            {
                return new UpdateCheckResult
                {
                    Succeeded = false,
                    Message = "检查更新超时，可打开下载页。"
                };
            }
            catch (Exception exception)
            {
                return new UpdateCheckResult
                {
                    Succeeded = false,
                    Message = "检查更新失败：" + exception.Message + "。可打开下载页。"
                };
            }
        }

        private static string ToApiUrl(string url)
        {
            string value = (url ?? string.Empty).Trim();
            if (value.IndexOf("github.com", StringComparison.OrdinalIgnoreCase) >= 0
                && value.IndexOf("api.github.com", StringComparison.OrdinalIgnoreCase) < 0
                && value.IndexOf("/releases", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                // https://github.com/owner/repo/releases → https://api.github.com/repos/owner/repo/releases/latest
                int index = value.IndexOf("/releases", StringComparison.OrdinalIgnoreCase);
                string head = value.Substring(0, index);
                head = head.Replace("https://github.com/", "https://api.github.com/repos/");
                return head + "/releases/latest";
            }

            return value;
        }

        private static string ExtractVersion(string body)
        {
            if (string.IsNullOrEmpty(body))
            {
                return null;
            }

            Match tag = TagPattern.Match(body);
            if (tag.Success)
            {
                return tag.Groups[1].Value;
            }

            Match match = VersionPattern.Match(body);
            return match.Success ? match.Groups[1].Value : null;
        }

        private static int CompareVersions(string left, string right)
        {
            Version a;
            Version b;
            if (!Version.TryParse(left, out a) || !Version.TryParse(right, out b))
            {
                return string.Compare(left, right, StringComparison.OrdinalIgnoreCase);
            }

            return a.CompareTo(b);
        }
    }
}
