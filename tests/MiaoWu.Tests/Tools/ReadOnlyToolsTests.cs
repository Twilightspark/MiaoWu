using System;
using System.IO;
using System.Text;
using System.Threading;
using MiaoWu.Core.Agents.Tools;
using MiaoWu.Infrastructure.Tools;
using Xunit;

namespace MiaoWu.Tests.Tools
{
    public sealed class ReadOnlyToolsTests : IDisposable
    {
        private readonly string _workspace;
        private readonly ToolContext _context;

        public ReadOnlyToolsTests()
        {
            _workspace = Path.Combine(
                Path.GetTempPath(),
                "miaowu-tools-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_workspace);

            _context = new ToolContext
            {
                SessionId = "s1",
                WorkspaceId = "w1",
                WorkspaceRoot = _workspace,
                Sandbox = new WorkspacePathSandbox(_workspace, FileSystemPathProbe.Instance),
                CallId = "call-1",
                Now = DateTimeOffset.UtcNow
            };
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_workspace))
                {
                    Directory.Delete(_workspace, true);
                }
            }
            catch (IOException)
            {
                // 临时目录清理失败不影响测试结论。
            }
        }

        [Fact]
        public void ReadReturnsNumberedLines()
        {
            File.WriteAllText(
                Path.Combine(_workspace, "a.txt"),
                "alpha\nbeta\ngamma\n",
                new UTF8Encoding(false));

            ToolResult result = Run(new ReadTool(), "{\"path\":\"a.txt\"}");

            Assert.True(result.IsSuccess, result.Content);
            Assert.Contains("1 | alpha", result.Content);
            Assert.Contains("2 | beta", result.Content);
            Assert.Contains("3 | gamma", result.Content);
            Assert.Contains("共 3 行", result.Content);
        }

        [Fact]
        public void ReadHonorsOffsetAndLimit()
        {
            File.WriteAllText(
                Path.Combine(_workspace, "a.txt"),
                "one\ntwo\nthree\nfour\n",
                new UTF8Encoding(false));

            ToolResult result = Run(
                new ReadTool(),
                "{\"path\":\"a.txt\",\"offset\":2,\"limit\":2}");

            Assert.True(result.IsSuccess, result.Content);
            Assert.Contains("2 | two", result.Content);
            Assert.Contains("3 | three", result.Content);
            Assert.DoesNotContain("1 | one", result.Content);
            Assert.DoesNotContain("4 | four", result.Content);
            Assert.True(result.Truncated);
        }

        [Fact]
        public void ReadClampsLimitTo2000Lines()
        {
            var builder = new StringBuilder();
            for (int i = 0; i < 2100; i++)
            {
                builder.Append("line ").Append(i).Append('\n');
            }

            File.WriteAllText(Path.Combine(_workspace, "long.txt"), builder.ToString(), new UTF8Encoding(false));

            ToolResult result = Run(new ReadTool(), "{\"path\":\"long.txt\",\"limit\":5000}");

            Assert.True(result.IsSuccess, result.Content);
            Assert.True(result.Truncated);
            Assert.Contains("1 | line 0", result.Content);
            Assert.Contains("2000 | line 1999", result.Content);
            Assert.DoesNotContain("2001 | line 2000", result.Content);
        }

        [Fact]
        public void ReadReportsOffsetBeyondEnd()
        {
            File.WriteAllText(Path.Combine(_workspace, "a.txt"), "only\n", new UTF8Encoding(false));

            ToolResult result = Run(new ReadTool(), "{\"path\":\"a.txt\",\"offset\":10}");

            Assert.True(result.IsSuccess, result.Content);
            Assert.Contains("超出范围", result.Content);
        }

        [Fact]
        public void ReadRejectsMissingFile()
        {
            ToolResult result = Run(new ReadTool(), "{\"path\":\"missing.txt\"}");

            Assert.Equal(ToolResultStatus.Failed, result.Status);
            Assert.Equal("not_found", result.ErrorCode);
        }

        [Fact]
        public void ReadRejectsBinary()
        {
            File.WriteAllBytes(Path.Combine(_workspace, "image.dat"), new byte[] { 0, 1, 2, 3, 4 });

            ToolResult result = Run(new ReadTool(), "{\"path\":\"image.dat\"}");

            Assert.Equal(ToolResultStatus.Failed, result.Status);
            Assert.Equal("binary_file", result.ErrorCode);
        }

        [Fact]
        public void ReadRejectsOversizedFile()
        {
            string path = Path.Combine(_workspace, "big.txt");
            using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write))
            {
                stream.SetLength(ToolText.MaxFileBytes + 1);
            }

            ToolResult result = Run(new ReadTool(), "{\"path\":\"big.txt\"}");

            Assert.Equal(ToolResultStatus.Failed, result.Status);
            Assert.Equal("file_too_large", result.ErrorCode);
        }

        [Fact]
        public void ReadDecodesGbk()
        {
            Encoding gbk;
            try
            {
                gbk = Encoding.GetEncoding(936);
            }
            catch (ArgumentException)
            {
                return;
            }

            File.WriteAllBytes(
                Path.Combine(_workspace, "gbk.txt"),
                gbk.GetBytes("中文内容\r\n第二行\r\n"));

            ToolResult result = Run(new ReadTool(), "{\"path\":\"gbk.txt\"}");

            Assert.True(result.IsSuccess, result.Content);
            Assert.Contains("中文内容", result.Content);
            Assert.Contains("GBK", result.Content);
        }

        [Fact]
        public void ReadRejectsTraversal()
        {
            ToolResult result = Run(
                new ReadTool(),
                "{\"path\":\"..\\\\..\\\\Windows\\\\win.ini\"}");

            Assert.Equal(ToolResultStatus.Failed, result.Status);
            Assert.Equal("path_outside_workspace", result.ErrorCode);
        }

        [Fact]
        public void MissingArgumentsReturnInvalidArguments()
        {
            ToolResult result = Run(new ReadTool(), "{}");

            Assert.Equal(ToolResultStatus.InvalidArguments, result.Status);
            Assert.Equal("missing_argument", result.ErrorCode);
        }

        [Fact]
        public void MalformedArgumentsReturnInvalidArguments()
        {
            ToolResult result = Run(new ReadTool(), "{\"path\": ");

            Assert.Equal(ToolResultStatus.InvalidArguments, result.Status);
            Assert.Equal("invalid_arguments", result.ErrorCode);
        }

        [Fact]
        public void GrepFindsMatchesAcrossFiles()
        {
            File.WriteAllText(Path.Combine(_workspace, "a.txt"), "TODO first\nnothing\n", new UTF8Encoding(false));
            Directory.CreateDirectory(Path.Combine(_workspace, "src"));
            File.WriteAllText(Path.Combine(_workspace, "src", "b.cs"), "class B { } // TODO second\n", new UTF8Encoding(false));

            ToolResult result = Run(new GrepTool(), "{\"pattern\":\"TODO\"}");

            Assert.True(result.IsSuccess, result.Content);
            Assert.Contains("a.txt:1:", result.Content);
            Assert.Contains("src/b.cs:1:", result.Content);
        }

        [Fact]
        public void GrepTreatsPatternAsRegularExpression()
        {
            File.WriteAllText(
                Path.Combine(_workspace, "a.txt"),
                "TODO1\nTODO2\nTODONT\n",
                new UTF8Encoding(false));

            ToolResult result = Run(new GrepTool(), "{\"pattern\":\"TODO[0-9]$\"}");

            Assert.True(result.IsSuccess, result.Content);
            Assert.Contains("a.txt:1:", result.Content);
            Assert.Contains("a.txt:2:", result.Content);
            Assert.Equal(2, CountOccurrences(result.Content, "a.txt:"));
        }

        [Fact]
        public void GrepRejectsInvalidRegularExpression()
        {
            ToolResult result = Run(new GrepTool(), "{\"pattern\":\"[unclosed\"}");

            Assert.Equal(ToolResultStatus.InvalidArguments, result.Status);
            Assert.Equal("invalid_pattern", result.ErrorCode);
        }

        [Fact]
        public void GrepHonorsIncludePattern()
        {
            File.WriteAllText(Path.Combine(_workspace, "a.txt"), "needle", new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(_workspace, "b.cs"), "needle", new UTF8Encoding(false));

            ToolResult onlyText = Run(
                new GrepTool(),
                "{\"pattern\":\"needle\",\"include\":\"*.txt\"}");

            Assert.Contains("a.txt", onlyText.Content);
            Assert.DoesNotContain("b.cs", onlyText.Content);
        }

        [Fact]
        public void GrepRespectsCaseSensitivity()
        {
            File.WriteAllText(Path.Combine(_workspace, "a.txt"), "Needle\nneedle", new UTF8Encoding(false));

            ToolResult insensitive = Run(new GrepTool(), "{\"pattern\":\"needle\"}");
            ToolResult sensitive = Run(
                new GrepTool(),
                "{\"pattern\":\"needle\",\"caseSensitive\":true}");

            Assert.Equal(2, CountOccurrences(insensitive.Content, "a.txt:"));
            Assert.Equal(1, CountOccurrences(sensitive.Content, "a.txt:"));
        }

        [Fact]
        public void GrepSkipsBuildDirectoriesAndAgentSessions()
        {
            Directory.CreateDirectory(Path.Combine(_workspace, "bin"));
            File.WriteAllText(Path.Combine(_workspace, "bin", "gen.cs"), "needle in bin");
            Directory.CreateDirectory(Path.Combine(_workspace, ".agent", "sessions"));
            File.WriteAllText(Path.Combine(_workspace, ".agent", "sessions", "old.jsonl"), "needle in session");
            File.WriteAllText(Path.Combine(_workspace, "src.cs"), "needle in source");

            ToolResult result = Run(new GrepTool(), "{\"pattern\":\"needle\"}");

            Assert.Contains("src.cs", result.Content);
            Assert.DoesNotContain("gen.cs", result.Content);
            Assert.DoesNotContain("old.jsonl", result.Content);
        }

        [Fact]
        public void GrepTruncatesAtLimit()
        {
            var builder = new StringBuilder();
            for (int i = 0; i < 20; i++)
            {
                builder.Append("needle line ").Append(i).Append('\n');
            }

            File.WriteAllText(Path.Combine(_workspace, "many.txt"), builder.ToString(), new UTF8Encoding(false));

            ToolResult result = Run(
                new GrepTool(),
                "{\"pattern\":\"needle\",\"limit\":5}");

            Assert.Equal(5, CountOccurrences(result.Content, "many.txt:"));
            Assert.True(result.Truncated);
        }

        [Fact]
        public void EmptyGrepReturnsReadableMessage()
        {
            File.WriteAllText(Path.Combine(_workspace, "a.txt"), "nothing here");

            ToolResult result = Run(new GrepTool(), "{\"pattern\":\"zzz\"}");

            Assert.True(result.IsSuccess);
            Assert.Contains("没有找到", result.Content);
        }

        [Fact]
        public void GlobMatchesNestedPattern()
        {
            Directory.CreateDirectory(Path.Combine(_workspace, "src", "nested"));
            File.WriteAllText(Path.Combine(_workspace, "src", "nested", "app.cs"), "class A {}");
            File.WriteAllText(Path.Combine(_workspace, "src", "top.cs"), "class T {}");
            File.WriteAllText(Path.Combine(_workspace, "readme.md"), "docs");

            ToolResult result = Run(new GlobTool(), "{\"pattern\":\"**/*.cs\"}");

            Assert.True(result.IsSuccess, result.Content);
            Assert.True(result.IsUntrusted);
            Assert.Contains("src/top.cs", result.Content);
            Assert.Contains("src/nested/app.cs", result.Content);
            Assert.DoesNotContain("readme.md", result.Content);
        }

        [Fact]
        public void GlobWithoutSeparatorMatchesFileName()
        {
            Directory.CreateDirectory(Path.Combine(_workspace, "src"));
            File.WriteAllText(Path.Combine(_workspace, "root.txt"), "a");
            File.WriteAllText(Path.Combine(_workspace, "src", "deep.txt"), "b");
            File.WriteAllText(Path.Combine(_workspace, "src", "deep.cs"), "c");

            ToolResult result = Run(new GlobTool(), "{\"pattern\":\"*.txt\"}");

            Assert.True(result.IsSuccess, result.Content);
            Assert.Contains("root.txt", result.Content);
            Assert.Contains("src/deep.txt", result.Content);
            Assert.DoesNotContain("deep.cs", result.Content);
        }

        [Fact]
        public void GlobSkipsExcludedDirectories()
        {
            Directory.CreateDirectory(Path.Combine(_workspace, "bin"));
            File.WriteAllText(Path.Combine(_workspace, "bin", "gen.cs"), "x");
            Directory.CreateDirectory(Path.Combine(_workspace, "node_modules", "pkg"));
            File.WriteAllText(Path.Combine(_workspace, "node_modules", "pkg", "index.js"), "x");
            Directory.CreateDirectory(Path.Combine(_workspace, ".agent", "cache"));
            File.WriteAllText(Path.Combine(_workspace, ".agent", "cache", "x.cs"), "x");
            File.WriteAllText(Path.Combine(_workspace, "keep.cs"), "x");

            ToolResult result = Run(new GlobTool(), "{\"pattern\":\"**/*.cs\"}");

            Assert.Contains("keep.cs", result.Content);
            Assert.DoesNotContain("gen.cs", result.Content);
            Assert.DoesNotContain("index.js", result.Content);
            Assert.DoesNotContain(".agent", result.Content);
        }

        [Fact]
        public void GlobTruncatesAtLimit()
        {
            for (int i = 0; i < 10; i++)
            {
                File.WriteAllText(Path.Combine(_workspace, "file" + i + ".txt"), "x");
            }

            ToolResult result = Run(new GlobTool(), "{\"pattern\":\"*.txt\",\"limit\":3}");

            Assert.True(result.IsSuccess, result.Content);
            Assert.True(result.Truncated);
            Assert.Contains("file0.txt", result.Content);
            Assert.Contains("file1.txt", result.Content);
            Assert.Contains("file2.txt", result.Content);
            Assert.DoesNotContain("file3.txt", result.Content);
            Assert.Contains("已截断", result.Content);
        }

        [Fact]
        public void GlobRejectsPathOutsideWorkspace()
        {
            ToolResult result = Run(new GlobTool(), "{\"pattern\":\"*.cs\",\"path\":\"..\\\\..\\\\Windows\"}");

            Assert.Equal(ToolResultStatus.Failed, result.Status);
            Assert.Equal("path_outside_workspace", result.ErrorCode);
        }

        [Fact]
        public void GlobReturnsReadableMessageWhenNothingMatches()
        {
            File.WriteAllText(Path.Combine(_workspace, "a.txt"), "x");

            ToolResult result = Run(new GlobTool(), "{\"pattern\":\"*.php\"}");

            Assert.True(result.IsSuccess);
            Assert.Contains("没有匹配的文件", result.Content);
        }

        [Fact]
        public void CancellationIsObserved()
        {
            File.WriteAllText(Path.Combine(_workspace, "a.txt"), "hello");
            var cts = new CancellationTokenSource();
            cts.Cancel();
            var tool = new ReadTool();

            ToolResult result = tool.ExecuteAsync(
                new ToolCall { Id = "c1", Name = tool.Name, ArgumentsJson = "{\"path\":\"a.txt\"}" },
                _context,
                cts.Token).GetAwaiter().GetResult();

            Assert.Equal(ToolResultStatus.Cancelled, result.Status);
        }

        private ToolResult Run(IAgentTool tool, string argumentsJson)
        {
            return tool.ExecuteAsync(
                new ToolCall { Id = "c1", Name = tool.Name, ArgumentsJson = argumentsJson },
                _context,
                CancellationToken.None).GetAwaiter().GetResult();
        }

        private static int CountOccurrences(string text, string needle)
        {
            int count = 0;
            int index = 0;
            while ((index = text.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
            {
                count++;
                index += needle.Length;
            }

            return count;
        }
    }
}
