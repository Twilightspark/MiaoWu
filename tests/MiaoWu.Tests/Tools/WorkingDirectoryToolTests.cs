using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MiaoWu.Core.Agents.Tools;
using MiaoWu.Infrastructure.Tools;
using Xunit;

namespace MiaoWu.Tests.Tools
{
    public sealed class WorkingDirectoryToolTests : IDisposable
    {
        private readonly string _root;
        private readonly WorkingDirectoryTool _tool = new WorkingDirectoryTool();
        private readonly FakeWorkingDirectory _workingDirectory;

        public WorkingDirectoryToolTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "miaowu-cwd-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(_root, "src"));
            _workingDirectory = new FakeWorkingDirectory(_root);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_root))
                {
                    Directory.Delete(_root, true);
                }
            }
            catch (Exception)
            {
            }
        }

        [Fact]
        public async Task ReturnsCurrentDirectoryWhenPathIsOmitted()
        {
            ToolResult result = await ExecuteAsync(null);

            Assert.True(result.IsSuccess);
            Assert.Contains("当前工作目录：.", result.Content);
            Assert.Contains(_root, result.Content);
        }

        [Fact]
        public async Task SwitchesToExistingSubdirectory()
        {
            ToolResult result = await ExecuteAsync("{\"path\":\"src\"}");

            Assert.True(result.IsSuccess);
            Assert.Equal(Path.Combine(_root, "src"), _workingDirectory.Current);
            Assert.Contains("src", result.Summary);
        }

        [Fact]
        public async Task RejectsPathOutsideWorkspace()
        {
            ToolResult result = await ExecuteAsync("{\"path\":\"..\"}");

            Assert.Equal(ToolResultStatus.InvalidArguments, result.Status);
            Assert.Equal("path_invalid", result.ErrorCode);
            Assert.Equal(_root, _workingDirectory.Current);
        }

        [Fact]
        public async Task RejectsMissingDirectory()
        {
            ToolResult result = await ExecuteAsync("{\"path\":\"nope\"}");

            Assert.Equal(ToolResultStatus.InvalidArguments, result.Status);
            Assert.Equal("path_invalid", result.ErrorCode);
            Assert.Equal(_root, _workingDirectory.Current);
        }

        private Task<ToolResult> ExecuteAsync(string argumentsJson)
        {
            var context = new ToolContext
            {
                SessionId = "s1",
                WorkspaceId = "w1",
                WorkspaceRoot = _root,
                Sandbox = new WorkspacePathSandbox(_root, new FileSystemPathProbe()),
                WorkingDirectory = _workingDirectory,
                Now = DateTimeOffset.UtcNow
            };

            return _tool.ExecuteAsync(
                new ToolCall
                {
                    Id = "call-1",
                    Name = ToolNames.WorkingDirectory,
                    ArgumentsJson = argumentsJson
                },
                context,
                CancellationToken.None);
        }

        private sealed class FakeWorkingDirectory : ISessionWorkingDirectory
        {
            public FakeWorkingDirectory(string current)
            {
                Current = current;
            }

            public string Current { get; private set; }

            public bool TrySet(string absolutePath)
            {
                if (string.IsNullOrWhiteSpace(absolutePath) || !Directory.Exists(absolutePath))
                {
                    return false;
                }

                Current = absolutePath;
                return true;
            }
        }
    }
}
