using System;
using System.Threading;
using System.Threading.Tasks;

namespace MiaoWu.Core.Updates
{
    /// <summary>
    /// 一次「检查更新」的结果。失败时 <see cref="Succeeded"/> 为 false，
    /// <see cref="Message"/> 给一句可读的原因；成功时 <see cref="HasUpdate"/> 表示远端是否比当前新。
    /// </summary>
    public sealed class UpdateCheckResult
    {
        /// <summary>请求是否完成并解析出版本号；离线、限流、无发布都算失败。</summary>
        public bool Succeeded { get; set; }

        /// <summary>远端最新发布是否比当前版本新。</summary>
        public bool HasUpdate { get; set; }

        /// <summary>当前运行版本，格式为 Major.Minor.Patch。</summary>
        public string CurrentVersion { get; set; }

        /// <summary>远端最新发布的版本号（已去掉 <c>v</c> 前缀）；失败时为空。</summary>
        public string LatestVersion { get; set; }

        /// <summary>最新发布的页面地址；失败时可能为仓库的 releases 页，供界面兜底打开。</summary>
        public string ReleaseUrl { get; set; }

        /// <summary>可直接显示给用户的一句话结果。</summary>
        public string Message { get; set; }
    }

    /// <summary>
    /// 只读的更新检查：取远端最新发布并与当前版本比较。不下载、不替换、不写配置。
    /// </summary>
    public interface IUpdateService
    {
        /// <summary>检查是否有更新；失败通过 <see cref="UpdateCheckResult.Message"/> 返回，不抛异常。</summary>
        Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken);
    }

    /// <summary>
    /// release 标签与版本号的归一化与比较。标签允许 <c>v1.2.3</c>、<c>V1.2.3</c>、
    /// <c>refs/tags/v1.2.3</c> 三种写法；比较时按数字段进行，预发布后缀（<c>-beta.1</c>）
    /// 排在正式版之前。
    /// </summary>
    public static class UpdateVersions
    {
        private const string TagRefPrefix = "refs/tags/";

        /// <summary>把标签归一化成 <c>Major.Minor.Patch</c> 形式；无法识别时返回原值（去空白）。</summary>
        public static string Normalize(string tag)
        {
            if (string.IsNullOrWhiteSpace(tag))
            {
                return null;
            }

            string value = tag.Trim();
            if (value.StartsWith(TagRefPrefix, StringComparison.OrdinalIgnoreCase))
            {
                value = value.Substring(TagRefPrefix.Length);
            }

            if (value.Length > 1
                && (value[0] == 'v' || value[0] == 'V')
                && char.IsDigit(value[1]))
            {
                value = value.Substring(1);
            }

            return value;
        }

        /// <summary>
        /// 比较两个版本：正数表示 <paramref name="left"/> 更新，0 表示相同，负数表示更旧。
        /// 两边都能解析成数字版本时按 <see cref="Version"/> 比较；否则退回序数比较。
        /// </summary>
        public static int Compare(string left, string right)
        {
            string a = Normalize(left);
            string b = Normalize(right);
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b))
            {
                return string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
            }

            string suffixA;
            string suffixB;
            Version versionA;
            Version versionB;
            bool numericA = TryParseNumeric(a, out versionA, out suffixA);
            bool numericB = TryParseNumeric(b, out versionB, out suffixB);

            if (numericA && numericB)
            {
                int result = versionA.CompareTo(versionB);
                if (result != 0)
                {
                    return result;
                }

                // 同一数字版本：正式版（无后缀）比预发布版新。
                bool preA = !string.IsNullOrEmpty(suffixA);
                bool preB = !string.IsNullOrEmpty(suffixB);
                if (preA != preB)
                {
                    return preA ? -1 : 1;
                }

                return string.Compare(suffixA, suffixB, StringComparison.OrdinalIgnoreCase);
            }

            return string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>把 <c>1.2.3-beta.1+sha</c> 拆成数字版本与后缀；后缀含前导 <c>-</c> 或 <c>+</c>。</summary>
        private static bool TryParseNumeric(string value, out Version version, out string suffix)
        {
            version = null;
            suffix = null;

            int cut = value.IndexOfAny(new[] { '-', '+' });
            string core = cut < 0 ? value : value.Substring(0, cut);
            if (cut >= 0)
            {
                suffix = value.Substring(cut);
            }

            return Version.TryParse(core, out version);
        }
    }

    /// <summary>release 页面地址与 GitHub API 地址之间的换算。</summary>
    public static class ReleasePages
    {
        private const string ApiHost = "api.github.com";
        private const string WebHost = "github.com";
        private const string LatestPath = "/releases/latest";

        /// <summary>
        /// 把 <c>https://github.com/owner/repo/releases</c>（或带 <c>/tag/...</c>、末尾斜杠、
        /// <c>api.github.com</c> 的地址）换算成 <c>https://api.github.com/repos/owner/repo/releases/latest</c>。
        /// 不是 GitHub 地址时原样返回，交给调用方直接请求。
        /// </summary>
        public static string ToLatestReleaseApi(string releasesUrl)
        {
            if (string.IsNullOrWhiteSpace(releasesUrl))
            {
                return null;
            }

            string value = releasesUrl.Trim();
            Uri uri;
            if (!Uri.TryCreate(value, UriKind.Absolute, out uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                return value;
            }

            string[] segments = uri.AbsolutePath
                .Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0)
            {
                return value;
            }

            if (string.Equals(uri.Host, ApiHost, StringComparison.OrdinalIgnoreCase))
            {
                // 已是 API 地址：/repos/owner/repo/releases/latest、/repos/owner/repo/releases、...
                if (segments.Length >= 4
                    && string.Equals(segments[0], "repos", StringComparison.OrdinalIgnoreCase))
                {
                    return "https://" + ApiHost + "/repos/" + segments[1] + "/" + segments[2] + LatestPath;
                }

                return value;
            }

            if (string.Equals(uri.Host, WebHost, StringComparison.OrdinalIgnoreCase)
                || string.Equals(uri.Host, "www." + WebHost, StringComparison.OrdinalIgnoreCase))
            {
                if (segments.Length >= 2)
                {
                    return "https://" + ApiHost + "/repos/" + segments[0] + "/" + segments[1] + LatestPath;
                }
            }

            return value;
        }
    }
}
