using MiaoWu.Infrastructure.Tools;
using Xunit;

namespace MiaoWu.Tests.Tools
{
    public sealed class ToolResultPrunerTests
    {
        [Fact]
        public void ShortContentIsUnchanged()
        {
            string content = new string('a', 100);
            Assert.Equal(content, ToolResultPruner.Prune(content));
        }

        [Fact]
        public void LongContentKeepsHeadAndTail()
        {
            string content = new string('h', 5000) + new string('m', 5000) + new string('t', 1000);

            string pruned = ToolResultPruner.Prune(content);

            Assert.True(pruned.Length < content.Length);
            Assert.StartsWith(new string('h', 4096), pruned);
            Assert.EndsWith(new string('t', 1000), pruned);
            Assert.Contains("中间省略", pruned);
        }
    }
}
