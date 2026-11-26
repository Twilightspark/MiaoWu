using System;
using System.Collections.Generic;
using System.Linq;
using MiaoWu.Core.Input;
using Xunit;

namespace MiaoWu.Tests.Input
{
    /// <summary>`@` 引用的文本语法与候选排序。</summary>
    public class MentionFormatterTests
    {
        [Fact]
        public void FormatQuotesOnlyWhenNeededAndKeepsDirectoriesOpen()
        {
            Assert.Equal("@src/main.cs", MentionFormatter.Format("src\\main.cs", false, false));
            Assert.Equal("@\"my report.md\"", MentionFormatter.Format("my report.md", false, false));

            // 正在补全的目录保留开引号，方便继续输入。
            Assert.Equal("@\"my dir/", MentionFormatter.Format("my dir", true, true));

            Assert.Equal("@src/", MentionFormatter.Format("src", true, false));
            Assert.Equal("@src/", MentionFormatter.Format("src/", true, false));
        }

        [Fact]
        public void ControlCharacters_AreRejected()
        {
            Assert.Null(MentionFormatter.Format("a\nb.txt", false, false));
            Assert.Null(MentionFormatter.Format("a\"b.txt", false, false));
            Assert.Null(MentionFormatter.Format("   ", false, false));
        }

        [Fact]
        public void RelativePath_NormalizesSlashes()
        {
            Assert.Equal("a/b/c", MentionFormatter.NormalizeRelativePath("./a\\b/c/"));
        }

        [Theory]
        [InlineData("a.png", true)]
        [InlineData("b.JPG", true)]
        [InlineData("c.webp", true)]
        [InlineData("d.txt", false)]
        [InlineData("", false)]
        public void ImageDetection_UsesExtension(string path, bool expected)
        {
            Assert.Equal(expected, MentionFormatter.IsImagePath(path));
        }

        [Fact]
        public void DisplayName_UsesFileName()
        {
            Assert.Equal("main.cs", MentionFormatter.GetDisplayName("src/main.cs", false));
            Assert.Equal("src/", MentionFormatter.GetDisplayName("src", true));
            Assert.Equal("file.txt", MentionFormatter.GetDisplayName("file.txt", false));
        }

        [Fact]
        public void ParentPath_IsEmptyAtRoot()
        {
            Assert.Equal(string.Empty, MentionFormatter.GetParentPath("file.txt"));
            Assert.Equal("src", MentionFormatter.GetParentPath("src/main.cs"));
        }

        [Fact]
        public void Mention_TextMatchesKind()
        {
            var mention = new ComposerMention
            {
                RelativePath = "src",
                Kind = ComposerMentionKind.Folder
            };

            Assert.Equal("@src/", mention.Text);
        }
    }

    public class MentionSearchTests
    {
        private static IReadOnlyList<WorkspaceFileEntry> Entries()
        {
            return new List<WorkspaceFileEntry>
            {
                new WorkspaceFileEntry("README.md", false),
                new WorkspaceFileEntry("src", true),
                new WorkspaceFileEntry("src/main.cs", false),
                new WorkspaceFileEntry("src/util", true),
                new WorkspaceFileEntry("src/util/helper.cs", false),
                new WorkspaceFileEntry("docs/readme-plan.md", false),
                new WorkspaceFileEntry("assets/logo.png", false)
            };
        }

        [Fact]
        public void EmptyQuery_ReturnsRowsWithDirectoriesFirst()
        {
            IReadOnlyList<WorkspaceFileEntry> rows = MentionSearch.Rank(Entries(), string.Empty, 10);

            Assert.Equal(7, rows.Count);
            Assert.True(rows[0].IsDirectory);
            Assert.True(rows[1].IsDirectory);
        }

        [Fact]
        public void NamePrefixBeatsSubstring()
        {
            IReadOnlyList<WorkspaceFileEntry> rows = MentionSearch.Rank(Entries(), "readme", 10);

            Assert.Equal("README.md", rows[0].RelativePath);
            Assert.Contains(rows, row => row.RelativePath == "docs/readme-plan.md");
        }

        [Fact]
        public void DirectoryPrefix_ListsDirectChildren()
        {
            IReadOnlyList<WorkspaceFileEntry> rows = MentionSearch.Rank(Entries(), "src/", 10);

            Assert.Equal(2, rows.Count);
            Assert.All(rows, row => Assert.Equal("src", row.ParentPath));
            Assert.Equal("util/", rows[0].Name);
        }

        [Fact]
        public void DirectoryPrefixWithTerm_FiltersChildren()
        {
            IReadOnlyList<WorkspaceFileEntry> rows = MentionSearch.Rank(Entries(), "src/ma", 10);

            Assert.Single(rows);
            Assert.Equal("src/main.cs", rows[0].RelativePath);
        }

        [Fact]
        public void Subsequence_Matches()
        {
            IReadOnlyList<WorkspaceFileEntry> rows = MentionSearch.Rank(Entries(), "mncs", 10);

            Assert.Contains(rows, row => row.RelativePath == "src/main.cs");
        }

        [Fact]
        public void Limit_IsRespected()
        {
            Assert.Equal(2, MentionSearch.Rank(Entries(), string.Empty, 2).Count);
        }

        [Fact]
        public void ImageEntry_IsMarked()
        {
            WorkspaceFileEntry entry = Entries().First(row => row.RelativePath == "assets/logo.png");

            Assert.True(entry.IsImage);
        }
    }
}
