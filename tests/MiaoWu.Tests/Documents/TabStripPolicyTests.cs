using System;
using System.Collections.Generic;
using MiaoWu.Core.Documents;
using Xunit;

namespace MiaoWu.Tests.Documents
{
    public sealed class TabStripPolicyTests
    {
        [Theory]
        [InlineData(0, 3, 0)]
        [InlineData(2, 2, 1)]
        [InlineData(2, 0, -1)]
        [InlineData(-1, 3, 0)]
        public void NextSelectionAfterCloseStaysAdjacent(int closed, int remaining, int expected)
        {
            Assert.Equal(expected, TabStripPolicy.GetNextSelectionAfterClose(closed, remaining));
        }

        [Theory]
        [InlineData(-1, 0, -1)]
        [InlineData(-3, 4, 0)]
        [InlineData(9, 4, 3)]
        [InlineData(2, 4, 2)]
        public void NormalizeSelectionClamps(int index, int count, int expected)
        {
            Assert.Equal(expected, TabStripPolicy.NormalizeSelection(index, count));
        }

        [Fact]
        public void FindByPathMatchesFileTabsCaseInsensitively()
        {
            var tabs = new List<DocumentTab>
            {
                DocumentTab.CreateStatic(DocumentTabKind.Diff, "diff"),
                DocumentTab.CreateFile(@"C:\w\README.md", "README.md")
            };

            Assert.NotNull(TabStripPolicy.FindByPath(tabs, @"c:\W\readme.MD"));
            Assert.Null(TabStripPolicy.FindByPath(tabs, @"C:\w\other.md"));
            Assert.Null(TabStripPolicy.FindByPath(tabs, " "));
        }

        [Fact]
        public void StripMetricsDriveContentWidthAndOverflow()
        {
            Assert.Equal(0, TabStripPolicy.GetContentWidth(0));
            Assert.Equal(168, TabStripPolicy.GetContentWidth(1));
            Assert.Equal(168 * 3 + 12, TabStripPolicy.GetContentWidth(3));

            // 视口减去「+」按钮后仍放不下才算溢出。
            Assert.True(TabStripPolicy.HasOverflow(3, 500));
            Assert.False(TabStripPolicy.HasOverflow(3, 600));
            Assert.False(TabStripPolicy.HasOverflow(1, 300));
            Assert.False(TabStripPolicy.HasOverflow(0, 600));
        }

        [Fact]
        public void DocumentTabKeepsItsIdentityAndScrollOffset()
        {
            DocumentTab file = DocumentTab.CreateFile(@"C:\a.md", "a.md");
            DocumentTab staticTab = DocumentTab.CreateStatic(DocumentTabKind.Diff, "diff");
            Assert.True(file.IsFileTab);
            Assert.False(staticTab.IsFileTab);
            Assert.Null(staticTab.FilePath);

            file.ScrollOffset = 120;
            DocumentTab copy = file.Clone();
            Assert.Equal(file.Id, copy.Id);
            Assert.Equal(120, copy.ScrollOffset);
            Assert.NotSame(file, copy);

            DocumentTab second = DocumentTab.CreateFile(@"C:\a.md", "a.md");
            Assert.NotEqual(file.Id, second.Id);
            Assert.False(string.IsNullOrEmpty(file.Id));
            Assert.True(Enum.IsDefined(typeof(DocumentTabKind), file.Kind));
        }
    }
}
