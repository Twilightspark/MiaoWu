using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MiaoWu.Core.Agents.Tools;
using MiaoWu.Infrastructure.Tools;
using Xunit;

namespace MiaoWu.Tests.Tools
{
    public sealed class PresentToolTests : IDisposable
    {
        private readonly string _root;
        private readonly PresentTool _tool = new PresentTool();

        public PresentToolTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "miaowu-present-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(_root, "out"));
            File.WriteAllText(Path.Combine(_root, "out", "report.md"), "# 报告");
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
        public async Task ListsDeliverables()
        {
            ToolResult result = await ExecuteAsync(
                "{\"files\":[{\"path\":\"out/report.md\",\"description\":\"最终报告\"}]}");

            Assert.True(result.IsSuccess);
            Assert.Contains("out/report.md", result.Content);
            Assert.Contains("最终报告", result.Content);
            Assert.Contains("交付", result.Summary);
        }

        [Fact]
        public async Task RejectsMissingFile()
        {
            ToolResult result = await ExecuteAsync("{\"files\":[{\"path\":\"out/missing.md\"}]}");

            Assert.Equal(ToolResultStatus.InvalidArguments, result.Status);
            Assert.Equal("path_invalid", result.ErrorCode);
        }

        [Fact]
        public async Task RejectsEmptyList()
        {
            ToolResult result = await ExecuteAsync("{\"files\":[]}");

            Assert.Equal(ToolResultStatus.InvalidArguments, result.Status);
        }

        private Task<ToolResult> ExecuteAsync(string argumentsJson)
        {
            var context = new ToolContext
            {
                SessionId = "s1",
                WorkspaceId = "w1",
                WorkspaceRoot = _root,
                Sandbox = new WorkspacePathSandbox(_root, new FileSystemPathProbe()),
                Now = DateTimeOffset.UtcNow
            };

            return _tool.ExecuteAsync(
                new ToolCall { Id = "c1", Name = ToolNames.Present, ArgumentsJson = argumentsJson },
                context,
                CancellationToken.None);
        }
    }
}
