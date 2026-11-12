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
        public void ListDirectoryListsDirectoriesBeforeFiles()
        {
            Directory.CreateDirectory(Path.Combine(_workspace, "src"));
            File.WriteAllText(Path.Combine(_workspace, "README.md"), "hello");
            File.WriteAllText(Path.Combine(_workspace, "a.txt"), "hello");

            ToolResult result = Run(new ListDirectoryTool(), "{}");

            Assert.True(result.IsSuccess, result.Content);
            int dirIndex = result.Content.IndexOf("d  src/", StringComparison.Ordinal);
            int fileIndex = result.Content.IndexOf("f  README.md", StringComparison.Ordinal);
            Assert.True(dirIndex >= 0, result.Content);
            Assert.True(fileIndex > dirIndex, result.Content);
            Assert.True(result.IsUntrusted);
        }

        [Fact]
        public void ListDirectoryHonorsDepth()
        {
            Directory.CreateDirectory(Path.Combine(_workspace, "src", "nested"));
            File.WriteAllText(Path.Combine(_workspace, "src", "nested", "app.cs"), "class A {}");

            ToolResult shallow = Run(new ListDirectoryTool(), "{\"depth\":1}");
            Assert.DoesNotContain("nested/app.cs", shallow.Content);

            ToolResult deep = Run(new ListDirectoryTool(), "{\"depth\":3}");
            Assert.Contains("src/nested/app.cs", deep.Content);
        }

        [Fact]
        public void ListDirectoryTruncatesAtLimit()
        {
            for (int i = 0; i < 10; i++)
            {
                File.WriteAllText(Path.Combine(_workspace, "file" + i + ".txt"), "x");
            }

            ToolResult result = Run(new ListDirectoryTool(), "{\"limit\":3}");

            Assert.True(result.Truncated);
            Assert.Contains("已截断", result.Content);
        }

        [Fact]
        public void ListDirectoryRejectsPathOutsideWorkspace()
        {
            ToolResult result = Run(new ListDirectoryTool(), "{\"path\":\"..\\\\..\\\\Windows\"}");

            Assert.Equal(ToolResultStatus.Failed, result.Status);
            Assert.Equal("path_outside_workspace", result.ErrorCode);
        }

        [Fact]
        public void ReadFileReturnsNumberedLines()
        {
            File.WriteAllText(
                Path.Combine(_workspace, "a.txt"),
                "alpha\nbeta\ngamma\n",
                new UTF8Encoding(false));

            ToolResult result = Run(new ReadFileTool(), "{\"path\":\"a.txt\"}");

            Assert.True(result.IsSuccess, result.Content);
            Assert.Contains("1 | alpha", result.Content);
            Assert.Contains("2 | beta", result.Content);
            Assert.Contains("3 | gamma", result.Content);
            Assert.Contains("共 3 行", result.Content);
        }

        [Fact]
        public void ReadFileHonorsLineRange()
        {
            File.WriteAllText(
                Path.Combine(_workspace, "a.txt"),
                "one\ntwo\nthree\nfour\n",
                new UTF8Encoding(false));

            ToolResult result = Run(
                new ReadFileTool(),
                "{\"path\":\"a.txt\",\"startLine\":2,\"maxLines\":2}");

            Assert.True(result.IsSuccess, result.Content);
            Assert.Contains("2 | two", result.Content);
            Assert.Contains("3 | three", result.Content);
            Assert.DoesNotContain("1 | one", result.Content);
            Assert.DoesNotContain("4 | four", result.Content);
            Assert.True(result.Truncated);
        }

        [Fact]
        public void ReadFileRejectsMissingFile()
        {
            ToolResult result = Run(new ReadFileTool(), "{\"path\":\"missing.txt\"}");

            Assert.Equal(ToolResultStatus.Failed, result.Status);
            Assert.Equal("not_found", result.ErrorCode);
        }

        [Fact]
        public void ReadFileRejectsBinary()
        {
            File.WriteAllBytes(Path.Combine(_workspace, "image.dat"), new byte[] { 0, 1, 2, 3, 4 });

            ToolResult result = Run(new ReadFileTool(), "{\"path\":\"image.dat\"}");

            Assert.Equal(ToolResultStatus.Failed, result.Status);
            Assert.Equal("binary_file", result.ErrorCode);
        }

        [Fact]
        public void ReadFileRejectsOversizedFile()
        {
            string path = Path.Combine(_workspace, "big.txt");
            using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write))
            {
                stream.SetLength(ToolText.MaxFileBytes + 1);
            }

            ToolResult result = Run(new ReadFileTool(), "{\"path\":\"big.txt\"}");

            Assert.Equal(ToolResultStatus.Failed, result.Status);
            Assert.Equal("file_too_large", result.ErrorCode);
        }

        [Fact]
        public void ReadFileDecodesGbk()
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

            ToolResult result = Run(new ReadFileTool(), "{\"path\":\"gbk.txt\"}");

            Assert.True(result.IsSuccess, result.Content);
            Assert.Contains("中文内容", result.Content);
            Assert.Contains("GBK", result.Content);
        }

        [Fact]
        public void ReadFileRejectsTraversal()
        {
            ToolResult result = Run(
                new ReadFileTool(),
                "{\"path\":\"..\\\\..\\\\Windows\\\\win.ini\"}");

            Assert.Equal(ToolResultStatus.Failed, result.Status);
            Assert.Equal("path_outside_workspace", result.ErrorCode);
        }

        [Fact]
        public void MissingArgumentsReturnInvalidArguments()
        {
            ToolResult result = Run(new ReadFileTool(), "{}");

            Assert.Equal(ToolResultStatus.InvalidArguments, result.Status);
            Assert.Equal("missing_argument", result.ErrorCode);
        }

        [Fact]
        public void MalformedArgumentsReturnInvalidArguments()
        {
            ToolResult result = Run(new ReadFileTool(), "{\"path\": ");

            Assert.Equal(ToolResultStatus.InvalidArguments, result.Status);
            Assert.Equal("invalid_arguments", result.ErrorCode);
        }

        [Fact]
        public void SearchFindsMatchesAcrossFiles()
        {
            File.WriteAllText(Path.Combine(_workspace, "a.txt"), "TODO first\nnothing\n", new UTF8Encoding(false));
            Directory.CreateDirectory(Path.Combine(_workspace, "src"));
            File.WriteAllText(Path.Combine(_workspace, "src", "b.cs"), "class B { } // TODO second\n", new UTF8Encoding(false));

            ToolResult result = Run(new SearchFilesTool(), "{\"query\":\"TODO\"}");

            Assert.True(result.IsSuccess, result.Content);
            Assert.Contains("a.txt:1:", result.Content);
            Assert.Contains("src/b.cs:1:", result.Content);
        }

        [Fact]
        public void SearchHonorsIncludePattern()
        {
            File.WriteAllText(Path.Combine(_workspace, "a.txt"), "needle", new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(_workspace, "b.cs"), "needle", new UTF8Encoding(false));

            ToolResult onlyText = Run(
                new SearchFilesTool(),
                "{\"query\":\"needle\",\"include\":\"*.txt\"}");

            Assert.Contains("a.txt", onlyText.Content);
            Assert.DoesNotContain("b.cs", onlyText.Content);
        }

        [Fact]
        public void SearchRespectsCaseSensitivity()
        {
            File.WriteAllText(Path.Combine(_workspace, "a.txt"), "Needle\nneedle", new UTF8Encoding(false));

            ToolResult insensitive = Run(new SearchFilesTool(), "{\"query\":\"needle\"}");
            ToolResult sensitive = Run(
                new SearchFilesTool(),
                "{\"query\":\"needle\",\"caseSensitive\":true}");

            Assert.Equal(2, CountOccurrences(insensitive.Content, "a.txt:"));
            Assert.Equal(1, CountOccurrences(sensitive.Content, "a.txt:"));
        }

        [Fact]
        public void SearchSkipsBuildDirectoriesAndAgentSessions()
        {
            Directory.CreateDirectory(Path.Combine(_workspace, "bin"));
            File.WriteAllText(Path.Combine(_workspace, "bin", "gen.cs"), "needle in bin");
            Directory.CreateDirectory(Path.Combine(_workspace, ".agent", "sessions"));
            File.WriteAllText(Path.Combine(_workspace, ".agent", "sessions", "old.jsonl"), "needle in session");
            File.WriteAllText(Path.Combine(_workspace, "src.cs"), "needle in source");

            ToolResult result = Run(new SearchFilesTool(), "{\"query\":\"needle\"}");

            Assert.Contains("src.cs", result.Content);
            Assert.DoesNotContain("gen.cs", result.Content);
            Assert.DoesNotContain("old.jsonl", result.Content);
        }

        [Fact]
        public void SearchTruncatesAtMaxResults()
        {
            var builder = new StringBuilder();
            for (int i = 0; i < 20; i++)
            {
                builder.Append("needle line ").Append(i).Append('\n');
            }

            File.WriteAllText(Path.Combine(_workspace, "many.txt"), builder.ToString(), new UTF8Encoding(false));

            ToolResult result = Run(
                new SearchFilesTool(),
                "{\"query\":\"needle\",\"maxResults\":5}");

            Assert.Equal(5, CountOccurrences(result.Content, "many.txt:"));
            Assert.True(result.Truncated);
        }

        [Fact]
        public void EmptySearchReturnsReadableMessage()
        {
            File.WriteAllText(Path.Combine(_workspace, "a.txt"), "nothing here");

            ToolResult result = Run(new SearchFilesTool(), "{\"query\":\"zzz\"}");

            Assert.True(result.IsSuccess);
            Assert.Contains("没有找到", result.Content);
        }

        [Fact]
        public void CancellationIsObserved()
        {
            File.WriteAllText(Path.Combine(_workspace, "a.txt"), "hello");
            var cts = new CancellationTokenSource();
            cts.Cancel();
            var tool = new ReadFileTool();

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
