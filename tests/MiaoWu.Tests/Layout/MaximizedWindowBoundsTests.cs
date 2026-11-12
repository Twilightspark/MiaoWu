using MiaoWu.Core.Layout;
using Xunit;

namespace MiaoWu.Tests.Layout
{
    public sealed class MaximizedWindowBoundsTests
    {
        [Fact]
        public void TaskbarOnBottomUsesFullWidthAndReducedHeight()
        {
            MaximizedWindowBounds bounds = WindowPlacementPolicy.ComputeMaximizedBounds(
                0, 0, 1920, 1080,
                0, 0, 1920, 1040);

            Assert.Equal(0, bounds.X);
            Assert.Equal(0, bounds.Y);
            Assert.Equal(1920, bounds.Width);
            Assert.Equal(1040, bounds.Height);
        }

        [Fact]
        public void TaskbarOnTopOffsetsTop()
        {
            MaximizedWindowBounds bounds = WindowPlacementPolicy.ComputeMaximizedBounds(
                0, 0, 1920, 1080,
                0, 40, 1920, 1080);

            Assert.Equal(0, bounds.X);
            Assert.Equal(40, bounds.Y);
            Assert.Equal(1920, bounds.Width);
            Assert.Equal(1040, bounds.Height);
        }

        [Fact]
        public void TaskbarOnLeftOffsetsLeft()
        {
            MaximizedWindowBounds bounds = WindowPlacementPolicy.ComputeMaximizedBounds(
                0, 0, 1920, 1080,
                62, 0, 1920, 1080);

            Assert.Equal(62, bounds.X);
            Assert.Equal(0, bounds.Y);
            Assert.Equal(1858, bounds.Width);
            Assert.Equal(1080, bounds.Height);
        }

        [Fact]
        public void NegativeSecondaryMonitorUsesRelativePosition()
        {
            MaximizedWindowBounds bounds = WindowPlacementPolicy.ComputeMaximizedBounds(
                -1920, 0, 0, 1080,
                -1920, 0, 0, 1040);

            Assert.Equal(0, bounds.X);
            Assert.Equal(0, bounds.Y);
            Assert.Equal(1920, bounds.Width);
            Assert.Equal(1040, bounds.Height);
        }
    }
}
