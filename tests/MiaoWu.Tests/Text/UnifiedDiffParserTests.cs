using System.Collections.Generic;
using MiaoWu.Core.Text;
using Xunit;

namespace MiaoWu.Tests.Text
{
    public sealed class UnifiedDiffParserTests
    {
        private const string SampleDiff =
            "diff --git a/sample.txt b/sample.txt\n" +
            "index 1234567..89abcde 100644\n" +
            "--- a/sample.txt\n" +
            "+++ b/sample.txt\n" +
            "@@ -1,3 +1,4 @@\n" +
            " line one\n" +
            "-old line\n" +
            "+new line\n" +
            "+extra line\n" +
            " line three\n" +
            "@@ -10,3 +11,3 @@ section\n" +
            " context a\n" +
            "-removed\n" +
            "+added\n" +
            " context b";

        [Fact]
        public void ParsesFileHeadersWithOriginalText()
        {
            DiffDocument document = UnifiedDiffParser.Parse(SampleDiff);

            Assert.Equal(15, document.Lines.Count);
            Assert.Equal(DiffLineKind.FileHeader, document.Lines[0].Kind);
            Assert.Equal("diff --git a/sample.txt b/sample.txt", document.Lines[0].Text);
            Assert.Equal("index 1234567..89abcde 100644", document.Lines[1].Text);
            Assert.Equal("--- a/sample.txt", document.Lines[2].Text);
            Assert.Equal("+++ b/sample.txt", document.Lines[3].Text);
            Assert.Null(document.Lines[0].OldLineNumber);
            Assert.Null(document.Lines[0].NewLineNumber);
            Assert.False(document.IsEmpty);
        }

        [Fact]
        public void HunkHeadersCarryParsedStartLineNumbers()
        {
            DiffDocument document = UnifiedDiffParser.Parse(SampleDiff);

            DiffLine first = document.Lines[4];
            Assert.Equal(DiffLineKind.HunkHeader, first.Kind);
            Assert.Equal("@@ -1,3 +1,4 @@", first.Text);
            Assert.Equal<int?>(1, first.OldLineNumber);
            Assert.Equal<int?>(1, first.NewLineNumber);

            DiffLine second = document.Lines[10];
            Assert.Equal(DiffLineKind.HunkHeader, second.Kind);
            Assert.Equal("@@ -10,3 +11,3 @@ section", second.Text);
            Assert.Equal<int?>(10, second.OldLineNumber);
            Assert.Equal<int?>(11, second.NewLineNumber);
            Assert.False(first.IsChange);
        }

        [Fact]
        public void LineNumbersStayContinuousThroughBothHunks()
        {
            DiffDocument document = UnifiedDiffParser.Parse(SampleDiff);

            AssertContext(document.Lines[5], "line one", 1, 1);
            Assert.Equal("old line", document.Lines[6].Text);
            Assert.Equal<int?>(2, document.Lines[6].OldLineNumber);
            Assert.Null(document.Lines[6].NewLineNumber);
            Assert.Equal("new line", document.Lines[7].Text);
            Assert.Null(document.Lines[7].OldLineNumber);
            Assert.Equal<int?>(2, document.Lines[7].NewLineNumber);
            Assert.Equal<int?>(3, document.Lines[8].NewLineNumber);
            AssertContext(document.Lines[9], "line three", 3, 4);

            AssertContext(document.Lines[11], "context a", 10, 11);
            Assert.Equal<int?>(11, document.Lines[12].OldLineNumber);
            Assert.Null(document.Lines[12].NewLineNumber);
            Assert.Equal<int?>(12, document.Lines[13].NewLineNumber);
            AssertContext(document.Lines[14], "context b", 12, 13);
        }

        [Fact]
        public void CountsFollowActualLinesNotFileHeaders()
        {
            DiffDocument document = UnifiedDiffParser.Parse(SampleDiff);

            Assert.Equal(3, document.AddedCount);
            Assert.Equal(2, document.RemovedCount);
        }

        [Fact]
        public void ChangeLinesDropPrefixAndAreFlagged()
        {
            DiffDocument document = UnifiedDiffParser.Parse(SampleDiff);

            Assert.True(document.Lines[6].IsChange);
            Assert.True(document.Lines[7].IsChange);
            Assert.False(document.Lines[5].IsChange);
            Assert.Equal(DiffLineKind.Removed, document.Lines[6].Kind);
            Assert.Equal(DiffLineKind.Added, document.Lines[7].Kind);
        }

        [Fact]
        public void NoNewlineMarkerKeepsWholeLine()
        {
            string diff = "@@ -1 +1 @@\n" +
                          "-old\n" +
                          "+new\n" +
                          "\\ No newline at end of file";

            DiffDocument document = UnifiedDiffParser.Parse(diff);

            DiffLine marker = document.Lines[document.Lines.Count - 1];
            Assert.Equal(DiffLineKind.NoNewline, marker.Kind);
            Assert.Equal("\\ No newline at end of file", marker.Text);
            Assert.Null(marker.OldLineNumber);
            Assert.Null(marker.NewLineNumber);
            Assert.False(marker.IsChange);
            Assert.Equal(1, document.AddedCount);
            Assert.Equal(1, document.RemovedCount);
        }

        [Fact]
        public void EmptyLineInsideHunkIsContextAndAdvancesBothCounters()
        {
            DiffDocument document = UnifiedDiffParser.Parse("@@ -1,2 +1,2 @@\n\n-a\n+b");

            Assert.Equal(4, document.Lines.Count);
            Assert.Equal(DiffLineKind.Context, document.Lines[1].Kind);
            Assert.Equal(string.Empty, document.Lines[1].Text);
            Assert.Equal<int?>(1, document.Lines[1].OldLineNumber);
            Assert.Equal<int?>(1, document.Lines[1].NewLineNumber);
            Assert.Equal<int?>(2, document.Lines[2].OldLineNumber);
            Assert.Equal<int?>(2, document.Lines[3].NewLineNumber);
        }

        [Fact]
        public void BlankLinesOutsideHunksAreDropped()
        {
            string diff = "diff --git a/x.txt b/x.txt\n" +
                          "\n" +
                          "@@ -1 +1 @@\n" +
                          "-a\n" +
                          "+b";

            DiffDocument document = UnifiedDiffParser.Parse(diff);

            Assert.Equal(4, document.Lines.Count);
            Assert.Equal(DiffLineKind.FileHeader, document.Lines[0].Kind);
            Assert.Equal(DiffLineKind.HunkHeader, document.Lines[1].Kind);
            Assert.Equal(1, document.AddedCount);
            Assert.Equal(1, document.RemovedCount);
        }

        [Fact]
        public void BlankLineAfterHunkCountsAsEmptyContext()
        {
            // hunk 后的空行仍在 hunk 内，按宽松规则当成空上下文行。
            DiffDocument document = UnifiedDiffParser.Parse("@@ -1 +1 @@\n-a\n+b\n\n");

            Assert.Equal(4, document.Lines.Count);
            Assert.Equal(DiffLineKind.Context, document.Lines[3].Kind);
            Assert.Equal(string.Empty, document.Lines[3].Text);
            Assert.Equal<int?>(2, document.Lines[3].OldLineNumber);
            Assert.Equal<int?>(2, document.Lines[3].NewLineNumber);
        }

        [Fact]
        public void MalformedHunkHeaderDoesNotThrowAndRestartsAtOne()
        {
            DiffDocument document = UnifiedDiffParser.Parse("@@ this is not a hunk @@\n+added\n context");

            DiffLine header = document.Lines[0];
            Assert.Equal(DiffLineKind.HunkHeader, header.Kind);
            Assert.Equal("@@ this is not a hunk @@", header.Text);
            Assert.Null(header.OldLineNumber);
            Assert.Null(header.NewLineNumber);
            Assert.Equal<int?>(1, document.Lines[1].NewLineNumber);
            Assert.Equal<int?>(1, document.Lines[2].OldLineNumber);
            Assert.Equal<int?>(2, document.Lines[2].NewLineNumber);
        }

        [Fact]
        public void SecondFileResetsHunkState()
        {
            string diff = "diff --git a/a.txt b/a.txt\n" +
                          "--- a/a.txt\n" +
                          "+++ b/a.txt\n" +
                          "@@ -1 +1 @@\n" +
                          "-a\n" +
                          "+b\n" +
                          "diff --git a/b.txt b/b.txt\n" +
                          "--- a/b.txt\n" +
                          "+++ b/b.txt\n" +
                          "@@ -5 +7 @@\n" +
                          "-c\n" +
                          "+d";

            DiffDocument document = UnifiedDiffParser.Parse(diff);
            IReadOnlyList<DiffLine> lines = document.Lines;

            Assert.Equal(12, lines.Count);
            Assert.Equal(DiffLineKind.FileHeader, lines[6].Kind);
            Assert.Equal("diff --git a/b.txt b/b.txt", lines[6].Text);
            Assert.Equal(DiffLineKind.HunkHeader, lines[9].Kind);
            Assert.Equal<int?>(5, lines[9].OldLineNumber);
            Assert.Equal<int?>(7, lines[9].NewLineNumber);
            // hunk 内没有上下文行，第一条新增行就是新文件的第 7 行。
            Assert.Equal<int?>(7, lines[11].NewLineNumber);
            Assert.Equal(2, document.AddedCount);
            Assert.Equal(2, document.RemovedCount);
        }

        [Fact]
        public void FileModeHeadersAreRecognized()
        {
            string diff = "diff --git a/new.txt b/new.txt\n" +
                          "new file mode 100644\n" +
                          "index 0000000..e69de29\n" +
                          "--- /dev/null\n" +
                          "+++ b/new.txt\n" +
                          "@@ -0,0 +1 @@\n" +
                          "+hello";

            DiffDocument document = UnifiedDiffParser.Parse(diff);

            Assert.Equal(7, document.Lines.Count);
            Assert.Equal("new file mode 100644", document.Lines[1].Text);
            Assert.Equal(1, document.AddedCount);
            Assert.Equal(0, document.RemovedCount);
        }

        [Fact]
        public void DeletedFileHeaderIsRecognized()
        {
            DiffDocument document = UnifiedDiffParser.Parse("diff --git a/old.txt b/old.txt\ndeleted file mode 100644");

            Assert.Equal(2, document.Lines.Count);
            Assert.Equal(DiffLineKind.FileHeader, document.Lines[1].Kind);
            Assert.Equal(0, document.AddedCount);
            Assert.Equal(0, document.RemovedCount);
        }

        [Fact]
        public void EmptyAndNullInputProduceEmptyDocument()
        {
            Assert.True(UnifiedDiffParser.Parse(null).IsEmpty);
            Assert.True(UnifiedDiffParser.Parse(string.Empty).IsEmpty);
            Assert.True(UnifiedDiffParser.Parse("   \n\n").IsEmpty);
            Assert.Equal(0, UnifiedDiffParser.Parse(null).AddedCount);
            Assert.Equal(0, UnifiedDiffParser.Parse(null).RemovedCount);
        }

        [Fact]
        public void PlainTextIsIgnoredInsteadOfCrashing()
        {
            DiffDocument document = UnifiedDiffParser.Parse("hello world\nsecond line\nno prefixes here");

            Assert.True(document.IsEmpty);
            Assert.Empty(document.Lines);
            Assert.Equal(0, document.AddedCount);
            Assert.Equal(0, document.RemovedCount);
        }

        [Fact]
        public void CrlfInputParsesTheSameAsLf()
        {
            string diff = "@@ -1 +1 @@\r\n-a\r\n+b\r\n";
            DiffDocument document = UnifiedDiffParser.Parse(diff);

            Assert.Equal(3, document.Lines.Count);
            Assert.Equal(DiffLineKind.HunkHeader, document.Lines[0].Kind);
            Assert.Equal("@@ -1 +1 @@", document.Lines[0].Text);
            Assert.Equal("a", document.Lines[1].Text);
            Assert.Equal("b", document.Lines[2].Text);
        }

        [Fact]
        public void HunkHeaderWithoutCountsIsAccepted()
        {
            DiffDocument document = UnifiedDiffParser.Parse("@@ -42 +7 @@\n-a\n+b");

            Assert.Equal<int?>(42, document.Lines[0].OldLineNumber);
            Assert.Equal<int?>(7, document.Lines[0].NewLineNumber);
            Assert.Equal<int?>(42, document.Lines[1].OldLineNumber);
            Assert.Equal<int?>(7, document.Lines[2].NewLineNumber);
        }

        private static void AssertContext(DiffLine line, string text, int oldNumber, int newNumber)
        {
            Assert.Equal(DiffLineKind.Context, line.Kind);
            Assert.Equal(text, line.Text);
            Assert.Equal<int?>(oldNumber, line.OldLineNumber);
            Assert.Equal<int?>(newNumber, line.NewLineNumber);
        }
    }
}
