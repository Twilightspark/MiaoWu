using MiaoWu.Core.Updates;
using MiaoWu.Infrastructure.Updates;
using Xunit;

namespace MiaoWu.Tests.Updates
{
    /// <summary>
    /// 检查更新：标签归一化、版本比较、release 页到 API 的换算，以及 GitHub
    /// latest release JSON 的解析（不联网，网络分支由 UI 手工验收）。
    /// </summary>
    public sealed class UpdateServiceTests
    {
        private const string ReleasesPage = "https://github.com/Twilightspark/MiaoWu/releases";

        [Fact]
        public void TagPrefixesAreNormalized()
        {
            Assert.Equal("0.8.0", UpdateVersions.Normalize("v0.8.0"));
            Assert.Equal("0.8.0", UpdateVersions.Normalize("V0.8.0"));
            Assert.Equal("0.8.0", UpdateVersions.Normalize("refs/tags/v0.8.0"));
            Assert.Equal("0.8", UpdateVersions.Normalize(" 0.8 "));
            Assert.Equal("v", UpdateVersions.Normalize("v"));
            Assert.Null(UpdateVersions.Normalize(null));
            Assert.Null(UpdateVersions.Normalize("  "));
        }

        [Fact]
        public void ComparesVersionsNumerically()
        {
            Assert.True(UpdateVersions.Compare("0.10.0", "0.9.9") > 0);
            Assert.True(UpdateVersions.Compare("v0.8.0", "0.8.0") == 0);
            Assert.True(UpdateVersions.Compare("0.7.9", "0.8.0") < 0);
            // 预发布版同数字版本时排在正式版之前。
            Assert.True(UpdateVersions.Compare("0.8.0-beta.1", "0.8.0") < 0);
            Assert.True(UpdateVersions.Compare("0.8.0", "0.8.0-rc.1") > 0);
        }

        [Fact]
        public void MapsReleasePageToApiUrl()
        {
            Assert.Equal(
                "https://api.github.com/repos/Twilightspark/MiaoWu/releases/latest",
                ReleasePages.ToLatestReleaseApi(ReleasesPage));
            Assert.Equal(
                "https://api.github.com/repos/Twilightspark/MiaoWu/releases/latest",
                ReleasePages.ToLatestReleaseApi(ReleasesPage + "/"));
            Assert.Equal(
                "https://api.github.com/repos/Twilightspark/MiaoWu/releases/latest",
                ReleasePages.ToLatestReleaseApi("https://github.com/Twilightspark/MiaoWu/releases/tag/v0.8.0"));
            Assert.Equal(
                "https://api.github.com/repos/Twilightspark/MiaoWu/releases/latest",
                ReleasePages.ToLatestReleaseApi(
                    "https://api.github.com/repos/Twilightspark/MiaoWu/releases/tag/v0.8.0"));
            Assert.Equal(
                "https://example.com/update.json",
                ReleasePages.ToLatestReleaseApi("https://example.com/update.json"));
            Assert.Null(ReleasePages.ToLatestReleaseApi(null));
        }

        [Fact]
        public void ParsesLatestReleaseAsCurrent()
        {
            const string json = "{\"tag_name\":\"v0.8.0\",\"html_url\":\""
                + ReleasesPage + "/tag/v0.8.0\",\"name\":\"0.8.0\",\"draft\":false,\"prerelease\":false}";

            UpdateCheckResult result = GitHubReleaseUpdateService.ParseLatestRelease(json, "0.8.0", ReleasesPage);

            Assert.True(result.Succeeded);
            Assert.False(result.HasUpdate);
            Assert.Equal("0.8.0", result.LatestVersion);
            Assert.Equal(ReleasesPage + "/tag/v0.8.0", result.ReleaseUrl);
            Assert.Contains("最新版本", result.Message);
        }

        [Fact]
        public void ParsesNewerRelease()
        {
            const string json = "{\"tag_name\":\"v0.9.1\",\"html_url\":\""
                + ReleasesPage + "/tag/v0.9.1\"}";

            UpdateCheckResult result = GitHubReleaseUpdateService.ParseLatestRelease(json, "0.8.0", ReleasesPage);

            Assert.True(result.Succeeded);
            Assert.True(result.HasUpdate);
            Assert.Equal("0.9.1", result.LatestVersion);
            Assert.Contains("发现新版本", result.Message);
        }

        [Fact]
        public void FallsBackToReleasePageWhenHtmlUrlMissing()
        {
            const string json = "{\"tag_name\":\"v0.9.1\"}";

            UpdateCheckResult result = GitHubReleaseUpdateService.ParseLatestRelease(json, "0.8.0", ReleasesPage);

            Assert.True(result.Succeeded);
            Assert.Equal(ReleasesPage, result.ReleaseUrl);
        }

        [Fact]
        public void RejectsReleaseWithoutVersion()
        {
            UpdateCheckResult result = GitHubReleaseUpdateService.ParseLatestRelease("{}", "0.8.0", ReleasesPage);

            Assert.False(result.Succeeded);
            Assert.Contains("缺少版本号", result.Message);
        }

        [Fact]
        public void RejectsMalformedJson()
        {
            UpdateCheckResult result = GitHubReleaseUpdateService.ParseLatestRelease(
                "<html>rate limited</html>",
                "0.8.0",
                ReleasesPage);

            Assert.False(result.Succeeded);
            Assert.Contains("解析", result.Message);
        }
    }
}
