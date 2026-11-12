using MiaoWu.Core.Windowing;
using Xunit;

namespace MiaoWu.Tests.Windowing
{
    public sealed class WindowFramePolicyTests
    {
        [Theory]
        [InlineData(7601, false)]
        [InlineData(10240, false)]
        [InlineData(19045, false)]
        [InlineData(21999, false)]
        [InlineData(22000, true)]
        [InlineData(22631, true)]
        [InlineData(26100, true)]
        public void SystemWindowShadowStartsAtWindows11(int build, bool expected)
        {
            Assert.Equal(expected, WindowFramePolicy.SupportsSystemWindowShadow(build));
        }

        [Theory]
        [InlineData(7601, 0)]
        [InlineData(19045, 0)]
        [InlineData(21999, 0)]
        [InlineData(22000, 1)]
        [InlineData(26100, 1)]
        public void GlassFrameIsOnePixelOnlyWhenSystemDrawsShadow(int build, int expected)
        {
            Assert.Equal(expected, WindowFramePolicy.GetGlassFrameThickness(build));
        }

        [Fact]
        public void GlassFrameAndShadowCapabilityStayInSync()
        {
            int[] builds = { 7601, 9600, 19045, 21999, 22000, 22631, 26100 };

            foreach (int build in builds)
            {
                int thickness = WindowFramePolicy.GetGlassFrameThickness(build);
                int expected = WindowFramePolicy.SupportsSystemWindowShadow(build) ? 1 : 0;

                Assert.Equal(expected, thickness);
            }
        }
    }
}
