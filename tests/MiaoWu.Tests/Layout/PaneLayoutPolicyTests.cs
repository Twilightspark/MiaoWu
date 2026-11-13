using MiaoWu.Core.Layout;
using Xunit;

namespace MiaoWu.Tests.Layout
{
    public sealed class PaneLayoutPolicyTests
    {
        private readonly PaneLayoutPolicy _policy = new PaneLayoutPolicy(new PaneMetrics());

        [Fact]
        public void DefaultLayoutMatchesDesignAt1440()
        {
            PaneLayout layout = _policy.Compute(1440, false);

            Assert.Equal(262, layout.Left, 3);
            Assert.Equal(742, layout.Middle, 3);
            Assert.Equal(428, layout.Right, 3);
            Assert.False(layout.Degraded);
        }

        [Fact]
        public void MinimumWindowCompressesLeftThenRight()
        {
            PaneLayout layout = _policy.Compute(1180, false);

            Assert.Equal(208, layout.Left, 3);
            Assert.Equal(580, layout.Middle, 3);
            Assert.Equal(384, layout.Right, 3);
            Assert.False(layout.Degraded);
        }

        [Fact]
        public void LeftReachesMinimumBeforeRight()
        {
            PaneLayout layout = _policy.Compute(1386, false);

            Assert.Equal(208, layout.Left, 3);
            Assert.Equal(742, layout.Middle, 3);
            Assert.Equal(428, layout.Right, 3);
        }

        [Fact]
        public void RightReachesMinimumBeforeMiddle()
        {
            PaneLayout layout = _policy.Compute(1342, false);

            Assert.Equal(208, layout.Left, 3);
            Assert.Equal(742, layout.Middle, 3);
            Assert.Equal(384, layout.Right, 3);
        }

        [Fact]
        public void MiddleHitsMinimumAt1080()
        {
            PaneLayout layout = _policy.Compute(1080, false);

            Assert.Equal(208, layout.Left, 3);
            Assert.Equal(480, layout.Middle, 3);
            Assert.Equal(384, layout.Right, 3);
            Assert.False(layout.Degraded);
        }

        [Fact]
        public void MinimumWindowSizeComesFromPaneContent()
        {
            Assert.Equal(1080, _policy.MinimumWindowWidth, 3);
            Assert.Equal(760, _policy.MinimumWindowHeight, 3);
        }

        [Fact]
        public void CollapsedLayoutReusesLeftAndGivesWidthToMiddle()
        {
            PaneLayout layout = _policy.Compute(1440, true);

            Assert.Equal(262, layout.Left, 3);
            Assert.Equal(1170, layout.Middle, 3);
            Assert.Equal(0, layout.Right, 3);
        }

        [Fact]
        public void CollapsedLayoutShrinksLeftAtMinimumWindow()
        {
            PaneLayout layout = _policy.Compute(1180, true);

            Assert.Equal(208, layout.Left, 3);
            Assert.Equal(964, layout.Middle, 3);
            Assert.Equal(0, layout.Right, 3);
        }

        [Fact]
        public void UserPreferredWidthsWinWhenSpaceAllows()
        {
            _policy.SetPreferred(300, 500);

            PaneLayout layout = _policy.Compute(1440, false);

            Assert.Equal(300, layout.Left, 3);
            Assert.Equal(632, layout.Middle, 3);
            Assert.Equal(500, layout.Right, 3);
        }

        [Fact]
        public void SplitterMaximumKeepsMiddleAtLeastMinimum()
        {
            Assert.Equal(308, _policy.GetLeftMaximum(1180, 384), 3);
            Assert.Equal(484, _policy.GetRightMaximum(1180, 208), 3);
        }
    }
}
