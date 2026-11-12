using System.Diagnostics;
using System.Text;
using MiaoWu.Core.Text;
using Xunit;

namespace MiaoWu.Tests.Text
{
    public sealed class UnifiedDiffBuilderTests
    {
        [Fact]
        public void IdenticalTextProducesEmptyDiff()
        {
            UnifiedDiffResult result = UnifiedDiffBuilder.Build("a\nb\n", "a\nb\n", "file.txt");

            Assert.True(result.IsEmpty);
            Assert.Equal(0, result.AddedCount);
            Assert.Equal(0, result.RemovedCount);
        }

        [Fact]
        public void SingleLineChangeProducesOneHunk()
        {
            UnifiedDiffResult result = UnifiedDiffBuilder.Build(
                "a\nb\nc\n",
                "a\nB\nc\n",
                "file.txt");

            Assert.False(result.IsEmpty);
            Assert.Equal(1, result.AddedCount);
            Assert.Equal(1, result.RemovedCount);
            Assert.Contains("--- a/file.txt", result.Text);
            Assert.Contains("+++ b/file.txt", result.Text);
            Assert.Contains("-b", result.Text);
            Assert.Contains("+B", result.Text);
            Assert.Contains("@@", result.Text);
        }

        [Fact]
        public void ParserRoundTripsAddedAndRemovedCounts()
        {
            string oldText = BuildLines(30, i => "line " + i);
            string newText = BuildLines(30, i => i == 5 ? "changed 5" : "line " + i);

            UnifiedDiffResult result = UnifiedDiffBuilder.Build(oldText, newText, "file.txt");
            DiffDocument parsed = UnifiedDiffParser.Parse(result.Text);

            Assert.Equal(result.AddedCount, parsed.AddedCount);
            Assert.Equal(result.RemovedCount, parsed.RemovedCount);
        }

        [Fact]
        public void ChangesFarApartProduceTwoHunks()
        {
            string oldText = BuildLines(40, i => "line " + i);
            string newText = BuildLines(40, i =>
            {
                if (i == 3)
                {
                    return "changed 3";
                }

                if (i == 36)
                {
                    return "changed 36";
                }

                return "line " + i;
            });

            UnifiedDiffResult result = UnifiedDiffBuilder.Build(oldText, newText, "file.txt");

            int hunks = CountOccurrences(result.Text, "@@ -");
            Assert.Equal(2, hunks);
            Assert.Equal(2, result.AddedCount);
            Assert.Equal(2, result.RemovedCount);
        }

        [Fact]
        public void NearbyChangesAreMergedIntoOneHunk()
        {
            string oldText = BuildLines(20, i => "line " + i);
            string newText = BuildLines(20, i =>
            {
                if (i == 5 || i == 9)
                {
                    return "changed " + i;
                }

                return "line " + i;
            });

            UnifiedDiffResult result = UnifiedDiffBuilder.Build(oldText, newText, "file.txt");

            Assert.Equal(1, CountOccurrences(result.Text, "@@ -"));
        }

        [Fact]
        public void InsertionAtStartUsesZeroOldRange()
        {
            var options = new UnifiedDiffOptions { ContextLines = 0 };
            UnifiedDiffResult result = UnifiedDiffBuilder.Build(
                "a\nb\n",
                "new\na\nb\n",
                "file.txt",
                options);

            Assert.Contains("@@ -0,0 +1,1 @@", result.Text);
            Assert.Equal(1, result.AddedCount);
            Assert.Equal(0, result.RemovedCount);
        }

        [Fact]
        public void DeletionToEmptyFileIsRepresented()
        {
            UnifiedDiffResult result = UnifiedDiffBuilder.Build("a\nb\n", string.Empty, "file.txt");

            Assert.Equal(2, result.RemovedCount);
            Assert.Equal(0, result.AddedCount);
            Assert.Contains("--- a/file.txt", result.Text);
        }

        [Fact]
        public void CrLfAndLfAreTreatedAsSameLines()
        {
            UnifiedDiffResult result = UnifiedDiffBuilder.Build(
                "a\r\nb\r\n",
                "a\nb\n",
                "file.txt");

            Assert.True(result.IsEmpty);
        }

        [Fact]
        public void HugeEditDistanceFallsBackToWholeReplacement()
        {
            string oldText = BuildLines(3000, i => "old " + i);
            string newText = BuildLines(3000, i => "new " + i);

            UnifiedDiffResult result = UnifiedDiffBuilder.Build(oldText, newText, "file.txt");

            Assert.Equal(3000, result.AddedCount);
            Assert.Equal(3000, result.RemovedCount);
            Assert.Contains("--- a/file.txt", result.Text);
        }

        [Fact]
        public void TenThousandLinesWithSmallChangeCompletesQuickly()
        {
            string oldText = BuildLines(10000, i => "line " + i + " with some padding text");
            string newText = BuildLines(10000, i =>
                i == 5000 ? "changed line" : "line " + i + " with some padding text");

            var stopwatch = Stopwatch.StartNew();
            UnifiedDiffResult result = UnifiedDiffBuilder.Build(oldText, newText, "file.txt");
            stopwatch.Stop();

            Assert.Equal(1, result.AddedCount);
            Assert.Equal(1, result.RemovedCount);
            Assert.True(
                stopwatch.ElapsedMilliseconds < 2000,
                "10k 行 diff 耗时 " + stopwatch.ElapsedMilliseconds + "ms");
        }

        [Fact]
        public void InputOverLimitReturnsTruncatedMessage()
        {
            var options = new UnifiedDiffOptions { MaxInputLines = 10 };
            UnifiedDiffResult result = UnifiedDiffBuilder.Build(
                BuildLines(20, i => "a" + i),
                BuildLines(20, i => "b" + i),
                "file.txt",
                options);

            Assert.True(result.Truncated);
            Assert.Contains("文件过大", result.Text);
        }

        [Fact]
        public void OutputCapMarksTruncated()
        {
            var options = new UnifiedDiffOptions { MaxOutputChars = 120 };
            UnifiedDiffResult result = UnifiedDiffBuilder.Build(
                BuildLines(100, i => "old " + i),
                BuildLines(100, i => "new " + i),
                "file.txt",
                options);

            Assert.True(result.Truncated);
            Assert.Contains("diff 已截断", result.Text);
        }

        private static string BuildLines(int count, System.Func<int, string> factory)
        {
            var builder = new StringBuilder();
            for (int i = 0; i < count; i++)
            {
                builder.Append(factory(i)).Append('\n');
            }

            return builder.ToString();
        }

        private static int CountOccurrences(string text, string needle)
        {
            int count = 0;
            int index = 0;
            while ((index = text.IndexOf(needle, index, System.StringComparison.Ordinal)) >= 0)
            {
                count++;
                index += needle.Length;
            }

            return count;
        }
    }
}
