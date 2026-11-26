using MiaoWu.Core.Windowing;
using Xunit;

namespace MiaoWu.Tests.Windowing
{
    public sealed class WindowFramePolicyTests
    {
        [Theory]
        [InlineData(7601, false)]
        [InlineData(21999, false)]
        [InlineData(22000, true)]
        [InlineData(26100, true)]
        public void SystemWindowShadowStartsAtWindows11(int build, bool expected)
        {
            Assert.Equal(expected, WindowFramePolicy.SupportsSystemWindowShadow(build));
        }

        [Fact]
        public void GlassFrameStaysInSyncWithShadowCapability()
        {
            int[] builds = { 7601, 9600, 19045, 21999, 22000, 22631, 26100 };

            foreach (int build in builds)
            {
                Assert.Equal(
                    WindowFramePolicy.SupportsSystemWindowShadow(build) ? 1 : 0,
                    WindowFramePolicy.GetGlassFrameThickness(build));
            }
        }
    }
}
