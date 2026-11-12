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
        [InlineData(1, 3, 1)]
        [InlineData(2, 2, 1)]
        [InlineData(2, 0, -1)]
        [InlineData(-1, 3, 0)]
        public void NextSelectionAfterCloseStaysAdjacent(int closed, int remaining, int expected)
        {
            Assert.Equal(expected, TabStripPolicy.GetNextSelectionAfterClose(closed, remaining));
        }

        [Theory]
        [InlineData(-1, 0, -1)]
        [InlineData(0, 0, -1)]
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
        public void ContentWidthMatchesCardMetrics()
        {
            Assert.Equal(0, TabStripPolicy.GetContentWidth(0));
            Assert.Equal(168, TabStripPolicy.GetContentWidth(1));
            Assert.Equal(168 * 3 + 12, TabStripPolicy.GetContentWidth(3));
        }

        [Theory]
        [InlineData(1, 300, false)]
        [InlineData(3, 500, true)]
        [InlineData(3, 600, false)]
        [InlineData(0, 600, false)]
        public void OverflowUsesViewportMinusPlusButton(int count, double viewport, bool expected)
        {
            Assert.Equal(expected, TabStripPolicy.HasOverflow(count, viewport));
        }

        [Fact]
        public void FileTabRequiresPath()
        {
            Assert.True(DocumentTab.CreateFile(@"C:\a.md", "a.md").IsFileTab);
            Assert.False(DocumentTab.CreateStatic(DocumentTabKind.Diff, "diff").IsFileTab);
            Assert.Null(DocumentTab.CreateStatic(DocumentTabKind.Diff, "diff").FilePath);
        }

        [Fact]
        public void CloneCopiesScrollOffset()
        {
            DocumentTab tab = DocumentTab.CreateFile(@"C:\a.md", "a.md");
            tab.ScrollOffset = 120;

            DocumentTab copy = tab.Clone();

            Assert.Equal(tab.Id, copy.Id);
            Assert.Equal(120, copy.ScrollOffset);
            Assert.NotSame(tab, copy);
        }

        [Fact]
        public void CreateFileGeneratesUniqueIds()
        {
            DocumentTab first = DocumentTab.CreateFile(@"C:\a.md", "a.md");
            DocumentTab second = DocumentTab.CreateFile(@"C:\a.md", "a.md");

            Assert.NotEqual(first.Id, second.Id);
            Assert.False(string.IsNullOrEmpty(first.Id));
            Assert.True(Enum.IsDefined(typeof(DocumentTabKind), first.Kind));
        }
    }
}
