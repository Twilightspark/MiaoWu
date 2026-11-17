using MiaoWu.Core.Settings;
using Xunit;

namespace MiaoWu.Tests.Settings
{
    public sealed class CloseWindowPolicyTests
    {
        [Fact]
        public void AskAlwaysPrompts()
        {
            Assert.Equal(
                CloseWindowDisposition.Ask,
                CloseWindowPolicy.Resolve(CloseWindowAction.Ask, true));
            Assert.Equal(
                CloseWindowDisposition.Ask,
                CloseWindowPolicy.Resolve(CloseWindowAction.Ask, false));
        }

        [Fact]
        public void MinimizeUsesTrayWhenAvailable()
        {
            Assert.Equal(
                CloseWindowDisposition.HideToTray,
                CloseWindowPolicy.Resolve(CloseWindowAction.MinimizeToTray, true));
        }

        [Fact]
        public void MinimizeFallsBackToTaskbarWithoutTray()
        {
            Assert.Equal(
                CloseWindowDisposition.MinimizeToTaskbar,
                CloseWindowPolicy.Resolve(CloseWindowAction.MinimizeToTray, false));
        }

        [Fact]
        public void ExitAlwaysExitsEvenWithoutTray()
        {
            Assert.Equal(
                CloseWindowDisposition.Exit,
                CloseWindowPolicy.Resolve(CloseWindowAction.Exit, true));
            Assert.Equal(
                CloseWindowDisposition.Exit,
                CloseWindowPolicy.Resolve(CloseWindowAction.Exit, false));
        }
    }
}
