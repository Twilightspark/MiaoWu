using System;
using System.IO;
using MiaoWu.Core.Agents.Tools;
using MiaoWu.Core.Errors;
using MiaoWu.Infrastructure.Providers;
using MiaoWu.Infrastructure.Tools;
using Xunit;

namespace MiaoWu.Tests.Tools
{
    public sealed class ToolOutputBudgetTests
    {
        [Fact]
        public void HeadKeepsFirstLines()
        {
            string text = BuildLines(2500);
            ToolOutputTruncation result = ToolOutputBudget.Apply(text, false, null);

            Assert.True(result.Truncated);
            Assert.Equal("lines", result.TruncatedBy);
            Assert.Equal(2000, result.OutputLines);
        }

        [Fact]
        public void TailKeepsLastLines()
        {
            string text = BuildLines(2500);
            ToolOutputTruncation result = ToolOutputBudget.Apply(text, true, null);

            Assert.True(result.Truncated);
            Assert.Contains("line-2500", result.Content);
            Assert.DoesNotContain("line-1\n", result.Content);
        }

        [Fact]
        public void SpillWritesUnderWorkspaceAgentCache()
        {
            string workspace = Path.Combine(Path.GetTempPath(), "miaowu-spill-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(workspace);
                string content = BuildLines(2500);
                string path = ToolOutputBudget.Save(workspace, "s1", "c1", content);

                Assert.NotNull(path);
                Assert.True(File.Exists(path));
                Assert.Contains(Path.Combine(".agent", "cache", "tool-output"), path);
                Assert.Contains("line-2500", File.ReadAllText(path));
            }
            finally
            {
                try
                {
                    if (Directory.Exists(workspace))
                    {
                        Directory.Delete(workspace, true);
                    }
                }
                catch (Exception)
                {
                }
            }
        }

        private static string BuildLines(int count)
        {
            var builder = new System.Text.StringBuilder();
            for (int i = 1; i <= count; i++)
            {
                builder.Append("line-").Append(i).Append('\n');
            }

            return builder.ToString();
        }
    }

    public sealed class AgentErrorClassifierTests
    {
        [Theory]
        [InlineData(401, AgentErrorKind.Auth)]
        [InlineData(403, AgentErrorKind.Permission)]
        [InlineData(404, AgentErrorKind.NotFound)]
        [InlineData(429, AgentErrorKind.RateLimited)]
        [InlineData(500, AgentErrorKind.Server)]
        public void MapsHttpStatus(int status, AgentErrorKind kind)
        {
            AgentError error = AgentErrorClassifier.FromHttp(status, "{\"error\":{\"message\":\"x\"}}", null);
            Assert.Equal(kind, error.Kind);
        }

        [Fact]
        public void MapsContextOverflowFrom400Body()
        {
            AgentError error = AgentErrorClassifier.FromHttp(
                400,
                "{\"error\":{\"code\":\"context_length_exceeded\",\"message\":\"too many tokens\"}}",
                "req1");

            Assert.Equal(AgentErrorKind.ContextOverflow, error.Kind);
            Assert.Equal("req1", error.RequestId);
        }

        [Fact]
        public void MapsTlsAndTimeoutExceptions()
        {
            var classifier = new AgentErrorClassifier();
            Assert.Equal(
                AgentErrorKind.Timeout,
                classifier.Classify(new TimeoutException("timeout")).Kind);
            Assert.Equal(
                AgentErrorKind.Tls,
                classifier.Classify(new InvalidOperationException("SSL handshake failed")).Kind);
        }
    }
}
