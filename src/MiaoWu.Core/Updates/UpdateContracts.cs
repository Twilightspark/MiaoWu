using System;
using System.IO;
using System.Text;
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

        /// <summary>发布里的安装包文件名；没有可下载安装包时为空。</summary>
        public string AssetName { get; set; }

        /// <summary>安装包的直接下载地址；没有可下载安装包时为空。</summary>
        public string AssetUrl { get; set; }

        /// <summary>安装包字节数；GitHub 没给大小时为 0。</summary>
        public long AssetSize { get; set; }

        /// <summary>可直接显示给用户的一句话结果。</summary>
        public string Message { get; set; }
    }

    /// <summary>
    /// 只读的更新检查：取远端最新发布并与当前版本比较。
    /// 下载安装包是另一条契约（<see cref="IUpdateDownloader"/>），检查接口本身仍不写本地状态。
    /// </summary>
    public interface IUpdateService
    {
        /// <summary>检查是否有更新；失败通过 <see cref="UpdateCheckResult.Message"/> 返回，不抛异常。</summary>
        Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken);
    }

    /// <summary>把最新发布的安装包下载到本地缓存的结果。</summary>
    public sealed class UpdateDownloadResult
    {
        /// <summary>下载是否完成并落到磁盘。</summary>
        public bool Succeeded { get; set; }

        /// <summary>下载完成后的本地安装包路径；失败时为空。</summary>
        public string FilePath { get; set; }

        /// <summary>实际写入的字节数。</summary>
        public long Bytes { get; set; }

        /// <summary>可直接显示给用户的一句话结果。</summary>
        public string Message { get; set; }
    }

    /// <summary>
    /// 下载最新发布的安装包。实现负责选择本地缓存路径、覆盖旧包与失败清理；
    /// 调用方只拿最终路径，不接触网络与临时文件。
    /// </summary>
    public interface IUpdateDownloader
    {
        /// <summary>
        /// 下载 <paramref name="update"/> 指向的安装包。进度按 0–1 汇报，长度未知时可为空；
        /// 失败通过 <see cref="UpdateDownloadResult.Message"/> 返回，不抛异常（取消除外）。
        /// </summary>
        Task<UpdateDownloadResult> DownloadAsync(
            UpdateCheckResult update,
            IProgress<double> progress,
            CancellationToken cancellationToken);
    }

    /// <summary>启动已下载的安装包。安装程序自身负责提权与关闭正在运行的旧版本。</summary>
    public interface IUpdateInstaller
    {
        /// <summary>启动安装程序；返回是否已成功创建安装进程。</summary>
        bool Install(string installerPath, out string error);
    }

    /// <summary>
    /// 发布资产的选择与本地文件命名。GitHub 的 release 往往同时挂安装包、离线全量包
    /// 与便携 zip，这里统一挑「日常安装包优先，其次全量包，再次任意 exe」。
    /// </summary>
    public static class UpdateAssets
    {
        private const int MaxFileNameLength = 120;
        private const string FallbackSuffix = "-setup.exe";

        private static readonly string[] ReservedNames =
        {
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
        };

        /// <summary>
        /// 资产优先级：3 = 日常安装包，2 = 离线全量包，1 = 其它 exe，0 = 不是安装包。
        /// 卸载程序（unins*）与 zip / 校验文件不参与。
        /// </summary>
        public static int InstallerPriority(string assetName)
        {
            if (string.IsNullOrWhiteSpace(assetName))
            {
                return 0;
            }

            string name = Path.GetFileName(assetName.Trim());
            if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                || name.IndexOf("unins", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return 0;
            }

            int setup = name.IndexOf("setup", StringComparison.OrdinalIgnoreCase);
            if (setup >= 0)
            {
                return name.IndexOf("offline", StringComparison.OrdinalIgnoreCase) >= 0 ? 2 : 3;
            }

            return 1;
        }

        /// <summary>
        /// 把发布资产名转成安全的本地文件名：去掉目录、非法字符、保留设备名与超长部分；
        /// 资产名不可用时退回 <c>MiaoWu-&lt;版本&gt;-setup.exe</c>。
        /// </summary>
        public static string SafeInstallerFileName(string assetName, string latestVersion)
        {
            string name = Path.GetFileName((assetName ?? string.Empty).Trim());
            if (name.Length == 0)
            {
                return FallbackName(latestVersion);
            }

            var builder = new StringBuilder(name.Length);
            foreach (char c in name)
            {
                builder.Append(IsInvalidFileNameChar(c) ? '_' : c);
            }

            string value = builder.ToString().Trim().TrimEnd('.', ' ');
            if (value.Length == 0 || value == "." || value == "..")
            {
                return FallbackName(latestVersion);
            }

            if (!value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                value += ".exe";
            }

            if (value.Length > MaxFileNameLength)
            {
                string extension = Path.GetExtension(value);
                int keep = MaxFileNameLength - extension.Length;
                value = keep > 0 ? value.Substring(0, keep) + extension : FallbackName(latestVersion);
            }

            string stem = Path.GetFileNameWithoutExtension(value);
            if (IsReservedName(stem))
            {
                value = "_" + value;
            }

            return value;
        }

        private static string FallbackName(string latestVersion)
        {
            string version = string.IsNullOrWhiteSpace(latestVersion)
                ? "update"
                : latestVersion.Trim();
            var builder = new StringBuilder();
            foreach (char c in version)
            {
                builder.Append(char.IsLetterOrDigit(c) || c == '.' || c == '-' ? c : '_');
            }

            string safeVersion = builder.Length == 0 ? "update" : builder.ToString();
            return "MiaoWu-" + safeVersion + FallbackSuffix;
        }

        private static bool IsInvalidFileNameChar(char c)
        {
            if (c < 32)
            {
                return true;
            }

            return c == '<' || c == '>' || c == ':' || c == '"'
                || c == '/' || c == '\\' || c == '|' || c == '?' || c == '*';
        }

        private static bool IsReservedName(string stem)
        {
            if (string.IsNullOrEmpty(stem))
            {
                return false;
            }

            foreach (string reserved in ReservedNames)
            {
                if (string.Equals(stem, reserved, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }
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
