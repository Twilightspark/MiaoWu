using MiaoWu.Core.Layout;
using Xunit;

namespace MiaoWu.Tests.Layout
{
    public sealed class WindowPlacementPolicyTests
    {
        [Fact]
        public void LargeWorkAreaUsesDefaultSizeAndCenters()
        {
            WindowPlacement placement = WindowPlacementPolicy.Compute(0, 0, 2560, 1392);

            Assert.Equal(1440, placement.Width, 3);
            Assert.Equal(900, placement.Height, 3);
            Assert.Equal(560, placement.Left, 3);
            Assert.Equal(246, placement.Top, 3);
            Assert.Equal(1180, placement.MinimumWidth, 3);
            Assert.Equal(760, placement.MinimumHeight, 3);
        }

        [Fact]
        public void SmallLaptopWorkAreaShrinksToNinetyPercent()
        {
            WindowPlacement placement = WindowPlacementPolicy.Compute(0, 0, 1366, 728);

            Assert.Equal(1229.4, placement.Width, 3);
            Assert.Equal(728, placement.Height, 3);
            Assert.Equal(1180, placement.MinimumWidth, 3);
            Assert.Equal(728, placement.MinimumHeight, 3);
        }

        [Fact]
        public void ScaledWorkAreaClampsMinimumToWorkArea()
        {
            WindowPlacement placement = WindowPlacementPolicy.Compute(0, 0, 1092, 582);

            Assert.Equal(1092, placement.Width, 3);
            Assert.Equal(582, placement.Height, 3);
            Assert.Equal(1092, placement.MinimumWidth, 3);
            Assert.Equal(582, placement.MinimumHeight, 3);
        }

        [Fact]
        public void ZeroWorkAreaFallsBackToDesignDefaults()
        {
            WindowPlacement placement = WindowPlacementPolicy.Compute(0, 0, 0, 0);

            Assert.Equal(1440, placement.Width, 3);
            Assert.Equal(900, placement.Height, 3);
            Assert.Equal(1180, placement.MinimumWidth, 3);
            Assert.Equal(760, placement.MinimumHeight, 3);
        }
    }
}
