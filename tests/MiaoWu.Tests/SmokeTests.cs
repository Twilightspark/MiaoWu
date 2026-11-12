using MiaoWu.Core;
using Xunit;

namespace MiaoWu.Tests
{
    public sealed class SmokeTests
    {
        [Fact]
        public void AppInfoHasName()
        {
            Assert.Equal("MiaoWu", AppInfo.Name);
        }
    }
}
