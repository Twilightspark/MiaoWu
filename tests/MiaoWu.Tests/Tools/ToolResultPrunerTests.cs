using MiaoWu.Infrastructure.Tools;
using Xunit;

namespace MiaoWu.Tests.Tools
{
    public sealed class ToolResultPrunerTests
    {
        [Fact]
        public void KeepsShortContentAndPrunesOnlyTheMiddleOfLongContent()
        {
            string content = new string('a', 100);
            Assert.Equal(content, ToolResultPruner.Prune(content));

            string longContent = new string('h', 5000) + new string('m', 5000) + new string('t', 1000);
            string pruned = ToolResultPruner.Prune(longContent);

            Assert.True(pruned.Length < longContent.Length);
            Assert.StartsWith(new string('h', 4096), pruned);
            Assert.EndsWith(new string('t', 1000), pruned);
            Assert.Contains("中间省略", pruned);
        }
    }
}
