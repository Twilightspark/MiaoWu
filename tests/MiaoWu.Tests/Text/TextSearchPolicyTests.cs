using System.Collections.Generic;
using MiaoWu.Core.Text;
using Xunit;

namespace MiaoWu.Tests.Text
{
    public sealed class TextSearchPolicyTests
    {
        [Fact]
        public void FindAllReturnsEveryNonOverlappingMatch()
        {
            IReadOnlyList<TextMatch> matches = TextSearchPolicy.FindAll("hello world hello", "hello");

            Assert.Equal(2, matches.Count);
            Assert.Equal(0, matches[0].Index);
            Assert.Equal(12, matches[1].Index);
            Assert.Equal(5, matches[0].Length);
        }

        [Fact]
        public void FindAllIgnoresCaseByDefault()
        {
            IReadOnlyList<TextMatch> matches = TextSearchPolicy.FindAll("Alpha alpha ALPHA", "alpha");

            Assert.Equal(3, matches.Count);
            Assert.Equal(0, matches[0].Index);
            Assert.Equal(6, matches[1].Index);
            Assert.Equal(12, matches[2].Index);
        }

        [Fact]
        public void FindAllHonorsMatchCase()
        {
            IReadOnlyList<TextMatch> matches = TextSearchPolicy.FindAll("Alpha alpha ALPHA", "alpha", true);

            Assert.Single(matches);
            Assert.Equal(6, matches[0].Index);
        }

        [Fact]
        public void FindAllWorksOnChinese()
        {
            IReadOnlyList<TextMatch> matches = TextSearchPolicy.FindAll("中文测试中文", "中文");

            Assert.Equal(2, matches.Count);
            Assert.Equal(0, matches[0].Index);
            Assert.Equal(4, matches[1].Index);
            Assert.Equal(2, matches[0].Length);
        }

        [Fact]
        public void FindAllSkipsOverlappingHits()
        {
            Assert.Single(TextSearchPolicy.FindAll("aaa", "aa"));

            IReadOnlyList<TextMatch> matches = TextSearchPolicy.FindAll("aaaa", "aa");
            Assert.Equal(2, matches.Count);
            Assert.Equal(0, matches[0].Index);
            Assert.Equal(2, matches[1].Index);
        }

        [Fact]
        public void FindAllReturnsEmptyForMissingQuery()
        {
            Assert.Empty(TextSearchPolicy.FindAll("abc", "zzz"));
            Assert.Empty(TextSearchPolicy.FindAll("abc", "abcd"));
        }

        [Fact]
        public void FindAllReturnsEmptyForBlankInput()
        {
            Assert.Empty(TextSearchPolicy.FindAll(null, "a"));
            Assert.Empty(TextSearchPolicy.FindAll(string.Empty, "a"));
            Assert.Empty(TextSearchPolicy.FindAll("abc", null));
            Assert.Empty(TextSearchPolicy.FindAll("abc", string.Empty));
            Assert.Empty(TextSearchPolicy.FindAll("abc", "   "));
        }

        [Fact]
        public void FindAllStopsAtMaxMatches()
        {
            Assert.Equal(500, TextSearchPolicy.MaxMatches);

            string text = new string('a', 600);
            IReadOnlyList<TextMatch> matches = TextSearchPolicy.FindAll(text, "a");

            Assert.Equal(TextSearchPolicy.MaxMatches, matches.Count);
            Assert.Equal(499, matches[matches.Count - 1].Index);
        }

        [Theory]
        [InlineData(0, 3, true, 1)]
        [InlineData(1, 3, true, 2)]
        [InlineData(2, 3, true, 0)]
        [InlineData(-1, 3, true, 0)]
        [InlineData(99, 3, true, 0)]
        [InlineData(2, 3, false, 1)]
        [InlineData(1, 3, false, 0)]
        [InlineData(0, 3, false, 2)]
        [InlineData(-1, 3, false, 2)]
        [InlineData(99, 3, false, 2)]
        [InlineData(0, 1, true, 0)]
        [InlineData(0, 1, false, 0)]
        [InlineData(0, 0, true, -1)]
        [InlineData(0, 0, false, -1)]
        [InlineData(0, -3, true, -1)]
        public void NextIndexWrapsAroundAndHandlesOutOfRange(int current, int count, bool forward, int expected)
        {
            Assert.Equal(expected, TextSearchPolicy.NextIndex(current, count, forward));
        }

        [Fact]
        public void NextIndexWalksThroughMatchesFromFindAll()
        {
            IReadOnlyList<TextMatch> matches = TextSearchPolicy.FindAll("a b a b a", "a");
            Assert.Equal(3, matches.Count);

            int index = -1;
            index = TextSearchPolicy.NextIndex(index, matches.Count, true);
            Assert.Equal(0, index);
            index = TextSearchPolicy.NextIndex(index, matches.Count, true);
            Assert.Equal(1, index);
            index = TextSearchPolicy.NextIndex(index, matches.Count, false);
            Assert.Equal(0, index);
            index = TextSearchPolicy.NextIndex(index, matches.Count, false);
            Assert.Equal(2, index);
        }
    }
}
