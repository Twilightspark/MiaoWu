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
            Assert.Equal(1080, placement.MinimumWidth, 3);
            Assert.Equal(760, placement.MinimumHeight, 3);
        }

        [Fact]
        public void SmallLaptopWorkAreaShrinksToNinetyPercent()
        {
            WindowPlacement placement = WindowPlacementPolicy.Compute(0, 0, 1366, 728);

            Assert.Equal(1229.4, placement.Width, 3);
            Assert.Equal(728, placement.Height, 3);
            Assert.Equal(1080, placement.MinimumWidth, 3);
            Assert.Equal(728, placement.MinimumHeight, 3);
        }

        [Fact]
        public void ScaledWorkAreaUsesContentMinimum()
        {
            WindowPlacement placement = WindowPlacementPolicy.Compute(0, 0, 1092, 582);

            Assert.Equal(1080, placement.Width, 3);
            Assert.Equal(582, placement.Height, 3);
            Assert.Equal(1080, placement.MinimumWidth, 3);
            Assert.Equal(582, placement.MinimumHeight, 3);
        }

        [Fact]
        public void ZeroWorkAreaFallsBackToDesignDefaults()
        {
            WindowPlacement placement = WindowPlacementPolicy.Compute(0, 0, 0, 0);

            Assert.Equal(1440, placement.Width, 3);
            Assert.Equal(900, placement.Height, 3);
            Assert.Equal(1080, placement.MinimumWidth, 3);
            Assert.Equal(760, placement.MinimumHeight, 3);
        }

        [Fact]
        public void SavedSizeIsRestoredExactlyAndCentered()
        {
            var metrics = new WindowPlacementMetrics
            {
                DefaultWidth = 1800,
                DefaultHeight = 1000,
                RestoreSavedSize = true
            };

            WindowPlacement placement = WindowPlacementPolicy.Compute(0, 0, 1920, 1080, metrics);

            Assert.Equal(1800, placement.Width, 3);
            Assert.Equal(1000, placement.Height, 3);
            Assert.Equal(60, placement.Left, 3);
            Assert.Equal(40, placement.Top, 3);
        }

        [Fact]
        public void SavedSizeShrinksToWorkArea()
        {
            var metrics = new WindowPlacementMetrics
            {
                DefaultWidth = 2600,
                DefaultHeight = 1600,
                RestoreSavedSize = true
            };

            WindowPlacement placement = WindowPlacementPolicy.Compute(0, 0, 1920, 1040, metrics);

            Assert.Equal(1920, placement.Width, 3);
            Assert.Equal(1040, placement.Height, 3);
            Assert.Equal(0, placement.Left, 3);
            Assert.Equal(0, placement.Top, 3);
        }

        [Fact]
        public void SavedSizeStillHonorsMinimum()
        {
            var metrics = new WindowPlacementMetrics
            {
                DefaultWidth = 900,
                DefaultHeight = 600,
                RestoreSavedSize = true
            };

            WindowPlacement placement = WindowPlacementPolicy.Compute(0, 0, 1920, 1080, metrics);

            Assert.Equal(1080, placement.Width, 3);
            Assert.Equal(760, placement.Height, 3);
            Assert.Equal(420, placement.Left, 3);
            Assert.Equal(160, placement.Top, 3);
        }

        [Fact]
        public void SavedSizeOnSmallWorkAreaClampsToWorkArea()
        {
            var metrics = new WindowPlacementMetrics
            {
                DefaultWidth = 1800,
                DefaultHeight = 1000,
                RestoreSavedSize = true
            };

            WindowPlacement placement = WindowPlacementPolicy.Compute(0, 0, 1200, 700, metrics);

            Assert.Equal(1200, placement.Width, 3);
            Assert.Equal(700, placement.Height, 3);
            Assert.Equal(0, placement.Left, 3);
            Assert.Equal(0, placement.Top, 3);
        }
    }
}
