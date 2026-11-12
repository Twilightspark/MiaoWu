using System;
using MiaoWu.Core.Markdown;
using Xunit;

namespace MiaoWu.Tests.Markdown
{
    public sealed class MarkdownAstCacheTests
    {
        private static readonly DateTime Stamp = new DateTime(2026, 11, 11, 10, 0, 0, DateTimeKind.Utc);

        [Fact]
        public void SamePathAndStampReturnsCachedDocument()
        {
            var cache = new MarkdownAstCache(4);

            MarkdownDocument first = cache.GetOrParse("a.md", Stamp, 10, "# A");
            MarkdownDocument second = cache.GetOrParse("a.md", Stamp, 10, "# DIFFERENT");

            Assert.Same(first, second);
            Assert.Single(first.Blocks);
        }

        [Fact]
        public void ChangedMtimeOrLengthReparses()
        {
            var cache = new MarkdownAstCache(4);

            MarkdownDocument first = cache.GetOrParse("a.md", Stamp, 10, "# A");
            MarkdownDocument changedStamp = cache.GetOrParse("a.md", Stamp.AddSeconds(1), 10, "# A");
            MarkdownDocument changedLength = cache.GetOrParse("a.md", Stamp.AddSeconds(1), 11, "# A");

            Assert.NotSame(first, changedStamp);
            Assert.NotSame(changedStamp, changedLength);
        }

        [Fact]
        public void CapacityEvictsLeastRecentlyUsed()
        {
            var cache = new MarkdownAstCache(2);

            cache.GetOrParse("a.md", Stamp, 1, "# A");
            cache.GetOrParse("b.md", Stamp, 1, "# B");
            cache.GetOrParse("a.md", Stamp, 1, "# A");
            cache.GetOrParse("c.md", Stamp, 1, "# C");

            Assert.Equal(2, cache.Count);

            MarkdownDocument reparseA = cache.GetOrParse("a.md", Stamp, 1, "# A");
            MarkdownDocument reparseB = cache.GetOrParse("b.md", Stamp, 1, "# B");
            Assert.Single(reparseA.Blocks);
            Assert.Single(reparseB.Blocks);
        }

        [Fact]
        public void InvalidateForcesReparse()
        {
            var cache = new MarkdownAstCache(4);
            MarkdownDocument first = cache.GetOrParse("a.md", Stamp, 1, "# A");

            cache.Invalidate("a.md");
            MarkdownDocument second = cache.GetOrParse("a.md", Stamp, 1, "# A");

            Assert.NotSame(first, second);
        }

        [Fact]
        public void ClearEmptiesCache()
        {
            var cache = new MarkdownAstCache(4);
            cache.GetOrParse("a.md", Stamp, 1, "# A");

            cache.Clear();

            Assert.Equal(0, cache.Count);
        }
    }
}
